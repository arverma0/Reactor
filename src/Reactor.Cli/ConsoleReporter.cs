using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Reactor.Events;
using Reactor.Execution;

namespace Reactor.Cli;

internal enum Verbosity
{
    Quiet,
    Normal,
    Verbose,
}

/// <summary>
/// Renders pipeline events to the console (or as JSON lines). While more than one stage is active (parallel branches),
/// activity lines name the stage they come from, and inside parallel sections every stage id gets a stable color.
/// </summary>
internal sealed partial class ConsoleReporter
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>Stage id colors for parallel sections, distinct from the status colors (green, yellow, red).</summary>
    private static readonly string[] StageColors = [Cyan, Magenta, BrightBlue, BrightMagenta, BrightCyan, Blue];

    private readonly object _gate = new();
    private readonly TextWriter _out;
    private readonly Verbosity _verbosity;
    private readonly bool _color;
    private readonly bool _json;

    // Stage tracking for attribution; guarded by _gate.
    private readonly List<string> _active = [];
    private readonly HashSet<string> _parallelNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _stageColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _questionStages = new(StringComparer.Ordinal);
    private int _openParallelSections;
    private bool _attributing;

    public ConsoleReporter(TextWriter output, Verbosity verbosity, bool color, bool json)
    {
        _out = output;
        _verbosity = verbosity;
        _color = color && !json;
        _json = json;
    }

    public static bool ColorSupported(bool noColorFlag) =>
        !noColorFlag && !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    public void OnEvent(PipelineEvent evt)
    {
        if (_json)
        {
            Write(evt.ToJson().ToJsonString(Compact));
            return;
        }

        // Branches report concurrently: track, render, and write each event as one step so lines match the state.
        lock (_gate)
        {
            TrackStart(evt);
            Render(evt);
            TrackEnd(evt);
        }
    }

    private void Render(PipelineEvent evt)
    {
        switch (evt)
        {
            case PipelineStarted started:
                Write($"{Paint("▶", Cyan)} {Paint($"Pipeline '{started.Name}'", Bold)}" + (started.Goal.Length > 0 ? $" — {started.Goal}" : ""));
                Write(Paint($"  run {started.RunId} · logs {started.LogsRoot}", Dim));
                break;
            case StageStarted stage:
                Write($"{Paint("●", Blue)} {Paint(stage.NodeId, Bold + StageColor(stage.NodeId))} {Paint($"[{stage.HandlerType}]", Dim)}" + (stage.Attempt > 1 ? Paint($" attempt {stage.Attempt}", Yellow) : ""));
                break;
            case StageCompleted completed:
                var (icon, color) = completed.Status switch
                {
                    "success" => ("✔", Green),
                    "partial_success" => ("◐", Yellow),
                    "skipped" => ("○", Dim),
                    _ => ("✖", Red),
                };
                var notes = _verbosity == Verbosity.Verbose && completed.Notes.Length > 0 ? Paint($" — {Clip(completed.Notes, 160)}", Dim) : "";
                Write($"{Paint(icon, color)} {Paint(completed.NodeId, StageColor(completed.NodeId))} {Paint(completed.Status, color)} {Paint($"({Seconds(completed.Duration)})", Dim)}{notes}");
                break;
            case StageFailed failed:
                Write($"  {Paint("✖", failed.WillRetry ? Yellow : Red)} {Paint(failed.NodeId, StageColor(failed.NodeId))}: {Clip(failed.Error, 400)}" + (failed.WillRetry ? Paint(" (will retry)", Yellow) : ""));
                break;
            case StageRetrying retrying:
                Write(Around("  ↻ ", retrying.NodeId, $" retry #{retrying.Attempt} in {retrying.Delay.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s", Yellow));
                break;
            case StageLog log when ShowLog(log.Level):
                var logColor = log.Level switch { "error" => Red, "warn" => Yellow, "debug" => Dim, _ => "" };
                var message = Clip(log.Message, _verbosity == Verbosity.Verbose ? 2000 : 240);
                Write("  " + (_attributing ? Paint("│ ", logColor) + Attributed(log.NodeId, message, logColor) : Paint("│ " + message, logColor)));
                break;
            case EdgeSelected edge when _verbosity == Verbosity.Verbose:
                Write(Paint($"  ↳ {edge.From} → {edge.To} ({edge.Reason}{(edge.Label.Length > 0 ? ", " + edge.Label : "")})", Dim));
                break;
            case GoalGateUnsatisfied gate:
                Write(Paint($"  ⚠ goal gate '{gate.NodeId}' not satisfied ({gate.Status})" + (gate.RetryTarget is null ? "" : $" → retrying from {gate.RetryTarget}"), Yellow));
                break;
            case LoopRestarted restart:
                Write(Paint($"⟲ loop restart #{restart.RestartCount} at {restart.TargetNodeId} (logs {restart.LogsRoot})", Yellow));
                break;
            case ParallelStarted parallel when _verbosity != Verbosity.Quiet:
                Write(Paint($"  ⇉ {parallel.NodeId}: {parallel.BranchCount} branches", Cyan));
                break;
            case ParallelBranchCompleted branch when _verbosity != Verbosity.Quiet:
                Write(Around($"  {(branch.Success ? "✔" : "✖")} branch ", branch.Branch, $" ({Seconds(branch.Duration)})", branch.Success ? Green : Red));
                break;
            case ParallelCompleted parallel when _verbosity != Verbosity.Quiet:
                Write(Paint($"  ⇉ {parallel.NodeId}: {parallel.SuccessCount} succeeded, {parallel.FailureCount} failed", Cyan));
                break;
            case InterviewCompleted interview:
                Write(_attributing && _questionStages.TryGetValue(interview.QuestionId, out var asked)
                    ? Around("  ✎ ", asked, $" answered: {interview.Answer}", Cyan)
                    : Paint($"  ✎ answered: {interview.Answer}", Cyan));
                break;
            case InterviewTimeout timeout:
                var waited = $"no answer for '{Clip(timeout.Question, 80)}' after {Seconds(timeout.Duration)}";
                Write(_attributing && timeout.Stage.Length > 0 ? Around("  ⌛ ", timeout.Stage, ": " + waited, Yellow) : Paint("  ⌛ " + waited, Yellow));
                break;
            case CheckpointSaved checkpoint when _verbosity == Verbosity.Verbose:
                Write(Paint($"  ⛁ checkpoint after {checkpoint.NodeId}", Dim));
                break;
            case FailedStageContinued continued:
                var flowed = $" failed; continuing to {continued.To} along an unconditional edge (nothing routes its failure)";
                Write(_attributing ? Around("  ⚠ ", continued.NodeId, flowed, Yellow) : Paint($"  ⚠ {continued.NodeId}{flowed}", Yellow));
                break;
            case VisitLimitReached limit:
                var limited = limit.Target is null
                    ? $" reached its visit limit ({limit.MaxVisits.ToString(CultureInfo.InvariantCulture)})"
                    : $" reached its visit limit ({limit.MaxVisits.ToString(CultureInfo.InvariantCulture)}) → {limit.Target}";
                Write(Around("  ⤳ ", limit.NodeId, limited, Yellow));
                break;
            case BudgetExceeded budget:
                Write(Paint($"  $ budget reached: {UsageLedger.FormatUsd(budget.SpentUsd)} of {UsageLedger.FormatUsd(budget.LimitUsd)}; stopping before {budget.NextNodeId}", Yellow));
                break;
            case MetricsRecorded metrics when _verbosity != Verbosity.Quiet:
                var values = string.Join(", ", metrics.Metrics.Take(8).Select(kv => kv.Key + "=" + kv.Value.ToString("0.###", CultureInfo.InvariantCulture)))
                             + (metrics.Metrics.Count > 8 ? $", … ({metrics.Metrics.Count} in all)" : "");
                Write(_attributing ? Around("  ≡ ", metrics.NodeId, ": " + values, Cyan) : Paint("  ≡ " + values, Cyan));
                break;
            case ArtifactProduced artifact when _verbosity != Verbosity.Quiet:
                var version = artifact.Version.ToString(CultureInfo.InvariantCulture);
                var produced = artifact.Path is null
                    ? $"{artifact.Name} v{version} (text, {Size(artifact.Bytes)})"
                    : $"{artifact.Name} v{version} → {artifact.Path} ({Size(artifact.Bytes)})";
                Write(_attributing ? Around("  ⇢ ", artifact.NodeId, ": " + produced, Cyan) : Paint("  ⇢ " + produced, Cyan));
                break;
        }
    }

    /// <summary>State changes that apply to the event's own line (a stage is active from its start line on).</summary>
    private void TrackStart(PipelineEvent evt)
    {
        switch (evt)
        {
            case PipelineStarted or LoopRestarted:
                _active.Clear();
                _questionStages.Clear();
                _openParallelSections = 0;
                _attributing = false;
                break;
            case StageStarted started when !_active.Contains(started.NodeId):
                _active.Add(started.NodeId);
                break;
            case ParallelStarted parallel:
                _openParallelSections++;
                _parallelNodes.Add(parallel.NodeId);
                break;
            case InterviewStarted interview:
                _questionStages[interview.QuestionId] = interview.Stage;
                break;
        }

        if (ActiveStages() > 1)
        {
            _attributing = true;
        }
    }

    /// <summary>
    /// State changes after the event's line. Attribution stays on until the parallel sections close, so the last
    /// branch's lines (and artifacts announced after a stage completes) keep naming their stage.
    /// </summary>
    private void TrackEnd(PipelineEvent evt)
    {
        switch (evt)
        {
            case StageCompleted completed:
                _active.Remove(completed.NodeId);
                break;
            case ParallelCompleted:
                _openParallelSections = Math.Max(0, _openParallelSections - 1);
                break;
            case InterviewCompleted interview:
                _questionStages.Remove(interview.QuestionId);
                break;
            case InterviewTimeout timeout:
                _questionStages.Remove(timeout.QuestionId);
                break;
        }

        if (_attributing && _openParallelSections == 0 && ActiveStages() <= 1)
        {
            _attributing = false;
        }
    }

    /// <summary>Active stages, not counting parallel nodes (their branches are the work).</summary>
    private int ActiveStages() => _active.Count(id => !_parallelNodes.Contains(id));

    /// <summary>
    /// <paramref name="message"/> from stage <paramref name="nodeId"/>: <c>proto_b → view …</c> for tool lines and other
    /// lines starting with a symbol, <c>proto_b: copilot-cli session … (model)</c> for backend session lines, and
    /// <c>proto_b: message</c> otherwise.
    /// </summary>
    private string Attributed(string nodeId, string message, string color)
    {
        var id = Paint(nodeId, StageColor(nodeId));
        if (SessionLine().Match(message) is { Success: true } session)
        {
            var model = session.Groups["model"].Success ? $" ({session.Groups["model"].Value})" : "";
            var resumed = session.Groups["verb"].Value is "resuming" or "continuing" ? " resumed" : "";
            return id + Paint($": {session.Groups["backend"].Value} session {session.Groups["id"].Value}{resumed}{model}", color);
        }

        return message.Length > 0 && !char.IsLetterOrDigit(message[0]) ? id + " " + Paint(message, color) : id + Paint(": " + message, color);
    }

    /// <summary>The color of <paramref name="nodeId"/> inside parallel sections (assigned on first use, then kept); "" elsewhere.</summary>
    private string StageColor(string nodeId)
    {
        if (!_color || (!_attributing && _openParallelSections == 0))
        {
            return "";
        }

        if (!_stageColors.TryGetValue(nodeId, out var code))
        {
            var taken = _active.Where(id => id != nodeId && _stageColors.ContainsKey(id)).Select(id => _stageColors[id]).ToHashSet(StringComparer.Ordinal);
            code = StageColors.FirstOrDefault(c => !taken.Contains(c)) ?? StageColors[_stageColors.Count % StageColors.Length];
            _stageColors[nodeId] = code;
        }

        return code;
    }

    /// <summary><paramref name="before"/> + <paramref name="nodeId"/> + <paramref name="after"/> in <paramref name="color"/>, the id in its stage color if it has one.</summary>
    private string Around(string before, string nodeId, string after, string color) =>
        StageColor(nodeId) is { Length: > 0 } stage
            ? Paint(before, color) + Paint(nodeId, stage) + Paint(after, color)
            : Paint(before + nodeId + after, color);

    /// <summary>Backend session lines: <c>copilot-cli: starting session 3589… (claude-sonnet-5)</c>, <c>copilot-sdk: continuing session …</c>.</summary>
    [GeneratedRegex(@"^(?<backend>[A-Za-z][\w.-]*): (?<verb>starting|resuming|started|continuing) session (?<id>\S+)(?: \((?<model>[^()]*)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionLine();

    /// <summary>Prints the final summary (or the result as JSON).</summary>
    public void Summary(PipelineResult result)
    {
        if (_json)
        {
            var json = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "Result",
                ["run_id"] = result.RunId,
                ["status"] = result.Cancelled ? "cancelled" : result.Status.ToWire(),
                ["logs_root"] = result.LogsRoot,
                ["duration_seconds"] = result.Duration.TotalSeconds,
                ["completed_nodes"] = new System.Text.Json.Nodes.JsonArray(result.CompletedNodes.Select(n => (System.Text.Json.Nodes.JsonNode?)n).ToArray()),
                ["failure_reason"] = result.FinalOutcome.FailureReason,
                ["notes"] = result.FinalOutcome.Notes,
                ["usage"] = UsageLedger.ToJson(result.Usage),
                ["budget_exceeded"] = result.BudgetExceeded,
            };
            Write(json.ToJsonString(Compact));
            return;
        }

        Write("");
        if (result.Cancelled)
        {
            Write($"{Paint("■ Cancelled", Yellow)} after {Seconds(result.Duration)} — resume with: reactor resume \"{result.LogsRoot}\"");
            return;
        }

        var ok = result.Status is StageStatus.Success or StageStatus.PartialSuccess;
        if (result.BudgetExceeded)
        {
            Write($"{Paint("■ Budget reached", Yellow)} after {Seconds(result.Duration)}");
            Write(Paint("  " + result.FinalOutcome.FailureReason, Yellow));
            Write(Paint($"  continue with: reactor resume \"{result.LogsRoot}\" --max-cost <higher budget>", Dim));
        }
        else
        {
            Write($"{Paint(ok ? "✔ Pipeline completed" : "✖ Pipeline failed", ok ? Green : Red)} ({result.Status.ToWire()}) in {Seconds(result.Duration)}");
            if (!ok && result.FinalOutcome.FailureReason.Length > 0)
            {
                Write(Paint("  " + result.FinalOutcome.FailureReason, Red));
            }
        }

        // Per the spec, failures routed onward (e.g. over unconditional edges) do not fail the run; make them visible.
        var failedStages = result.NodeOutcomes.Where(kv => kv.Value.Status == StageStatus.Fail).Select(kv => kv.Key).ToList();
        if (ok && failedStages.Count > 0)
        {
            Write(Paint($"  ⚠ {failedStages.Count} stage(s) failed and were routed onward: {string.Join(", ", failedStages)}", Yellow));
        }

        Write(Paint($"  stages: {string.Join(" → ", result.CompletedNodes)}", Dim));
        if (UsageLedger.Describe(result.Usage) is { Length: > 0 } usage)
        {
            Write(Paint($"  usage:  {usage}", Dim));
        }

        Write(Paint($"  logs:   {result.LogsRoot}", Dim));
    }

    public void Info(string message)
    {
        if (!_json)
        {
            Write(Paint(message, Dim));
        }
    }

    public void Warn(string message) => Error.WriteLine(_color ? $"{Yellow}{message}{Reset}" : message);

    public static TextWriter Error => Console.Error;

    private bool ShowLog(string level) => _verbosity switch
    {
        Verbosity.Quiet => level is "warn" or "error",
        Verbosity.Normal => level is not "debug",
        _ => true,
    };

    private void Write(string line)
    {
        lock (_gate)
        {
            _out.WriteLine(line);
            _out.Flush();
        }
    }

    private string Paint(string text, string code) => _color && code.Length > 0 ? code + text + Reset : text;

    private static string Seconds(TimeSpan duration) => duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };

    private static string Clip(string text, int max)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string Dim = "\u001b[2m";
    private const string Red = "\u001b[31m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Blue = "\u001b[34m";
    private const string Magenta = "\u001b[35m";
    private const string Cyan = "\u001b[36m";
    private const string BrightBlue = "\u001b[94m";
    private const string BrightMagenta = "\u001b[95m";
    private const string BrightCyan = "\u001b[96m";
}
