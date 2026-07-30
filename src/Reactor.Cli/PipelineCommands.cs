using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Human;
using Option = System.CommandLine.Option;
using Reactor.Llm;
using Reactor.Rendering;
using Reactor.Validation;

namespace Reactor.Cli;

/// <summary>Exit codes.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int PipelineFailed = 1;
    public const int InvalidInput = 2;
    public const int Configuration = 3;
    public const int Cancelled = 130;
}

/// <summary>Options shared by commands that run pipelines.</summary>
internal sealed class RunOptionSet
{
    public Option<string> Backend { get; } = new("--backend", "-b")
    {
        Description = $"Codergen backend: {string.Join(", ", BackendFactory.Kinds)} (default: auto, or $REACTOR_BACKEND)",
        DefaultValueFactory = _ => Environment.GetEnvironmentVariable("REACTOR_BACKEND") is { Length: > 0 } fromEnv ? fromEnv : "auto",
    };

    public Option<string?> Model { get; } = new("--model", "-m") { Description = "Default model for stages without llm_model" };

    public Option<string?> Provider { get; } = new("--provider") { Description = "Default LLM provider for the agent/direct backends" };

    public Option<int?> MaxTokens { get; } = new("--max-tokens") { Description = "Output token limit per LLM response for the agent/direct backends (default: the provider's)" };

    public Option<string?> WorkingDirectory { get; } = new("--workdir", "-C") { Description = "Working directory for tools and agents (default: current directory)" };

    public Option<string[]> Set { get; } = new("--set", "-s") { Description = "Initial context value key=value (repeatable)", Arity = ArgumentArity.ZeroOrMore };

    public Option<bool> AutoApprove { get; } = new("--auto-approve", "-y") { Description = "Answer human gates automatically (first option / yes)" };

    public Option<string[]> Answers { get; } = new("--answer") { Description = "Pre-supplied human gate answers, used in order (repeatable)", Arity = ArgumentArity.ZeroOrMore };

    public Option<int> MaxSteps { get; } = new("--max-steps") { Description = "Fail the run after this many stage executions, a guard against runaway loops (0 = unlimited)", DefaultValueFactory = _ => 500 };

    public Option<double?> MaxCost { get; } = new("--max-cost") { Description = "Stop (resumably) before the next stage once the run's recorded cost reaches this many US dollars; overrides the graph's max_cost (0 = no budget)" };

    public Option<bool> Json { get; } = new("--json") { Description = "Emit events and the result as JSON lines" };

    public Option<bool> Verbose { get; } = new("--verbose", "-v") { Description = "Show debug logs, edges, and checkpoints" };

    public Option<bool> Quiet { get; } = new("--quiet", "-q") { Description = "Only show stage results, warnings, and errors" };

    public Option<bool> NoColor { get; } = new("--no-color") { Description = "Disable colored output" };

    public Option<string?> CopilotRuntime { get; } = new("--copilot-runtime") { Description = "Path to a Copilot runtime for the copilot backend" };

    public Option<string?> CopilotCli { get; } = new("--copilot-cli") { Description = "Path to the copilot CLI (default: copilot on PATH, or $REACTOR_COPILOT_CLI)" };

    public Option<string?> ClaudeCli { get; } = new("--claude-cli") { Description = "Path to the claude CLI (default: claude on PATH, or $REACTOR_CLAUDE_CLI)" };

    public Option<bool> NoTrackChanges { get; } = new("--no-track-changes")
    {
        Description = "Do not snapshot the working directory around stages (no <stage>/changes.json, no workspace rewind on re-fly); like the graph attribute track_changes=false",
    };

    public void AddTo(Command command)
    {
        foreach (var option in new Option[] { Backend, Model, Provider, MaxTokens, WorkingDirectory, Set, AutoApprove, Answers, MaxSteps, MaxCost, NoTrackChanges, Json, Verbose, Quiet, NoColor, CopilotRuntime, CopilotCli, ClaudeCli })
        {
            command.Options.Add(option);
        }

        AddValidators();
    }

    /// <summary>Restricts <see cref="Backend"/> to the known kinds and <see cref="MaxTokens"/> to positive values.</summary>
    public void AddValidators()
    {
        Backend.AcceptOnlyFromAmong(BackendFactory.Kinds);
        MaxTokens.Validators.Add(PositiveIfSet);
        MaxCost.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<double?>() is { } cost && (cost < 0 || !double.IsFinite(cost)))
            {
                result.AddError($"{result.Option.Name} must be zero or a positive amount of US dollars.");
            }
        });
    }

    /// <summary>Rejects zero or negative values of an optional count.</summary>
    internal static void PositiveIfSet(System.CommandLine.Parsing.OptionResult result)
    {
        if (result.GetValueOrDefault<int?>() is <= 0)
        {
            result.AddError($"{result.Option.Name} must be a positive number.");
        }
    }

    public BackendSettings Backends(ParseResult result) => new()
    {
        Kind = result.GetValue(Backend) ?? "auto",
        Model = result.GetValue(Model),
        Provider = result.GetValue(Provider),
        MaxTokens = result.GetValue(MaxTokens),
        CopilotRuntime = result.GetValue(CopilotRuntime),
        CopilotCli = result.GetValue(CopilotCli),
        ClaudeCli = result.GetValue(ClaudeCli),
    };

    public string WorkDir(ParseResult result) => Path.GetFullPath(result.GetValue(WorkingDirectory) ?? Directory.GetCurrentDirectory());

    public ConsoleReporter Reporter(ParseResult result)
    {
        var json = result.GetValue(Json);
        var verbosity = result.GetValue(Verbose) ? Verbosity.Verbose : result.GetValue(Quiet) ? Verbosity.Quiet : Verbosity.Normal;
        return new ConsoleReporter(Console.Out, verbosity, ConsoleReporter.ColorSupported(result.GetValue(NoColor)), json);
    }

    public IInterviewer Interviewer(ParseResult result)
    {
        if (result.GetValue(AutoApprove))
        {
            return new AutoApproveInterviewer();
        }

        var answers = result.GetValue(Answers) ?? [];
        if (answers.Length > 0)
        {
            return new QueueInterviewer(answers);
        }

        return new ConsoleInterviewer(Console.In, result.GetValue(Json) ? Console.Error : Console.Out);
    }

    /// <exception cref="FormatException">A <c>--set</c> value is not <c>key=value</c>.</exception>
    public Dictionary<string, object?> InitialContext(ParseResult result)
    {
        var context = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in result.GetValue(Set) ?? [])
        {
            var index = pair.IndexOf('=', StringComparison.Ordinal);
            if (index <= 0)
            {
                throw new FormatException($"--set expects key=value, got '{pair}'.");
            }

            var value = pair[(index + 1)..];
            context[pair[..index].Trim()] = TryParseJson(value);
        }

        return context;
    }

    private static object? TryParseJson(string value)
    {
        if (value.Length > 0 && (value[0] is '{' or '[' or '"' || char.IsDigit(value[0]) || value is "true" or "false" or "null"))
        {
            try
            {
                return ContextValues.FromJson(JsonNode.Parse(value));
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return value;
    }
}

internal static class PipelineCommands
{
    public static Command Run()
    {
        var file = new Argument<string>("file") { Description = "DOT pipeline file ('-' reads standard input)" };
        var logs = new Option<string?>("--logs") { Description = "Run directory (default: <workdir>/.reactor/runs/<UTC timestamp>-<name>)" };
        var startAt = new Option<string?>("--start-at") { Description = "Start at this node instead of the start node" };
        var options = new RunOptionSet();
        var command = new Command("run", "Run a pipeline") { file, logs, startAt };
        options.AddTo(command);
        command.SetAction(async (result, cancellationToken) =>
        {
            var reporter = options.Reporter(result);
            string source;
            try
            {
                source = await ReadSourceAsync(result.GetValue(file)!, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                reporter.Warn(ex.Message);
                return ExitCodes.InvalidInput;
            }

            var workDir = options.WorkDir(result);
            return await ExecuteAsync(options, result, reporter, workDir, cancellationToken, async (runner, runOptions, ct) =>
            {
                Graph graph;
                try
                {
                    graph = DotParser.Parse(source);
                    var path = result.GetValue(file)!;
                    graph.SourcePath = path == "-" ? null : Path.GetFullPath(path);
                }
                catch (DotParseException ex)
                {
                    reporter.Warn("✖ " + ex.Message);
                    return null;
                }

                var prepared = runner.Prepare(graph);
                if (!PrintDiagnostics(reporter, prepared.Diagnostics))
                {
                    return null;
                }

                string logsRoot;
                if (result.GetValue(logs) is { } explicitLogs)
                {
                    // Nothing is created under the working directory's .reactor unless the run directory is in it.
                    logsRoot = Path.GetFullPath(explicitLogs);
                    if (IsInside(logsRoot, ReactorDirectory(workDir)))
                    {
                        EnsureReactorDirectory(workDir);
                    }
                }
                else
                {
                    EnsureRunsDirectory(workDir);
                    logsRoot = UniqueRunDirectory(RunsDirectory(workDir), RunDirectoryName(prepared.Graph.Name, DateTime.UtcNow));
                }

                return await runner.RunAsync(graph, runOptions with { LogsRoot = logsRoot, StartAt = result.GetValue(startAt) }, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        });
        return command;
    }

    public static Command Resume()
    {
        var logs = new Argument<string>("logs") { Description = "The run directory (containing checkpoint.json) to resume" };
        var options = new RunOptionSet();
        var command = new Command("resume", "Resume a run from its last checkpoint") { logs };
        options.AddTo(command);
        command.SetAction(async (result, cancellationToken) =>
        {
            var reporter = options.Reporter(result);
            var logsRoot = Path.GetFullPath(result.GetValue(logs)!);
            if (!File.Exists(Path.Combine(logsRoot, "checkpoint.json")))
            {
                reporter.Warn($"No checkpoint.json in {logsRoot}.");
                return ExitCodes.InvalidInput;
            }

            return await ExecuteAsync(options, result, reporter, options.WorkDir(result), cancellationToken,
                (runner, runOptions, ct) => runner.ResumeAsync(logsRoot, runOptions with { LogsRoot = logsRoot }, ct)!).ConfigureAwait(false);
        });
        return command;
    }

    public static Command Validate()
    {
        var file = new Argument<string>("file") { Description = "DOT pipeline file ('-' reads standard input)" };
        var json = new Option<bool>("--json") { Description = "Print diagnostics as JSON" };
        var noColor = new Option<bool>("--no-color") { Description = "Disable colored output" };
        var simulate = new Option<bool>("--simulate") { Description = "Also fly the happy path in the wind tunnel (every stage succeeds): every flight must land" };
        var command = new Command("validate", "Parse and lint a pipeline without running it") { file, json, noColor, simulate };
        command.SetAction(async (result, cancellationToken) =>
        {
            string source;
            try
            {
                source = await ReadSourceAsync(result.GetValue(file)!, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitCodes.InvalidInput;
            }

            var runner = new PipelineRunner(new PipelineRunnerOptions());
            PreparedPipeline prepared;
            try
            {
                prepared = runner.Prepare(DotParser.Parse(source));
            }
            catch (DotParseException ex)
            {
                if (result.GetValue(json))
                {
                    Console.WriteLine(new JsonObject { ["valid"] = false, ["error"] = ex.Message, ["line"] = ex.Line, ["column"] = ex.Column }.ToJsonString());
                }
                else
                {
                    await Console.Error.WriteLineAsync("✖ " + ex.Message).ConfigureAwait(false);
                }

                return ExitCodes.InvalidInput;
            }

            var happyPath = result.GetValue(simulate) && !prepared.HasErrors
                ? await SimulateCommand.CheckHappyPathAsync(prepared.Graph, cancellationToken).ConfigureAwait(false)
                : null;
            if (result.GetValue(json))
            {
                var report = new JsonObject
                {
                    ["valid"] = !prepared.HasErrors && happyPath?.Passed != false,
                    ["name"] = prepared.Graph.Name,
                    ["nodes"] = prepared.Graph.Nodes.Count,
                    ["edges"] = prepared.Graph.Edges.Count,
                    ["diagnostics"] = new JsonArray(prepared.Diagnostics.Select(d => (JsonNode?)new JsonObject
                    {
                        ["rule"] = d.Rule,
                        ["severity"] = d.Severity.ToString().ToLowerInvariant(),
                        ["message"] = d.Message,
                        ["node_id"] = d.NodeId,
                        ["fix"] = d.Fix,
                    }).ToArray()),
                };
                if (happyPath is not null)
                {
                    report["simulation"] = happyPath.ToJson();
                }

                Console.WriteLine(report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                var reporter = new ConsoleReporter(Console.Out, Verbosity.Normal, ConsoleReporter.ColorSupported(result.GetValue(noColor)), json: false);
                PrintDiagnostics(reporter, prepared.Diagnostics);
                if (!prepared.HasErrors)
                {
                    Console.WriteLine($"✔ {prepared.Graph.Name}: {prepared.Graph.Nodes.Count} nodes, {prepared.Graph.Edges.Count} edges — valid");
                }

                if (happyPath is not null)
                {
                    Console.WriteLine(happyPath.Describe(result.GetValue(file)!));
                }
            }

            return prepared.HasErrors || happyPath is { Passed: false } ? ExitCodes.InvalidInput : ExitCodes.Success;
        });
        return command;
    }

    public static Command Render()
    {
        var file = new Argument<string>("file") { Description = "DOT pipeline file ('-' reads standard input)" };
        var output = new Option<string?>("--output", "-o") { Description = "Output file (default: <file>.<format>; '-' for stdout)" };
        var format = new Option<string?>("--format", "-f")
        {
            Description = "svg, png, pdf, jpg, gif, json (any Graphviz output format), or dot for the display DOT without Graphviz " +
                "(default: from the --output extension, else svg)",
        };
        var run = new Option<string?>("--run") { Description = "Color stages by status from this run directory: finished stages from its checkpoint and events, running stages from events.jsonl" };
        var command = new Command("render", "Render a pipeline graph with Graphviz") { file, output, format, run };
        command.SetAction(async (result, cancellationToken) =>
        {
            var path = result.GetValue(file)!;
            if (!RenderFormat.TryResolve(result.GetValue(format), result.GetValue(output), out var fmt, out var formatError))
            {
                await Console.Error.WriteLineAsync("✖ " + formatError).ConfigureAwait(false);
                return ExitCodes.InvalidInput;
            }

            var target = result.GetValue(output) ?? (path == "-" ? "-" : RenderFormat.DefaultOutput(path, fmt));
            if (target != "-" && path != "-" && string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                await Console.Error.WriteLineAsync($"✖ Refusing to overwrite the pipeline file {Path.GetFullPath(path)} with its rendering; choose another --output.").ConfigureAwait(false);
                return ExitCodes.InvalidInput;
            }

            Graph graph;
            try
            {
                graph = new PipelineRunner(new PipelineRunnerOptions()).Prepare(DotParser.Parse(await ReadSourceAsync(path, cancellationToken).ConfigureAwait(false))).Graph;
            }
            catch (Exception ex) when (ex is IOException or DotParseException)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitCodes.InvalidInput;
            }

            RunProgress? progress = null;
            if (result.GetValue(run) is { } runDir)
            {
                try
                {
                    progress = RunProgress.Load(Path.GetFullPath(runDir));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
                {
                    await Console.Error.WriteLineAsync("✖ " + ex.Message).ConfigureAwait(false);
                    return ExitCodes.InvalidInput;
                }
            }

            var dot = GraphRenderer.ToDisplayDot(graph, progress?.Statuses, progress?.Running);
            byte[] bytes;
            try
            {
                bytes = fmt == "dot" ? System.Text.Encoding.UTF8.GetBytes(dot) : await GraphRenderer.RenderAsync(dot, fmt, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (GraphvizException ex)
            {
                var hint = ex.InnerException is System.ComponentModel.Win32Exception ? " (--format dot writes the display DOT without Graphviz.)" : "";
                await Console.Error.WriteLineAsync($"✖ {ex.Message}{hint}").ConfigureAwait(false);
                return ExitCodes.Configuration;
            }

            if (target == "-")
            {
                await using var stdout = Console.OpenStandardOutput();
                await stdout.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Console.Error.WriteLineAsync("✖ " + ex.Message).ConfigureAwait(false);
                    return ExitCodes.InvalidInput;
                }

                Console.WriteLine($"Wrote {Path.GetFullPath(target)} ({fmt}" + (progress is null ? ")" : $", {Describe(progress)})"));
            }

            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>A one-line summary of a run's progress for <c>reactor render --run</c>.</summary>
    private static string Describe(RunProgress progress) =>
        $"{progress.Statuses.Count.ToString(CultureInfo.InvariantCulture)} stage(s) finished, " +
        (progress.Running.Count > 0 ? "running: " + string.Join(", ", progress.Running) : progress.Finished ? "run ended" : "none running");

    /// <summary>Shared run/resume flow: compose backends, run, report, and map the result to an exit code.</summary>
    private static async Task<int> ExecuteAsync(
        RunOptionSet options,
        ParseResult result,
        ConsoleReporter reporter,
        string workDir,
        CancellationToken cancellationToken,
        Func<PipelineRunner, RunOptions, CancellationToken, Task<PipelineResult?>> body)
    {
        if (!Directory.Exists(workDir))
        {
            reporter.Warn($"Working directory '{workDir}' does not exist.");
            return ExitCodes.InvalidInput;
        }

        Dictionary<string, object?> initialContext;
        try
        {
            initialContext = options.InitialContext(result);
        }
        catch (FormatException ex)
        {
            reporter.Warn(ex.Message);
            return ExitCodes.InvalidInput;
        }

        BackendSelection selection;
        try
        {
            selection = BackendFactory.Create(options.Backends(result));
        }
        catch (ConfigurationError ex)
        {
            reporter.Warn("✖ " + ex.Message);
            return ExitCodes.Configuration;
        }

        await using (selection.ConfigureAwait(false))
        {
            reporter.Info($"backend: {selection.Description}");
            var runner = new PipelineRunner(new PipelineRunnerOptions
            {
                Backend = selection.Backend,
                Interviewer = options.Interviewer(result),
                LogsBaseDirectory = RunsDirectory(workDir),

                // Tool stages change files for real even with the simulate backend.
                RealWorkspace = true,
            });
            var runOptions = new RunOptions
            {
                WorkingDirectory = workDir,
                InitialContext = initialContext,
                OnEvent = reporter.OnEvent,
                MaxSteps = Math.Max(0, result.GetValue(options.MaxSteps)),
                MaxCostUsd = result.GetValue(options.MaxCost),
                TrackWorkspaceChanges = result.GetValue(options.NoTrackChanges) ? false : null,

                // A snapshot after each stage lets the Studio re-fly (fork) CLI runs from any stage.
                KeepCheckpointHistory = true,
            };

            PipelineResult? outcome;
            try
            {
                outcome = await body(runner, runOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (ValidationException ex)
            {
                PrintDiagnostics(reporter, ex.Diagnostics);
                return ExitCodes.InvalidInput;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                reporter.Warn("Cancelled.");
                return ExitCodes.Cancelled;
            }

            if (outcome is null)
            {
                return ExitCodes.InvalidInput;
            }

            reporter.Summary(outcome);
            return outcome.Cancelled ? ExitCodes.Cancelled
                : outcome.Status is StageStatus.Success or StageStatus.PartialSuccess ? ExitCodes.Success
                : ExitCodes.PipelineFailed;
        }
    }

    /// <summary>Prints diagnostics; returns false when any is an error.</summary>
    internal static bool PrintDiagnostics(ConsoleReporter reporter, IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var location = diagnostic.NodeId is { } node ? $" ({node})" : diagnostic.Edge is { } edge ? $" ({edge.From} -> {edge.To})" : "";
            var line = $"{(diagnostic.Severity == Severity.Error ? "✖ error" : diagnostic.Severity == Severity.Warning ? "⚠ warning" : "ℹ info")} [{diagnostic.Rule}] {diagnostic.Message}{location}";
            if (diagnostic.Fix is { Length: > 0 } fix)
            {
                line += $"\n    fix: {fix}";
            }

            if (diagnostic.Severity == Severity.Error)
            {
                reporter.Warn(line);
            }
            else
            {
                reporter.Info(line);
            }
        }

        return !diagnostics.Any(d => d.Severity == Severity.Error);
    }

    internal static async Task<string> ReadSourceAsync(string path, CancellationToken cancellationToken)
    {
        if (path == "-")
        {
            return await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Pipeline file not found: {Path.GetFullPath(path)}");
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    internal static string ReactorDirectory(string workDir) => Path.Combine(workDir, ".reactor");

    internal static string RunsDirectory(string workDir) => Path.Combine(ReactorDirectory(workDir), "runs");

    /// <summary>
    /// The default run directory name: <c>{UTC timestamp yyyyMMdd-HHmmss}-{graph}</c>. The timestamp is UTC, like the
    /// names the engine and the server give their runs, so CLI and server runs sharing a runs folder sort by start time.
    /// </summary>
    internal static string RunDirectoryName(string graphName, DateTime utcNow) =>
        $"{(utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : utcNow).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Sanitize(graphName)}";

    /// <summary><paramref name="name"/> under <paramref name="parent"/>, suffixed (-2, -3, …) if that run directory exists.</summary>
    internal static string UniqueRunDirectory(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        for (var suffix = 2; Directory.Exists(path); suffix++)
        {
            path = Path.Combine(parent, $"{name}-{suffix.ToString(CultureInfo.InvariantCulture)}");
        }

        return path;
    }

    /// <summary>Creates <c>.reactor/runs/</c> (and <c>.reactor/.gitignore</c>) in <paramref name="workDir"/>.</summary>
    internal static void EnsureRunsDirectory(string workDir)
    {
        EnsureReactorDirectory(workDir);
        Directory.CreateDirectory(RunsDirectory(workDir));
    }

    /// <summary>Creates <c>.reactor/</c> with a <c>.gitignore</c> so run logs never end up in commits.</summary>
    internal static void EnsureReactorDirectory(string workDir)
    {
        var root = ReactorDirectory(workDir);
        Directory.CreateDirectory(root);
        var ignore = Path.Combine(root, ".gitignore");
        if (!File.Exists(ignore))
        {
            File.WriteAllText(ignore, "# Reactor run logs and checkpoints\n*\n");
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or below it (both full paths).</summary>
    internal static bool IsInside(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    internal static string Sanitize(string name)
    {
        var clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return clean.Length == 0 ? "pipeline" : clean.Length > 40 ? clean[..40] : clean;
    }

    internal static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
