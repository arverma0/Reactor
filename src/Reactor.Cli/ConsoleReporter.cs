using System.Globalization;
using System.Text.Json;
using Reactor.Events;
using Reactor.Execution;

namespace Reactor.Cli;

internal enum Verbosity
{
    Quiet,
    Normal,
    Verbose,
}

/// <summary>Renders pipeline events to the console (or as JSON lines).</summary>
internal sealed class ConsoleReporter
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly TextWriter _out;
    private readonly Verbosity _verbosity;
    private readonly bool _color;
    private readonly bool _json;

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

        switch (evt)
        {
            case PipelineStarted started:
                Write($"{Paint("▶", Cyan)} {Paint($"Pipeline '{started.Name}'", Bold)}" + (started.Goal.Length > 0 ? $" — {started.Goal}" : ""));
                Write(Paint($"  run {started.RunId} · logs {started.LogsRoot}", Dim));
                break;
            case StageStarted stage:
                Write($"{Paint("●", Blue)} {Paint(stage.NodeId, Bold)} {Paint($"[{stage.HandlerType}]", Dim)}" + (stage.Attempt > 1 ? Paint($" attempt {stage.Attempt}", Yellow) : ""));
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
                Write($"{Paint(icon, color)} {completed.NodeId} {Paint(completed.Status, color)} {Paint($"({Seconds(completed.Duration)})", Dim)}{notes}");
                break;
            case StageFailed failed:
                Write($"  {Paint("✖", failed.WillRetry ? Yellow : Red)} {failed.NodeId}: {Clip(failed.Error, 400)}" + (failed.WillRetry ? Paint(" (will retry)", Yellow) : ""));
                break;
            case StageRetrying retrying:
                Write(Paint($"  ↻ {retrying.NodeId} retry #{retrying.Attempt} in {retrying.Delay.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s", Yellow));
                break;
            case StageLog log when ShowLog(log.Level):
                var logColor = log.Level switch { "error" => Red, "warn" => Yellow, "debug" => Dim, _ => "" };
                Write("  " + Paint("│ " + Clip(log.Message, _verbosity == Verbosity.Verbose ? 2000 : 240), logColor));
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
                Write(Paint($"  {(branch.Success ? "✔" : "✖")} branch {branch.Branch} ({Seconds(branch.Duration)})", branch.Success ? Green : Red));
                break;
            case ParallelCompleted parallel when _verbosity != Verbosity.Quiet:
                Write(Paint($"  ⇉ {parallel.NodeId}: {parallel.SuccessCount} succeeded, {parallel.FailureCount} failed", Cyan));
                break;
            case InterviewCompleted interview:
                Write(Paint($"  ✎ answered: {interview.Answer}", Cyan));
                break;
            case InterviewTimeout timeout:
                Write(Paint($"  ⌛ no answer for '{Clip(timeout.Question, 80)}' after {Seconds(timeout.Duration)}", Yellow));
                break;
            case CheckpointSaved checkpoint when _verbosity == Verbosity.Verbose:
                Write(Paint($"  ⛁ checkpoint after {checkpoint.NodeId}", Dim));
                break;
        }
    }

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
        Write($"{Paint(ok ? "✔ Pipeline completed" : "✖ Pipeline failed", ok ? Green : Red)} ({result.Status.ToWire()}) in {Seconds(result.Duration)}");
        if (!ok && result.FinalOutcome.FailureReason.Length > 0)
        {
            Write(Paint("  " + result.FinalOutcome.FailureReason, Red));
        }

        Write(Paint($"  stages: {string.Join(" → ", result.CompletedNodes)}", Dim));
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
    private const string Cyan = "\u001b[36m";
}
