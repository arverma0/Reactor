using System.Diagnostics;
using System.Globalization;
using System.Text;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Workspaces;

namespace Reactor.Handlers;

/// <summary>
/// Parallel fan-out (spec 4.8): runs every outgoing branch concurrently (bounded by <c>max_parallel</c>, default 4), each
/// on an isolated clone of the context, until each branch reaches a join point (fan-in node, exit, or dead end).
/// Results are stored in <c>parallel.results</c>; branch context changes are not merged back, except artifact records
/// (<c>artifacts.*</c>, a Reactor extension; see <see cref="StageArtifacts.MergeBranchRecords"/>), which are merged in branch
/// declaration order from every branch that ran, failed ones included, and outcome metrics (<c>metrics.*</c>, also a Reactor
/// extension; a later branch's value wins). The engine then continues at the branches' common
/// join node. Join policies: <c>wait_all</c> (default; SUCCESS when no branch failed, else PARTIAL_SUCCESS) and
/// <c>first_success</c> (SUCCESS as soon as one branch succeeds; the rest are cancelled and contribute no artifacts).
/// Reactor extension: <c>branch_workdir</c> chooses the branches' working directories — <c>shared</c> (default: the
/// parent's), <c>worktree</c> (a detached git worktree per branch, of a snapshot of the parent's current state), or
/// <c>copy</c> (a copy of its files per branch); in real runs each <c>parallel.results</c> entry records the branch's
/// <c>workdir</c> (plus, for its own workspace, the snapshots it started from and ended at and what it changed).
/// Simulations always share the working directory.
/// </summary>
public sealed class ParallelHandler : INodeHandler
{
    private readonly bool _sequential;

    public ParallelHandler()
    {
    }

    /// <summary>
    /// With <paramref name="sequential"/>, branches run one at a time in edge order instead of concurrently, which makes the
    /// result independent of thread scheduling (used by simulations): with <c>first_success</c>, the first branch in edge
    /// order that succeeds wins and later branches are not started.
    /// </summary>
    internal ParallelHandler(bool sequential)
    {
        _sequential = sequential;
    }

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

        // Reactor extension: branch workspaces (branch_workdir=worktree|copy) in real runs; simulations always share.
        var workdirMode = WorkspaceAttributes.BranchWorkdirOf(node);
        if (!WorkspaceAttributes.BranchWorkdirModes.Contains(workdirMode))
        {
            return Outcome.Fail($"Unknown branch_workdir '{workdirMode}'; expected shared, worktree, or copy.");
        }

        var tracker = run.Workspace is { RealRun: true } realRun ? realRun : null;
        IReadOnlyList<BranchWorkspace>? workspaces = null;
        if (tracker is not null && workdirMode != WorkspaceAttributes.BranchModes.Shared)
        {
            try
            {
                workspaces = await tracker.CreateBranchWorkspacesAsync(node, workdirMode, context, branches.Select(e => e.To).ToList(), logsRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Outcome.Fail(ex.Message);
            }
        }

        var forkValues = context.Snapshot();
        var stopwatch = Stopwatch.StartNew();
        run.Events.Emit(new ParallelStarted(node.Id, branches.Count));

        using var gate = new SemaphoreSlim(maxParallel, maxParallel);
        using var firstSuccessCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var results = new BranchResult[branches.Count];

        async Task RunBranchAsync(Edge edge, int index)
        {
            await gate.WaitAsync(firstSuccessCts.Token).ConfigureAwait(false);
            try
            {
                run.Events.Emit(new ParallelBranchStarted(node.Id, edge.To, index));
                var branchContext = context.Clone();
                if (workspaces is not null)
                {
                    branchContext.Set(PipelineContext.WorkingDirectoryKey, workspaces[index].Directory);
                }

                BranchResult result;
                try
                {
                    result = await run.BranchExecutor.ExecuteBranchAsync(edge.To, branchContext, firstSuccessCts.Token).ConfigureAwait(false);
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
        }

        if (_sequential)
        {
            for (var i = 0; i < branches.Count && !firstSuccessCts.IsCancellationRequested; i++)
            {
                await RunBranchAsync(branches[i], i).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        else
        {
            var tasks = branches.Select((edge, index) => Task.Run(() => RunBranchAsync(edge, index), CancellationToken.None)).ToList();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (firstSuccessCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Branches still waiting for a slot were cancelled by first_success.
            }
        }

        for (var i = 0; i < results.Length; i++)
        {
            results[i] ??= new BranchResult { BranchId = branches[i].To, Status = StageStatus.Skipped, Notes = "Not started (first_success already satisfied)." };
        }

        // Where each branch worked: its workspace, the snapshot it started from and ended at, and what it changed.
        var workdirs = new Dictionary<string, object?>[results.Length];
        if (tracker is not null)
        {
            for (var i = 0; i < results.Length; i++)
            {
                workdirs[i] = workspaces is null
                    ? new Dictionary<string, object?> { ["workdir"] = context.WorkingDirectory, ["workdir_mode"] = WorkspaceAttributes.BranchModes.Shared }
                    : await tracker.FinishBranchAsync(workspaces[i], cancellationToken).ConfigureAwait(false);
            }

            if (workspaces is not null)
            {
                await tracker.CheckParentAfterBranchesAsync(node, context, workspaces, cancellationToken).ConfigureAwait(false);
            }
        }

        var successCount = results.Count(r => r.Status == StageStatus.Success || r.Status == StageStatus.PartialSuccess);
        var failCount = results.Count(r => r.Status == StageStatus.Fail);
        run.Events.Emit(new ParallelCompleted(node.Id, stopwatch.Elapsed, successCount, failCount));

        var join = DetermineJoin(results, graph);
        var serialized = results.Select((r, i) => Serialize(r, workdirs[i])).ToList();
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

        // Reactor extension: so are outcome metrics (metrics.*) reported inside branches; later branches win a conflict.
        foreach (var result in results)
        {
            foreach (var (key, value) in result.Context)
            {
                if (key.StartsWith(Outcome.MetricsContextPrefix, StringComparison.Ordinal) &&
                    !(forkValues.TryGetValue(key, out var before) && Equals(ContextValues.Normalize(before), ContextValues.Normalize(value))))
                {
                    updates[key] = value;
                }
            }
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

    private static Dictionary<string, object?> Serialize(BranchResult result, Dictionary<string, object?>? workdir)
    {
        var entry = new Dictionary<string, object?>
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

        // Reactor extension: workdir, workdir_mode, base_snapshot, snapshot, tree, changes (real runs only).
        foreach (var (key, value) in workdir ?? [])
        {
            entry[key] = value;
        }

        return entry;
    }

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
/// exist or every candidate failed. Reactor extension: the best branch's working directory is exposed as
/// <c>parallel.fan_in.best_workdir</c>, and with <c>promote="best"</c> the changes that branch made in its own workspace
/// (<c>branch_workdir="worktree"</c> or <c>"copy"</c>) are applied to the fan-in's working directory — the fan-in fails,
/// changing nothing, when that directory changed the same files since the fork.
/// </summary>
public sealed class FanInHandler : INodeHandler
{
    private sealed record Candidate(string Id, StageStatus Status, double Score, string Notes, string FailureReason, string LastResponse)
    {
        /// <summary>The candidate's <c>parallel.results</c> entry.</summary>
        public IReadOnlyDictionary<string, object?> Entry { get; init; } = new Dictionary<string, object?>();

        public string Workdir => ContextValues.ToDisplayString(Entry.GetValueOrDefault("workdir"));

        /// <summary>True when the branch worked in its own workspace.</summary>
        public bool Isolated => ContextValues.ToDisplayString(Entry.GetValueOrDefault("workdir_mode")) is WorkspaceAttributes.BranchModes.Worktree or WorkspaceAttributes.BranchModes.Copy;
    }

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
        IReadOnlyDictionary<string, object?>? judgeMetadata = null;
        string? judgement = null;
        var backend = context.Run?.Backend;
        if (!string.IsNullOrWhiteSpace(node.Prompt) && backend is not null)
        {
            var (chosen, metadata, text) = await EvaluateWithLlmAsync(node, context, graph, logsRoot, backend, candidates, cancellationToken).ConfigureAwait(false);
            best = chosen ?? HeuristicSelect(candidates);
            method = chosen is null ? "heuristic (LLM choice unparseable)" : "llm";
            judgeMetadata = metadata;
            judgement = text;
        }
        else
        {
            best = HeuristicSelect(candidates);
            method = "heuristic";
        }

        if (judgeMetadata is not null)
        {
            // The judge is a full backend session: record what it cost like any codergen stage (plus its decision).
            var record = new Dictionary<string, object?>(judgeMetadata, StringComparer.Ordinal)
            {
                ["fan_in_method"] = method,
                ["fan_in_best_id"] = best.Id,
            };
            var stageDir = Path.Combine(logsRoot, node.Id);
            Directory.CreateDirectory(stageDir);
            await File.WriteAllTextAsync(
                Path.Combine(stageDir, "backend.json"),
                ContextValues.ToJson(ContextValues.Normalize(record))?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "{}",
                cancellationToken).ConfigureAwait(false);
        }

        var updates = new Dictionary<string, object?>
        {
            ["parallel.fan_in.best_id"] = best.Id,
            ["parallel.fan_in.best_outcome"] = best.Status.ToWire(),
            ["parallel.fan_in.best_notes"] = best.Notes,
            ["parallel.fan_in.best_response"] = best.LastResponse,
            ["parallel.fan_in.method"] = method,
            ["parallel.fan_in.best_workdir"] = best.Workdir,
        };
        var notes = $"Selected best candidate: {best.Id} ({method})";
        if (WorkspaceAttributes.Promotes(node) && context.Run?.Workspace is { RealRun: true } tracker)
        {
            var promotion = await tracker.PromoteAsync(node, context, best.Entry, cancellationToken).ConfigureAwait(false);
            if (promotion.Failure is { } failure)
            {
                return Outcome.Fail(failure, updates);
            }

            updates["parallel.fan_in.promoted"] = promotion.Promoted ? best.Id : "";
            notes += "; " + promotion.Note;
        }

        // Reactor extension: an LLM judge's declared outputs (its judgement as text, or a report file it wrote) are
        // recorded like a codergen stage's; a declared file it did not write fails the fan-in.
        var outcome = Outcome.Success(notes, updates);
        return judgement is not null && ArtifactPins.Parse(node).Outputs.Count > 0
            ? await StageArtifacts.CompleteAsync(node, outcome, context, judgement, cancellationToken: cancellationToken).ConfigureAwait(false)
            : outcome;
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

    private static async Task<(Candidate? Chosen, IReadOnlyDictionary<string, object?> Metadata, string Text)> EvaluateWithLlmAsync(
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

            // Branches with their own workspace: where to look, and what they changed since the fork.
            if (c.Isolated && c.Workdir.Length > 0)
            {
                sb.AppendLine("- workspace: " + c.Workdir);
                if (DescribeChanges(c) is { Length: > 0 } changed)
                {
                    sb.AppendLine("- files changed: " + changed);
                }
            }
        }

        // Reactor extension: the judge's declared artifact inputs and outputs (e.g. a judgement report to write).
        if (StageArtifacts.PromptSections(node, context) is { Length: > 0 } artifacts)
        {
            sb.AppendLine().Append(artifacts.TrimEnd()).AppendLine();
        }

        sb.AppendLine();
        sb.Append("Reply with the id of the best candidate on the first line, formatted exactly as: BEST: <id>");
        var prompt = sb.ToString().ReplaceLineEndings("\n");

        var stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);
        await File.WriteAllTextAsync(Path.Combine(stageDir, "prompt.md"), prompt, cancellationToken).ConfigureAwait(false);
        CodergenResult result;
        try
        {
            result = await backend.RunAsync(node, prompt, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CodergenHandler.RecordFailedAttemptAsync(stageDir, ex, cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            throw;
        }

        var text = result.Text ?? result.Outcome?.Notes ?? "";
        await File.WriteAllTextAsync(Path.Combine(stageDir, "response.md"), text, cancellationToken).ConfigureAwait(false);
        return (ParseChoice(text, candidates), result.Metadata, text);
    }

    /// <summary>The candidate named on a <c>BEST: &lt;id&gt;</c> line, else the first candidate id mentioned anywhere.</summary>
    private static Candidate? ParseChoice(string text, IReadOnlyList<Candidate> candidates)
    {
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
                ContextValues.ToDisplayString(map.GetValueOrDefault("last_response")))
            {
                Entry = map,
            });
        }

        return list;
    }

    /// <summary>"+2 ~1 -0: src/a.ts, src/b.ts, …" from a result entry's <c>changes</c>, or "" without one.</summary>
    private static string DescribeChanges(Candidate candidate)
    {
        if (candidate.Entry.GetValueOrDefault("changes") is not Dictionary<string, object?> changes)
        {
            return "";
        }

        string Count(string key) => ContextValues.ToDisplayString(changes.GetValueOrDefault(key) ?? 0);
        var paths = changes.GetValueOrDefault("paths") is List<object?> list ? list.Select(ContextValues.ToDisplayString).ToList() : [];
        return $"+{Count("added")} ~{Count("modified")} -{Count("deleted")}" + (paths.Count > 0 ? ": " + string.Join(", ", paths) : "");
    }
}
