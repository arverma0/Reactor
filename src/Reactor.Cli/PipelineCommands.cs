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

    public Option<string?> WorkingDirectory { get; } = new("--workdir", "-C") { Description = "Working directory for tools and agents (default: current directory)" };

    public Option<string[]> Set { get; } = new("--set", "-s") { Description = "Initial context value key=value (repeatable)", Arity = ArgumentArity.ZeroOrMore };

    public Option<bool> AutoApprove { get; } = new("--auto-approve", "-y") { Description = "Answer human gates automatically (first option / yes)" };

    public Option<string[]> Answers { get; } = new("--answer") { Description = "Pre-supplied human gate answers, used in order (repeatable)", Arity = ArgumentArity.ZeroOrMore };

    public Option<int> MaxSteps { get; } = new("--max-steps") { Description = "Fail the run after this many stage executions, a guard against runaway loops (0 = unlimited)", DefaultValueFactory = _ => 500 };

    public Option<bool> Json { get; } = new("--json") { Description = "Emit events and the result as JSON lines" };

    public Option<bool> Verbose { get; } = new("--verbose", "-v") { Description = "Show debug logs, edges, and checkpoints" };

    public Option<bool> Quiet { get; } = new("--quiet", "-q") { Description = "Only show stage results, warnings, and errors" };

    public Option<bool> NoColor { get; } = new("--no-color") { Description = "Disable colored output" };

    public Option<string?> CopilotRuntime { get; } = new("--copilot-runtime") { Description = "Path to a Copilot runtime for the copilot backend" };

    public Option<string?> CopilotCli { get; } = new("--copilot-cli") { Description = "Path to the copilot CLI (default: copilot on PATH, or $REACTOR_COPILOT_CLI)" };

    public Option<string?> ClaudeCli { get; } = new("--claude-cli") { Description = "Path to the claude CLI (default: claude on PATH, or $REACTOR_CLAUDE_CLI)" };

    public void AddTo(Command command)
    {
        foreach (var option in new Option[] { Backend, Model, Provider, WorkingDirectory, Set, AutoApprove, Answers, MaxSteps, Json, Verbose, Quiet, NoColor, CopilotRuntime, CopilotCli, ClaudeCli })
        {
            command.Options.Add(option);
        }

        Backend.AcceptOnlyFromAmong(BackendFactory.Kinds);
    }

    public BackendSettings Backends(ParseResult result) => new()
    {
        Kind = result.GetValue(Backend) ?? "auto",
        Model = result.GetValue(Model),
        Provider = result.GetValue(Provider),
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
        var logs = new Option<string?>("--logs") { Description = "Run directory (default: <workdir>/.reactor/runs/<timestamp>-<name>)" };
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

                var logsRoot = result.GetValue(logs) is { } explicitLogs
                    ? Path.GetFullPath(explicitLogs)
                    : UniqueRunDirectory(RunsDirectory(workDir), $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(prepared.Graph.Name)}");
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
        var command = new Command("validate", "Parse and lint a pipeline without running it") { file, json, noColor };
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

            if (result.GetValue(json))
            {
                Console.WriteLine(new JsonObject
                {
                    ["valid"] = !prepared.HasErrors,
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
                }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                var reporter = new ConsoleReporter(Console.Out, Verbosity.Normal, ConsoleReporter.ColorSupported(result.GetValue(noColor)), json: false);
                PrintDiagnostics(reporter, prepared.Diagnostics);
                if (!prepared.HasErrors)
                {
                    Console.WriteLine($"✔ {prepared.Graph.Name}: {prepared.Graph.Nodes.Count} nodes, {prepared.Graph.Edges.Count} edges — valid");
                }
            }

            return prepared.HasErrors ? ExitCodes.InvalidInput : ExitCodes.Success;
        });
        return command;
    }

    public static Command Render()
    {
        var file = new Argument<string>("file") { Description = "DOT pipeline file" };
        var output = new Option<string?>("--output", "-o") { Description = "Output file (default: <file>.<format>; '-' for stdout)" };
        var format = new Option<string>("--format", "-f") { Description = "svg, png, pdf, or dot", DefaultValueFactory = _ => "svg" };
        var run = new Option<string?>("--run") { Description = "Color stages by status from this run directory's checkpoint" };
        var command = new Command("render", "Render a pipeline graph with Graphviz") { file, output, format, run };
        command.SetAction(async (result, cancellationToken) =>
        {
            var path = result.GetValue(file)!;
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

            IReadOnlyDictionary<string, string>? statuses = null;
            string? current = null;
            if (result.GetValue(run) is { } runDir)
            {
                var checkpoint = Checkpoint.Load(Path.Combine(Path.GetFullPath(runDir), "checkpoint.json"));
                statuses = GraphRenderer.StatusesFrom(checkpoint.NodeOutcomes);
                current = checkpoint.FinalStatus is null ? checkpoint.CurrentNode : null;
            }

            var dot = GraphRenderer.ToDisplayDot(graph, statuses, current);
            var fmt = result.GetValue(format)!.ToLowerInvariant();
            var target = result.GetValue(output) ?? (path == "-" ? "-" : Path.ChangeExtension(path, fmt == "dot" ? ".rendered.dot" : "." + fmt));
            byte[] bytes;
            try
            {
                bytes = fmt == "dot" ? System.Text.Encoding.UTF8.GetBytes(dot) : await GraphRenderer.RenderAsync(dot, fmt, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (GraphvizException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitCodes.Configuration;
            }

            if (target == "-")
            {
                await using var stdout = Console.OpenStandardOutput();
                await stdout.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Wrote {Path.GetFullPath(target)}");
            }

            return ExitCodes.Success;
        });
        return command;
    }

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
            EnsureRunsDirectory(workDir);
            var runner = new PipelineRunner(new PipelineRunnerOptions
            {
                Backend = selection.Backend,
                Interviewer = options.Interviewer(result),
                LogsBaseDirectory = RunsDirectory(workDir),
            });
            var runOptions = new RunOptions
            {
                WorkingDirectory = workDir,
                InitialContext = initialContext,
                OnEvent = reporter.OnEvent,
                MaxSteps = Math.Max(0, result.GetValue(options.MaxSteps)),
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

    internal static string RunsDirectory(string workDir) => Path.Combine(workDir, ".reactor", "runs");

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

    /// <summary>Creates <c>.reactor/</c> with a <c>.gitignore</c> so run logs never end up in commits.</summary>
    internal static void EnsureRunsDirectory(string workDir)
    {
        var root = Path.Combine(workDir, ".reactor");
        Directory.CreateDirectory(Path.Combine(root, "runs"));
        var ignore = Path.Combine(root, ".gitignore");
        if (!File.Exists(ignore))
        {
            File.WriteAllText(ignore, "# Reactor run logs and checkpoints\n*\n");
        }
    }

    internal static string Sanitize(string name)
    {
        var clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return clean.Length == 0 ? "pipeline" : clean.Length > 40 ? clean[..40] : clean;
    }

    internal static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
