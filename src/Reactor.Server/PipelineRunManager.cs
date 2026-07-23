using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;
using Reactor.Validation;

namespace Reactor.Server;

/// <summary>Configuration for HTTP server mode.</summary>
public sealed record ReactorServerOptions
{
    /// <summary>Backend for codergen stages (null = simulation mode). Owned by the caller: the server never disposes it.</summary>
    public ICodergenBackend? Backend { get; init; }

    /// <summary>Where run directories are created (relative paths resolve against <see cref="WorkingDirectory"/>).</summary>
    public string LogsBaseDirectory { get; init; } = Path.Combine(".reactor", "runs");

    /// <summary>Default working directory for runs (tools, agents, child pipelines).</summary>
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>Allow API clients to choose a run's working directory (off by default).</summary>
    public bool AllowClientWorkingDirectory { get; init; }

    /// <summary>When set, API requests must carry <c>Authorization: Bearer &lt;token&gt;</c> (or <c>?access_token=</c>).</summary>
    public string? AuthToken { get; init; }

    /// <summary>Runs executing at once; further runs wait in the <c>queued</c> state.</summary>
    public int MaxConcurrentRuns { get; init; } = 4;

    /// <summary>Events kept in memory per run for SSE replay (the full log is always in <c>events.jsonl</c>).</summary>
    public int MaxEventsPerRun { get; init; } = 50_000;

    /// <summary>Path to Graphviz <c>dot</c> (default: on PATH).</summary>
    public string? GraphvizPath { get; init; }

    /// <summary>Register custom handlers, transforms, or lint rules on the shared runner.</summary>
    public Action<PipelineRunner>? ConfigureRunner { get; init; }

    /// <summary>Stage executions allowed per run before it fails (guards against runaway loops; 0 = unlimited).</summary>
    public int MaxStepsPerRun { get; init; } = 1000;

    /// <summary>Maximum DOT source size accepted (default 1 MiB).</summary>
    public int MaxDotBytes { get; init; } = 1024 * 1024;

    /// <summary>The default backend kind (e.g. <c>copilot</c>, <c>agent</c>, <c>simulate</c>), shown in the Studio.</summary>
    public string? BackendKind { get; init; }

    /// <summary>Human-readable description of the configured backend, shown in the Studio.</summary>
    public string? BackendDescription { get; init; }

    /// <summary>Backend route names that nodes may select with <c>backend="..."</c>.</summary>
    public IReadOnlyList<string> BackendRoutes { get; init; } = [];

    /// <summary>Lists the models stages can use (catalog and account models), for the Studio's model pickers.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<ModelDescriptor>>>? ListModels { get; init; }
}

/// <summary>A model offered to pipeline authors by the Studio.</summary>
public sealed record ModelDescriptor
{
    public required string Id { get; init; }

    /// <summary>Provider name (<c>openai</c>, <c>anthropic</c>, ...), or <c>copilot</c> for GitHub Copilot account models.</summary>
    public required string Provider { get; init; }

    /// <summary>Where the entry came from: <c>catalog</c> or <c>copilot</c>.</summary>
    public required string Source { get; init; }

    public string? DisplayName { get; init; }

    public long? ContextWindow { get; init; }

    public IReadOnlyList<string> ReasoningEfforts { get; init; } = [];

    public bool? SupportsTools { get; init; }

    public bool? SupportsReasoning { get; init; }

    /// <summary>Copilot billing multiplier, when known.</summary>
    public double? BillingMultiplier { get; init; }

    /// <summary>Copilot policy state (<c>enabled</c>, <c>disabled</c>, ...), when known.</summary>
    public string? PolicyState { get; init; }

    /// <summary>False when the model's provider is not configured (for example no API key).</summary>
    public bool Available { get; init; } = true;
}

/// <summary>Run states reported by the API.</summary>
public static class RunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Success = "success";
    public const string PartialSuccess = "partial_success";
    public const string Fail = "fail";
    public const string Cancelled = "cancelled";
    public const string Error = "error";

    public static bool IsTerminal(string status) => status is Success or PartialSuccess or Fail or Cancelled or Error;
}

/// <summary>A pipeline event with its per-run sequence number (the SSE event id).</summary>
public sealed record RunEvent(long Sequence, PipelineEvent Event);

/// <summary>A pipeline run managed by the server: progress, events, pending questions, and the result.</summary>
public sealed class ManagedRun
{
    private readonly object _gate = new();
    private readonly LinkedList<RunEvent> _events = new();
    private readonly List<Channel<RunEvent>> _subscribers = [];
    private readonly Dictionary<string, string> _nodeStatuses = new(StringComparer.Ordinal);
    private readonly List<string> _completedNodes = [];
    private readonly List<string> _activeNodes = [];
    private readonly int _maxEvents;
    private readonly string? _studio;
    private long _sequence;
    private bool _eventsCompleted;

    internal ManagedRun(
        string id,
        Graph graph,
        string source,
        string logsRoot,
        string workingDirectory,
        IReadOnlyDictionary<string, object?>? initialContext,
        int maxEvents,
        System.Text.Json.Nodes.JsonObject? studio = null)
    {
        Id = id;
        Graph = graph;
        Source = source;
        LogsRoot = logsRoot;
        CurrentLogsRoot = logsRoot;
        WorkingDirectory = workingDirectory;
        InitialContext = initialContext;
        _studio = studio?.ToJsonString();
        _maxEvents = maxEvents;
    }

    public string Id { get; }

    public Graph Graph { get; }

    public string Name => Graph.Name;

    public string Goal => Graph.Goal;

    /// <summary>The submitted DOT source.</summary>
    public string Source { get; }

    public string LogsRoot { get; }

    /// <summary>The logs root currently in use (changes after a <c>loop_restart</c>).</summary>
    public string CurrentLogsRoot { get; private set; }

    public string WorkingDirectory { get; }

    public IReadOnlyDictionary<string, object?>? InitialContext { get; }

    /// <summary>
    /// Studio metadata saved with the run as <c>studio.json</c> (the <c>"studio"</c> object of the start request, plus
    /// <c>forked_from</c> for forks and <c>simulated</c> for simulated runs), or null. Each read returns a new copy.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject? Studio => _studio is null ? null : System.Text.Json.Nodes.JsonNode.Parse(_studio) as System.Text.Json.Nodes.JsonObject;

    /// <summary>Simulation settings when the run was started with a <c>"simulation"</c> object; null for real runs.</summary>
    public RunSimulation? Simulation { get; internal init; }

    public WebInterviewer Interviewer { get; } = new();

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string Status { get; private set; } = RunStatus.Queued;

    public string? CurrentNode { get; private set; }

    public PipelineResult? Result { get; private set; }

    public string? Error { get; private set; }

    public long EventCount => Interlocked.Read(ref _sequence);

    internal CancellationTokenSource Cancellation { get; set; } = new();

    internal Task Execution { get; set; } = Task.CompletedTask;

    public IReadOnlyList<string> CompletedNodes
    {
        get
        {
            lock (_gate)
            {
                return _completedNodes.ToList();
            }
        }
    }

    /// <summary>Latest status per node (wire names).</summary>
    public IReadOnlyDictionary<string, string> NodeStatuses
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_nodeStatuses, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Stages executing right now, oldest first (several while parallel branches run).</summary>
    public IReadOnlyList<string> ActiveNodes
    {
        get
        {
            lock (_gate)
            {
                return _activeNodes.ToList();
            }
        }
    }

    /// <summary>Waits for the run (including a resumed run) to finish.</summary>
    public Task WaitAsync(CancellationToken cancellationToken = default) => Execution.WaitAsync(cancellationToken);

    /// <summary>
    /// Events after <paramref name="afterSequence"/>: the in-memory backlog first, then live events until the run ends.
    /// </summary>
    public async IAsyncEnumerable<RunEvent> ReadEventsAsync(long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<RunEvent> backlog;
        Channel<RunEvent>? channel = null;
        lock (_gate)
        {
            backlog = _events.Where(e => e.Sequence > afterSequence).ToList();
            if (!_eventsCompleted)
            {
                channel = Channel.CreateUnbounded<RunEvent>(new UnboundedChannelOptions { SingleReader = true });
                _subscribers.Add(channel);
            }
        }

        try
        {
            var last = afterSequence;
            foreach (var evt in backlog)
            {
                last = evt.Sequence;
                yield return evt;
            }

            if (channel is null)
            {
                yield break;
            }

            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (evt.Sequence > last)
                {
                    last = evt.Sequence;
                    yield return evt;
                }
            }
        }
        finally
        {
            if (channel is not null)
            {
                lock (_gate)
                {
                    _subscribers.Remove(channel);
                }
            }
        }
    }

    internal void Record(PipelineEvent pipelineEvent)
    {
        lock (_gate)
        {
            var evt = new RunEvent(++_sequence, pipelineEvent);
            _events.AddLast(evt);
            while (_events.Count > _maxEvents)
            {
                _events.RemoveFirst();
            }

            switch (pipelineEvent)
            {
                case StageStarted started:
                    CurrentNode = started.NodeId;
                    if (!_activeNodes.Contains(started.NodeId))
                    {
                        _activeNodes.Add(started.NodeId);
                    }

                    break;
                case StageCompleted completed:
                    _nodeStatuses[completed.NodeId] = completed.Status;
                    _completedNodes.Add(completed.NodeId);
                    _activeNodes.Remove(completed.NodeId);
                    if (CurrentNode == completed.NodeId)
                    {
                        CurrentNode = null;
                    }

                    break;
                case StageFailed { WillRetry: false } failed:
                    _nodeStatuses[failed.NodeId] = "fail";
                    break;
                case LoopRestarted restarted:
                    CurrentLogsRoot = restarted.LogsRoot;
                    _nodeStatuses.Clear();
                    _completedNodes.Clear();
                    _activeNodes.Clear();
                    break;
            }

            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(evt);
            }
        }
    }

    /// <summary>
    /// Adds a host-generated event (for example a note that a user queued steering) to the live event journal, but only
    /// while the run is executing, so it can never appear after the run's final events. Like engine events it gets the
    /// next sequence number and reaches SSE subscribers; unlike them it is not written to the run's <c>events.jsonl</c>.
    /// </summary>
    internal bool TryRecordNote(PipelineEvent pipelineEvent)
    {
        lock (_gate)
        {
            if (Status != RunStatus.Running || _eventsCompleted)
            {
                return false;
            }

            Record(pipelineEvent);
            return true;
        }
    }

    internal void MarkRunning()
    {
        lock (_gate)
        {
            Status = RunStatus.Running;
            StartedAt ??= DateTimeOffset.UtcNow;
            FinishedAt = null;
            Error = null;
        }
    }

    internal void Finish(string status, PipelineResult? result, string? error)
    {
        lock (_gate)
        {
            Status = status;
            Result = result ?? Result;
            Error = error;
            FinishedAt = DateTimeOffset.UtcNow;
            CurrentNode = null;
            _activeNodes.Clear();
            if (result is not null)
            {
                foreach (var (node, outcome) in result.NodeOutcomes)
                {
                    _nodeStatuses[node] = outcome.Status.ToWire();
                }
            }
        }
    }

    internal void CompleteEvents()
    {
        lock (_gate)
        {
            _eventsCompleted = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// Atomically moves a finished, unsuccessful run back to <c>queued</c> for a resume, so concurrent resume requests cannot
    /// start two executions on the same run directory.
    /// </summary>
    internal bool TryBeginResume(CancellationTokenSource cancellation, out string? error)
    {
        lock (_gate)
        {
            if (!RunStatus.IsTerminal(Status))
            {
                error = "The run is still active.";
                return false;
            }

            if (Status == RunStatus.Success)
            {
                error = "The run already succeeded.";
                return false;
            }

            Cancellation = cancellation;
            Status = RunStatus.Queued;
            FinishedAt = null;
            Error = null;
            _eventsCompleted = false;
            error = null;
            return true;
        }
    }
}

/// <summary>Outcome of starting (or resuming) a run.</summary>
public sealed record StartResult(ManagedRun? Run, IReadOnlyList<Diagnostic> Diagnostics, string? Error)
{
    public bool Succeeded => Run is not null;
}

/// <summary>A request to start a pipeline.</summary>
public sealed record StartRequest
{
    public required string Dot { get; init; }

    public IReadOnlyDictionary<string, object?>? Context { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// A working directory chosen by the server itself (the Architect's scratch directory), created if needed and exempt
    /// from <see cref="ReactorServerOptions.AllowClientWorkingDirectory"/>; takes precedence over <see cref="WorkingDirectory"/>.
    /// </summary>
    internal string? ServerWorkingDirectory { get; init; }

    /// <summary>Optional simulation settings (<c>"simulation"</c> in the JSON body): run with simulated stages instead of the backend.</summary>
    public System.Text.Json.Nodes.JsonObject? Simulation { get; init; }

    /// <summary>
    /// Optional Studio metadata (<c>"studio"</c> in the JSON body), e.g. the source file and layout of the pipeline. It is
    /// saved with the run as <c>studio.json</c> (at most <see cref="PipelineRunManager.MaxStudioBytes"/>).
    /// </summary>
    public System.Text.Json.Nodes.JsonObject? Studio { get; init; }
}

/// <summary>A request to start a run that continues from a saved checkpoint, for example a fork of an earlier run.</summary>
public sealed record CheckpointStartRequest
{
    /// <summary>The pipeline to run; it may differ from the pipeline that produced the checkpoint.</summary>
    public required string Dot { get; init; }

    /// <summary>
    /// The state to continue from. The run continues after <see cref="Checkpoint.CurrentNode"/>, routing on that stage's
    /// recorded outcome; the checkpoint's run id and final status are replaced.
    /// </summary>
    public required Checkpoint Checkpoint { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Studio metadata saved with the run as <c>studio.json</c> (for forks, <c>forked_from</c> and the source run's fields).</summary>
    public System.Text.Json.Nodes.JsonObject? Studio { get; init; }
}

/// <summary>Starts, tracks, cancels, and resumes pipeline runs for the HTTP server.</summary>
public sealed class PipelineRunManager : IAsyncDisposable
{
    /// <summary>Largest Studio metadata object (serialized) accepted with a run.</summary>
    public const int MaxStudioBytes = 4 * 1024 * 1024;

    /// <summary>Name of the Studio metadata file in a run directory.</summary>
    public const string StudioFileName = "studio.json";

    private static readonly System.Text.Json.JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private readonly ReactorServerOptions _options;
    private readonly ConcurrentDictionary<string, ManagedRun> _runs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private int _disposed;

    public PipelineRunManager(ReactorServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRuns));
        Runner = new PipelineRunner(new PipelineRunnerOptions
        {
            Backend = options.Backend,
            LogsBaseDirectory = ResolveBase(options),
        });
        options.ConfigureRunner?.Invoke(Runner);
    }

    public PipelineRunner Runner { get; }

    public ReactorServerOptions Options => _options;

    /// <summary>The directory that holds run directories (<see cref="ReactorServerOptions.LogsBaseDirectory"/>, resolved).</summary>
    public string RunsDirectory => Runner.Options.LogsBaseDirectory;

    /// <summary>Where simulated runs' directories go (<c>{working directory}/.reactor/simulations</c>), apart from real runs.</summary>
    public string SimulationsDirectory => Path.Combine(Path.GetFullPath(_options.WorkingDirectory), ".reactor", "simulations");

    /// <summary>All runs, newest first.</summary>
    public IReadOnlyList<ManagedRun> Runs => _runs.Values.OrderByDescending(r => r.CreatedAt).ToList();

    public ManagedRun? Get(string id) => _runs.GetValueOrDefault(id);

    /// <summary>Parses and validates the pipeline, then starts it (or queues it when all slots are busy).</summary>
    public StartResult Start(StartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PreparedPipeline prepared;
        try
        {
            prepared = Runner.Prepare(DotParser.Parse(request.Dot));
        }
        catch (DotParseException ex)
        {
            return new StartResult(null, [], ex.Message);
        }

        if (prepared.HasErrors)
        {
            return new StartResult(null, prepared.Diagnostics, "The pipeline has validation errors.");
        }

        RunSimulation? simulation = null;
        if (request.Simulation is { } simulationJson && !RunSimulation.TryParse(simulationJson, out simulation, out var simulationErrors))
        {
            return new StartResult(null, simulationErrors, "The simulation settings are invalid.");
        }

        string workingDirectory;
        if (request.ServerWorkingDirectory is { } serverDirectory)
        {
            workingDirectory = Path.GetFullPath(serverDirectory);
            try
            {
                Directory.CreateDirectory(workingDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new StartResult(null, prepared.Diagnostics, $"Could not create the working directory: {ex.Message}");
            }
        }
        else if (!TryResolveWorkingDirectory(request.WorkingDirectory, out workingDirectory, out var error))
        {
            return new StartResult(null, prepared.Diagnostics, error);
        }

        var studio = request.Studio?.DeepClone().AsObject();
        if (simulation is not null)
        {
            studio ??= [];
            studio["simulated"] = true;
        }

        if (StudioTooLarge(studio))
        {
            return new StartResult(null, prepared.Diagnostics, $"The studio metadata exceeds {MaxStudioBytes} bytes.");
        }

        var id = NewRunId();
        var logsRoot = simulation is null
            ? NewLogsRoot(prepared.Graph.Name, id)
            : Path.Combine(SimulationsDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Sanitize(prepared.Graph.Name)}-{id}");
        try
        {
            WriteStudioFile(logsRoot, studio);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StartResult(null, prepared.Diagnostics, $"Could not create the run directory: {ex.Message}");
        }

        var run = new ManagedRun(id, prepared.Graph, request.Dot, logsRoot, workingDirectory, request.Context, Math.Max(100, _options.MaxEventsPerRun), studio)
        {
            Simulation = simulation,
        };
        _runs[id] = run;
        run.Execution = simulation is { } simulated
            ? ExecuteAsync(run, (options, ct) => CreateSimulationRunner(simulated).RunAsync(prepared.Graph, simulated.Apply(options, prepared.Graph), ct))
            : ExecuteAsync(run, (options, ct) => Runner.RunAsync(prepared.Graph, options, ct));
        return new StartResult(run, prepared.Diagnostics, null);
    }

    /// <summary>
    /// Starts a new run that continues from a checkpoint (for example a fork of an earlier run from one of its stage
    /// snapshots): validates the pipeline, creates a run directory with <c>graph.dot</c>, <c>checkpoint.json</c> (with the
    /// new run id, no final status), <c>manifest.json</c>, and <c>studio.json</c>, then resumes it like
    /// <see cref="Resume"/>. The run is listed, streamed, cancelled, and answered like any other run.
    /// </summary>
    public StartResult StartFromCheckpoint(CheckpointStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PreparedPipeline prepared;
        try
        {
            prepared = Runner.Prepare(DotParser.Parse(request.Dot));
        }
        catch (DotParseException ex)
        {
            return new StartResult(null, [], ex.Message);
        }

        if (prepared.HasErrors)
        {
            return new StartResult(null, prepared.Diagnostics, "The pipeline has validation errors.");
        }

        var source = request.Checkpoint;
        if (source.CurrentNode.Length > 0 && !prepared.Graph.ContainsNode(source.CurrentNode))
        {
            return new StartResult(null, prepared.Diagnostics, $"The checkpoint's stage '{source.CurrentNode}' does not exist in the pipeline.");
        }

        if (!TryResolveWorkingDirectory(request.WorkingDirectory, out var workingDirectory, out var error))
        {
            return new StartResult(null, prepared.Diagnostics, error);
        }

        var studio = request.Studio?.DeepClone().AsObject();
        if (StudioTooLarge(studio))
        {
            return new StartResult(null, prepared.Diagnostics, $"The studio metadata exceeds {MaxStudioBytes} bytes.");
        }

        var id = NewRunId();
        var logsRoot = NewLogsRoot(prepared.Graph.Name, id);
        var checkpoint = Checkpoint.FromJson(source.ToJson());
        checkpoint.RunId = id;
        checkpoint.FinalStatus = null;
        checkpoint.ContextValues["internal.run_id"] = id;
        try
        {
            Directory.CreateDirectory(logsRoot);
            File.WriteAllText(Path.Combine(logsRoot, "graph.dot"), request.Dot);
            checkpoint.Save(Path.Combine(logsRoot, Checkpoint.FileName));
            WriteJsonFile(Path.Combine(logsRoot, "manifest.json"), new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = prepared.Graph.Name,
                ["label"] = prepared.Graph.Label,
                ["goal"] = prepared.Graph.Goal,
                ["run_id"] = id,
                ["started_at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["logs_root"] = logsRoot,
                ["working_directory"] = workingDirectory,
                ["node_count"] = prepared.Graph.Nodes.Count,
                ["edge_count"] = prepared.Graph.Edges.Count,
                ["loop_restarts"] = checkpoint.LoopRestarts,
                ["reactor_version"] = PipelineRunner.Version,
                ["resumed_from"] = source.CurrentNode,
            });
            WriteStudioFile(logsRoot, studio);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StartResult(null, prepared.Diagnostics, $"Could not create the run directory: {ex.Message}");
        }

        var run = new ManagedRun(id, prepared.Graph, request.Dot, logsRoot, workingDirectory, null, Math.Max(100, _options.MaxEventsPerRun), studio);
        _runs[id] = run;
        run.Execution = ExecuteAsync(run, (options, ct) => Runner.ResumeAsync(logsRoot, options, ct));
        return new StartResult(run, prepared.Diagnostics, null);
    }

    /// <summary>Requests cancellation; false when the run is unknown or already finished.</summary>
    public bool Cancel(string id)
    {
        if (!_runs.TryGetValue(id, out var run) || RunStatus.IsTerminal(run.Status))
        {
            return false;
        }

        run.Cancellation.Cancel();
        return true;
    }

    /// <summary>Resumes a finished, unsuccessful run from its last checkpoint.</summary>
    public StartResult Resume(string id)
    {
        if (!_runs.TryGetValue(id, out var run))
        {
            return new StartResult(null, [], $"Unknown run '{id}'.");
        }

        if (!File.Exists(Path.Combine(run.CurrentLogsRoot, "checkpoint.json")))
        {
            return new StartResult(null, [], RunStatus.IsTerminal(run.Status) ? "The run has no checkpoint to resume from." : "The run is still active.");
        }

        if (!run.TryBeginResume(new CancellationTokenSource(), out var error))
        {
            return new StartResult(null, [], error);
        }

        var logsRoot = run.CurrentLogsRoot;
        run.Execution = run.Simulation is { } simulation
            ? ExecuteAsync(run, (options, ct) => CreateSimulationRunner(simulation).ResumeAsync(logsRoot, simulation.Apply(options with { LogsRoot = logsRoot }, run.Graph), ct))
            : ExecuteAsync(run, (options, ct) => Runner.ResumeAsync(logsRoot, options with { LogsRoot = logsRoot }, ct));
        return new StartResult(run, [], null);
    }

    /// <summary>Handler types registered on <see cref="Runner"/> beyond the built-in ones (simulated like codergen stages).</summary>
    public IReadOnlyList<string> CustomHandlerTypes() =>
        Runner.Handlers.Types.Where(t => !HandlerTypes.BuiltIn.Contains(t)).Order(StringComparer.Ordinal).ToList();

    /// <summary>Cancels active runs and waits (up to 30 s) for them to stop. The backend is owned by the caller.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var run in _runs.Values)
        {
            if (!RunStatus.IsTerminal(run.Status))
            {
                await run.Cancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        try
        {
            await Task.WhenAll(_runs.Values.Select(r => r.Execution)).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private async Task ExecuteAsync(ManagedRun run, Func<RunOptions, CancellationToken, Task<PipelineResult>> body)
    {
        var token = run.Cancellation.Token;
        await Task.Yield();
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            run.Finish(RunStatus.Cancelled, null, null);
            run.CompleteEvents();
            return;
        }

        try
        {
            run.MarkRunning();
            var options = new RunOptions
            {
                RunId = run.Id,
                LogsRoot = run.LogsRoot,
                WorkingDirectory = run.WorkingDirectory,
                Interviewer = run.Interviewer,
                InitialContext = run.InitialContext,
                OnEvent = run.Record,
                MaxSteps = Math.Max(0, _options.MaxStepsPerRun),
                KeepCheckpointHistory = true,
            };
            var result = await body(options, token).ConfigureAwait(false);
            run.Finish(StatusOf(result), result, result.Cancelled ? null : FailureOf(result));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            run.Finish(RunStatus.Cancelled, null, null);
        }
        catch (Exception ex)
        {
            run.Finish(RunStatus.Error, null, ex.Message);
        }
        finally
        {
            _slots.Release();
            run.CompleteEvents();
        }
    }

    private static string StatusOf(PipelineResult result) =>
        result.Cancelled ? RunStatus.Cancelled : result.Status switch
        {
            StageStatus.Success => RunStatus.Success,
            StageStatus.PartialSuccess => RunStatus.PartialSuccess,
            _ => RunStatus.Fail,
        };

    private static string? FailureOf(PipelineResult result) =>
        result.Status is StageStatus.Success or StageStatus.PartialSuccess || string.IsNullOrWhiteSpace(result.FinalOutcome.FailureReason)
            ? null
            : result.FinalOutcome.FailureReason;

    /// <summary>
    /// A runner like <see cref="Runner"/> (options, transforms, lint rules, custom handler types) whose stages are simulated;
    /// one per execution, because simulated handlers carry the run's draws.
    /// </summary>
    private PipelineRunner CreateSimulationRunner(RunSimulation simulation)
    {
        var runner = simulation.Sample.CreateRunner(Runner.Options with { LogsBaseDirectory = SimulationsDirectory }, CustomHandlerTypes());
        foreach (var transform in Runner.Transforms)
        {
            runner.RegisterTransform(transform);
        }

        foreach (var rule in Runner.LintRules)
        {
            runner.RegisterLintRule(rule);
        }

        return runner;
    }

    private static string ResolveBase(ReactorServerOptions options) =>
        Path.GetFullPath(Path.IsPathRooted(options.LogsBaseDirectory) ? options.LogsBaseDirectory : Path.Combine(options.WorkingDirectory, options.LogsBaseDirectory));

    private static string NewRunId() => Guid.NewGuid().ToString("N")[..12];

    private string NewLogsRoot(string graphName, string id) =>
        Path.Combine(ResolveBase(_options), $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Sanitize(graphName)}-{id}");

    private bool TryResolveWorkingDirectory(string? requested, out string workingDirectory, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            workingDirectory = Path.GetFullPath(_options.WorkingDirectory);
            return true;
        }

        workingDirectory = "";
        if (!_options.AllowClientWorkingDirectory)
        {
            error = "This server does not allow clients to choose the working directory.";
            return false;
        }

        workingDirectory = Path.GetFullPath(requested);
        if (!Directory.Exists(workingDirectory))
        {
            error = $"Working directory '{workingDirectory}' does not exist.";
            return false;
        }

        return true;
    }

    private static bool StudioTooLarge(System.Text.Json.Nodes.JsonObject? studio) =>
        studio is not null && System.Text.Encoding.UTF8.GetByteCount(studio.ToJsonString()) > MaxStudioBytes;

    /// <summary>Creates the run directory and writes <c>studio.json</c> into it (nothing when there is no metadata).</summary>
    private static void WriteStudioFile(string logsRoot, System.Text.Json.Nodes.JsonObject? studio)
    {
        if (studio is null)
        {
            return;
        }

        Directory.CreateDirectory(logsRoot);
        WriteJsonFile(Path.Combine(logsRoot, StudioFileName), studio);
    }

    private static void WriteJsonFile(string path, System.Text.Json.Nodes.JsonNode json)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(IndentedJson));
        File.Move(temp, path, overwrite: true);
    }

    private static string Sanitize(string name)
    {
        var clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return clean.Length == 0 ? "pipeline" : clean.Length > 40 ? clean[..40] : clean;
    }
}
