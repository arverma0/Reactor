using System.Diagnostics;
using System.Globalization;
using System.Text;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Handlers;

/// <summary>
/// Parallel fan-out (spec 4.8): runs every outgoing branch concurrently (bounded by <c>max_parallel</c>, default 4), each
/// on an isolated clone of the context, until each branch reaches a join point (fan-in node, exit, or dead end).
/// Results are stored in <c>parallel.results</c>; branch context changes are not merged back, except artifact records
/// (<c>artifacts.*</c>, a Reactor extension; see <see cref="StageArtifacts.MergeBranchRecords"/>), which are merged in branch
/// declaration order from every branch that ran, failed ones included. The engine then continues at the branches' common
/// join node. Join policies: <c>wait_all</c> (default; SUCCESS when no branch failed, else PARTIAL_SUCCESS) and
/// <c>first_success</c> (SUCCESS as soon as one branch succeeds; the rest are cancelled and contribute no artifacts).
/// </summary>
public sealed class ParallelHandler : INodeHandler
{
    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);
        var run = context.Run ?? throw new InvalidOperationException("The parallel handler requires an engine run (PipelineContext.Run).");

        var branches = graph.OutgoingEdges(node.Id);
        if (branches.Count == 0)
        {
            return Outcome.Fail("Parallel node has no outgoing branches.");
        }

        var joinPolicy = (node[AttrKeys.JoinPolicy] ?? "wait_all").Trim().ToLowerInvariant();
        if (joinPolicy is not ("wait_all" or "first_success"))
        {
            return Outcome.Fail($"Unknown join_policy '{joinPolicy}'; expected wait_all or first_success.");
        }

        var maxParallel = Math.Max(1, AttributeValues.ParseInt(node[AttrKeys.MaxParallel]) ?? run.RunnerOptions.DefaultMaxParallel);
        var forkValues = context.Snapshot();
        var stopwatch = Stopwatch.StartNew();
        run.Events.Emit(new ParallelStarted(node.Id, branches.Count));

        using var gate = new SemaphoreSlim(maxParallel, maxParallel);
        using var firstSuccessCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var results = new BranchResult[branches.Count];

        var tasks = branches.Select((edge, index) => Task.Run(async () =>
        {
            await gate.WaitAsync(firstSuccessCts.Token).ConfigureAwait(false);
            try
            {
                run.Events.Emit(new ParallelBranchStarted(node.Id, edge.To, index));
                BranchResult result;
                try
                {
                    result = await run.BranchExecutor.ExecuteBranchAsync(edge.To, context.Clone(), firstSuccessCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (firstSuccessCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    result = new BranchResult { BranchId = edge.To, Status = StageStatus.Skipped, Notes = "Cancelled after another branch succeeded (first_success)." };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new BranchResult { BranchId = edge.To, Status = StageStatus.Fail, FailureReason = ex.Message };
                }

                results[index] = result;
                run.Events.Emit(new ParallelBranchCompleted(node.Id, edge.To, index, result.Duration, result.Status.IsSuccessful()));
                if (joinPolicy == "first_success" && result.Status.IsSuccessful())
                {
                    await firstSuccessCts.CancelAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }, CancellationToken.None)).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (firstSuccessCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Branches still waiting for a slot were cancelled by first_success.
        }

        for (var i = 0; i < results.Length; i++)
        {
            results[i] ??= new BranchResult { BranchId = branches[i].To, Status = StageStatus.Skipped, Notes = "Not started (first_success already satisfied)." };
        }

        var successCount = results.Count(r => r.Status == StageStatus.Success || r.Status == StageStatus.PartialSuccess);
        var failCount = results.Count(r => r.Status == StageStatus.Fail);
        run.Events.Emit(new ParallelCompleted(node.Id, stopwatch.Elapsed, successCount, failCount));

        var join = DetermineJoin(results, graph);
        var serialized = results.Select(Serialize).ToList();
        var updates = new Dictionary<string, object?>
        {
            ["parallel.results"] = serialized,
            ["parallel.branch_count"] = results.Length,
            ["parallel.success_count"] = successCount,
            ["parallel.fail_count"] = failCount,
            ["parallel.join_node"] = join ?? "",
            ["parallel.source_node"] = node.Id,
        };

        // Reactor extension: artifact records are the one part of branch contexts merged back (in branch declaration order).
        foreach (var (key, value) in StageArtifacts.MergeBranchRecords(forkValues, results.Select(r => r.Context)))
        {
            updates[key] = value;
        }

        var summary = Summarize(results);
        Outcome outcome = joinPolicy switch
        {
            "first_success" when successCount > 0 => Outcome.Success(summary, updates),
            "first_success" => Outcome.Fail("No branch succeeded (first_success). " + summary, updates),
            _ when failCount == 0 => Outcome.Success(summary, updates),
            _ => Outcome.PartialSuccess(summary, updates),
        };

        if (join is null)
        {
            return outcome.Status == StageStatus.Fail
                ? outcome
                : Outcome.Fail("Parallel branches did not converge on a join node (fan-in or exit). " + summary, updates);
        }

        // Continue at the join even when branches failed: the fan-in (and its failure routing) decides what happens next.
        return outcome with { JumpTo = join };
    }

    /// <summary>The node where branches converge: the most common stop node, preferring fan-in nodes.</summary>
    internal static string? DetermineJoin(IReadOnlyList<BranchResult> results, Graph graph)
    {
        var stops = results.Where(r => r.StopNodeId is not null).Select(r => r.StopNodeId!).ToList();
        if (stops.Count == 0)
        {
            return null;
        }

        return stops
            .GroupBy(s => s, StringComparer.Ordinal)
            .OrderByDescending(g => graph.GetNode(g.Key) is { } n && HandlerTypes.Resolve(n) == HandlerTypes.FanIn)
            .ThenByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First().Key;
    }

    private static Dictionary<string, object?> Serialize(BranchResult result) => new()
    {
        ["id"] = result.BranchId,
        ["status"] = result.Status.ToWire(),
        ["stop_node"] = result.StopNodeId,
        ["notes"] = result.Notes,
        ["failure_reason"] = result.FailureReason,
        ["completed_nodes"] = result.CompletedNodes.ToList<object?>(),
        ["last_stage"] = result.Context.GetValueOrDefault("last_stage"),
        ["last_response"] = result.Context.GetValueOrDefault("last_response"),
        ["score"] = Score(result.Context),
        ["duration_ms"] = (long)result.Duration.TotalMilliseconds,
    };

    private static double Score(IReadOnlyDictionary<string, object?> context)
    {
        foreach (var key in new[] { "score", "parallel.score", "context.score" })
        {
            if (context.TryGetValue(key, out var value) && value is not null &&
                double.TryParse(ContextValues.ToDisplayString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
            {
                return score;
            }
        }

        return 0;
    }

    private static string Summarize(IReadOnlyList<BranchResult> results)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{results.Count} branch(es): ");
        sb.AppendJoin(", ", results.Select(r => $"{r.BranchId}={r.Status.ToWire()}"));
        return sb.ToString();
    }
}

/// <summary>
/// Fan-in (spec 4.9): consolidates <c>parallel.results</c> and selects the best candidate — by asking the LLM backend when
/// the node has a <c>prompt</c>, otherwise heuristically (outcome rank, then score, then id). Fails only when no results
/// exist or every candidate failed.
/// </summary>
public sealed class FanInHandler : INodeHandler
{
    private sealed record Candidate(string Id, StageStatus Status, double Score, string Notes, string FailureReason, string LastResponse);

    public async Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(graph);

        var candidates = ReadCandidates(context.Get("parallel.results"));
        if (candidates.Count == 0)
        {
            return Outcome.Fail("No parallel results to evaluate");
        }

        if (candidates.All(c => c.Status == StageStatus.Fail))
        {
            return Outcome.Fail("All parallel candidates failed: " + string.Join("; ", candidates.Select(c => $"{c.Id}: {c.FailureReason}")),
                new Dictionary<string, object?> { ["parallel.fan_in.best_id"] = "", ["parallel.fan_in.best_outcome"] = "fail" });
        }

        Candidate best;
        string method;
        var backend = context.Run?.Backend;
        if (!string.IsNullOrWhiteSpace(node.Prompt) && backend is not null)
        {
            var chosen = await EvaluateWithLlmAsync(node, context, graph, logsRoot, backend, candidates, cancellationToken).ConfigureAwait(false);
            best = chosen ?? HeuristicSelect(candidates);
            method = chosen is null ? "heuristic (LLM choice unparseable)" : "llm";
        }
        else
        {
            best = HeuristicSelect(candidates);
            method = "heuristic";
        }

        return Outcome.Success($"Selected best candidate: {best.Id} ({method})", new Dictionary<string, object?>
        {
            ["parallel.fan_in.best_id"] = best.Id,
            ["parallel.fan_in.best_outcome"] = best.Status.ToWire(),
            ["parallel.fan_in.best_notes"] = best.Notes,
            ["parallel.fan_in.best_response"] = best.LastResponse,
            ["parallel.fan_in.method"] = method,
        });
    }

    private static Candidate HeuristicSelect(IReadOnlyList<Candidate> candidates) =>
        candidates
            .OrderBy(c => Rank(c.Status))
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .First();

    private static int Rank(StageStatus status) => status switch
    {
        StageStatus.Success => 0,
        StageStatus.PartialSuccess => 1,
        StageStatus.Retry => 2,
        StageStatus.Fail => 3,
        _ => 4,
    };

    private static async Task<Candidate?> EvaluateWithLlmAsync(
        Node node,
        PipelineContext context,
        Graph graph,
        string logsRoot,
        ICodergenBackend backend,
        IReadOnlyList<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Transforms.VariableExpansionTransform.Expand(node.Prompt, graph.Goal));
        sb.AppendLine();
        sb.AppendLine("## Candidates");
        foreach (var c in candidates)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {c.Id}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- status: {c.Status.ToWire()}");
            if (c.Score != 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- score: {c.Score}");
            }

            if (c.Notes.Length > 0)
            {
                sb.AppendLine("- notes: " + c.Notes);
            }

            if (c.FailureReason.Length > 0)
            {
                sb.AppendLine("- failure: " + c.FailureReason);
            }

            if (c.LastResponse.Length > 0)
            {
                sb.AppendLine("- last response: " + c.LastResponse);
            }
        }

        sb.AppendLine();
        sb.Append("Reply with the id of the best candidate on the first line, formatted exactly as: BEST: <id>");
        var prompt = sb.ToString().ReplaceLineEndings("\n");

        var stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);
        await File.WriteAllTextAsync(Path.Combine(stageDir, "prompt.md"), prompt, cancellationToken).ConfigureAwait(false);
        var result = await backend.RunAsync(node, prompt, context, cancellationToken).ConfigureAwait(false);
        var text = result.Text ?? result.Outcome?.Notes ?? "";
        await File.WriteAllTextAsync(Path.Combine(stageDir, "response.md"), text, cancellationToken).ConfigureAwait(false);

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().Trim('*', '`');
            if (trimmed.StartsWith("BEST:", StringComparison.OrdinalIgnoreCase))
            {
                var id = trimmed[5..].Trim().Trim('`', '*', '"', '\'', '.');
                var match = candidates.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }
            }
        }

        // Fall back to the first candidate id mentioned anywhere in the response.
        return candidates
            .Select(c => (Candidate: c, Index: text.IndexOf(c.Id, StringComparison.Ordinal)))
            .Where(x => x.Index >= 0)
            .OrderBy(x => x.Index)
            .Select(x => x.Candidate)
            .FirstOrDefault();
    }

    private static List<Candidate> ReadCandidates(object? raw)
    {
        var list = new List<Candidate>();
        if (raw is not List<object?> items)
        {
            return list;
        }

        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> map)
            {
                continue;
            }

            var id = ContextValues.ToDisplayString(map.GetValueOrDefault("id"));
            if (id.Length == 0)
            {
                continue;
            }

            StageStatusExtensions.TryParse(ContextValues.ToDisplayString(map.GetValueOrDefault("status")), out var status);
            double.TryParse(ContextValues.ToDisplayString(map.GetValueOrDefault("score")), NumberStyles.Float, CultureInfo.InvariantCulture, out var score);
            list.Add(new Candidate(
                id,
                status,
                score,
                ContextValues.ToDisplayString(map.GetValueOrDefault("notes")),
                ContextValues.ToDisplayString(map.GetValueOrDefault("failure_reason")),
                ContextValues.ToDisplayString(map.GetValueOrDefault("last_response"))));
        }

        return list;
    }
}
