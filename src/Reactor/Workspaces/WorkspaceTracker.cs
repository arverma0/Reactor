using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reactor.Events;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Workspaces;

/// <summary>Settings of a <see cref="WorkspaceTracker"/>.</summary>
internal sealed record WorkspaceTrackerOptions
{
    public required string RunId { get; init; }

    /// <summary>The run directory (the first one, before any loop restart): holds <c>.workspace/</c> and <c>workspace.json</c>.</summary>
    public required string RunDirectory { get; init; }

    /// <summary>The run's working directory (the main workspace).</summary>
    public required string WorkingDirectory { get; init; }

    public required Graph Graph { get; init; }

    /// <summary>Emits events through the engine (stamped and logged like any other).</summary>
    public required Action<PipelineEvent> Emit { get; init; }

    /// <summary>
    /// A real run: the run state is persisted and the working directory is real (<see cref="PipelineRunnerOptions.RealWorkspace"/>;
    /// by default, when a codergen backend is configured). Only real runs snapshot workspaces or create branch workspaces;
    /// simulations never touch the working directory.
    /// </summary>
    public bool RealRun { get; init; }

    /// <summary>The run-level switch for <c>changes.json</c> (<see cref="PipelineRunnerOptions.TrackWorkspaceChanges"/>).</summary>
    public bool TrackChanges { get; init; } = true;

    /// <summary>File limit of file-walk snapshots.</summary>
    public int WalkFileLimit { get; init; } = WalkSnapshotter.DefaultFileLimit;
}

/// <summary>
/// The per-run workspace service (Reactor extension). For each stage visit that can change files it snapshots the stage's
/// working directory before and after (git private index, or a file walk outside git), writes the difference to the
/// stage's <c>changes.json</c>, emits <see cref="WorkspaceChanged"/>, and checks <c>write_scope</c>. It also serializes
/// <c>concurrency_group</c> stages, gives parallel branches their own workspaces (<c>branch_workdir</c>), promotes a
/// fan-in's best branch, and removes branch worktrees when the run ends. Every failure of its own is logged and leaves
/// the run alone (only <c>write_scope_enforce</c> and branch workspaces that cannot be created fail stages).
/// </summary>
internal sealed partial class WorkspaceTracker : IAsyncDisposable
{
    public const string ChangesFileName = "changes.json";
    public const string RunFileName = "workspace.json";
    public const string StateDirectoryName = ".workspace";

    /// <summary>Maximum changes listed in <c>changes.json</c> (counts are always complete).</summary>
    public const int MaxListedChanges = 2000;

    private const int SampleSize = 5;
    private const int MaxReportedViolations = 20;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly WorkspaceTrackerOptions _options;
    private readonly ConcurrentDictionary<string, Workspace> _workspaces = new(PathComparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groups = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string NodeId, int Index), StageWatch> _holds = new();
    private readonly ConcurrentDictionary<PipelineContext, StageWatch> _watches = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();
    private readonly JsonObject _runRecord = new();

    public WorkspaceTracker(WorkspaceTrackerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        RunDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RunDirectory));
        MainDirectory = Normalize(options.WorkingDirectory);
    }

    internal static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>True for real runs (persisted, in a real working directory); simulations never snapshot or create branch workspaces.</summary>
    public bool RealRun => _options.RealRun;

    public string RunDirectory { get; }

    public string MainDirectory { get; }

    /// <summary><c>&lt;run&gt;/.workspace</c>: private indexes, branch base manifests, and the owner file.</summary>
    public string StateDirectory => Path.Combine(RunDirectory, StateDirectoryName);

    /// <summary>
    /// True when a visit of <paramref name="node"/> gets a <c>changes.json</c>: a real run with tracking on (run option,
    /// then the node's <c>track_changes</c>, then the graph's — a node with a <c>write_scope</c> is tracked unless it says
    /// otherwise) for a node type that can change files.
    /// </summary>
    public bool Tracks(Node node, string handlerType)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!RealRun || !_options.TrackChanges || !CanChangeFiles(node, handlerType))
        {
            return false;
        }

        var graphSetting = AttributeValues.ParseBool(_options.Graph[WorkspaceAttributes.TrackChanges]) ?? true;
        return AttributeValues.ParseBool(node[WorkspaceAttributes.TrackChanges])
               ?? (graphSetting || !string.IsNullOrWhiteSpace(node[WorkspaceAttributes.WriteScope]));
    }

    /// <summary>
    /// False for node types that cannot change files (start, exit, conditional, human gates, parallel fan-outs — their
    /// branches are tracked stage by stage — and fan-ins without a judge prompt or promotion).
    /// </summary>
    public static bool CanChangeFiles(Node node, string handlerType) => handlerType switch
    {
        HandlerTypes.Start or HandlerTypes.Exit or HandlerTypes.Conditional or HandlerTypes.WaitHuman or HandlerTypes.Parallel => false,
        HandlerTypes.FanIn => !string.IsNullOrWhiteSpace(node.Prompt) || WorkspaceAttributes.Promotes(node),
        _ => true,
    };

    /// <summary>
    /// Starts a stage visit: waits for its <c>concurrency_group</c> and takes (or reuses) the "before" snapshot. Null when
    /// neither applies. Dispose the watch when the visit ends (it releases the group).
    /// </summary>
    public async Task<StageWatch?> BeginAsync(Node node, string handlerType, PipelineContext context, int index, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        var tracked = Tracks(node, handlerType);
        var group = handlerType == HandlerTypes.Parallel ? null : WorkspaceAttributes.ConcurrencyGroupOf(node);
        if (!tracked && RealRun && (handlerType is HandlerTypes.WaitHuman or HandlerTypes.Parallel || CanChangeFiles(node, handlerType)) &&
            _workspaces.TryGetValue(Normalize(context.WorkingDirectory), out var waited))
        {
            // The outside world may change files while a gate waits (branches may stop mid-write, and a stage with
            // track_changes=false may write anything): look again next time.
            lock (waited)
            {
                waited.Stale = true;
            }
        }

        if (!tracked && group is null)
        {
            return null;
        }

        var watch = new StageWatch(this, node, handlerType, context, index);
        try
        {
            if (group is not null)
            {
                await AcquireGroupAsync(watch, group, cancellationToken).ConfigureAwait(false);
            }

            if (tracked)
            {
                await StartTrackingAsync(watch, cancellationToken).ConfigureAwait(false);
            }

            return watch;
        }
        catch
        {
            watch.Dispose();
            throw;
        }
    }

    private async Task AcquireGroupAsync(StageWatch watch, string group, CancellationToken cancellationToken)
    {
        var semaphore = _groups.GetOrAdd(group, _ => new SemaphoreSlim(1, 1));
        if (!semaphore.Wait(0))
        {
            var waited = Stopwatch.StartNew();
            Log(watch.Node.Id, $"waiting for concurrency_group '{group}' (another stage of the group is running)");
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            Log(watch.Node.Id, $"concurrency_group '{group}' acquired after {AttributeValues.FormatDuration(waited.Elapsed)}");
        }

        watch.Group = semaphore;
    }

    private async Task StartTrackingAsync(StageWatch watch, CancellationToken cancellationToken)
    {
        var workspace = await GetWorkspaceAsync(watch.Context.WorkingDirectory, watch.Node.Id, cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            return;
        }

        bool reuse;
        lock (workspace)
        {
            foreach (var other in workspace.Active)
            {
                other.Concurrent.Add(watch);
                watch.Concurrent.Add(other);
            }

            // Nothing ran here since the last snapshot (the previous stage's "after"), so it is this stage's "before".
            reuse = workspace.Last is not null && !workspace.Stale && workspace.Active.Count == 0;
            workspace.Active.Add(watch);
            watch.Workspace = workspace;
            watch.Before = reuse ? workspace.Last : null;
        }

        _watches[watch.Context] = watch;
        if (AttributeValues.ParseBool(watch.Node[WorkspaceAttributes.WriteScopeEnforce]) == true)
        {
            // Hold the stage's StageCompleted until the scope is checked, so a violation can still turn it into a failure.
            _holds[(watch.Node.Id, watch.Index)] = watch;
        }

        if (!reuse)
        {
            watch.Before = await TakeSnapshotAsync(workspace, $"before {watch.Node.Id}", watch.Node.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends a tracked visit: takes the "after" snapshot, writes <c>changes.json</c>, emits <see cref="WorkspaceChanged"/>,
    /// and checks <c>write_scope</c>. Returns the outcome to record — a failure when <c>write_scope_enforce</c> is set
    /// and a successful stage changed files outside its scope (the failure is not retried).
    /// </summary>
    public async Task<Outcome> CompleteAsync(StageWatch watch, Outcome outcome, int visit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(outcome);
        if (watch.Workspace is not { } workspace)
        {
            watch.Completed = true;
            ReleaseHold(watch, null);
            return outcome;
        }

        var node = watch.Node;
        WorkspaceSnapshot? after = null;
        var changes = ChangeSet.Empty;
        string? error = null;
        if (watch.Before is { } before)
        {
            (after, var diff) = await TakeSnapshotAsync(workspace, before, $"after {node.Id} ({outcome.Status.ToWire()})", node.Id, cancellationToken).ConfigureAwait(false);
            if (after is null || diff is null)
            {
                error = "the snapshot after the stage failed";
            }
            else
            {
                changes = diff;
            }
        }
        else
        {
            error = "the snapshot before the stage failed";
        }

        List<StageWatch> concurrent;
        lock (workspace)
        {
            workspace.Active.Remove(watch);
            concurrent = [.. watch.Concurrent];
        }

        watch.Completed = true;
        _watches.TryRemove(new KeyValuePair<PipelineContext, StageWatch>(watch.Context, watch));

        // A path outside the scope counts against this stage unless a stage that changed the same directory at the same
        // time may have written it (no scope, or one that allows it): then it is attributed to that stage instead.
        var scope = watch.Scope;
        var outside = scope is null ? [] : changes.Changes.Where(c => !scope.Allows(c.Path)).Select(c => c.Path).ToList();
        var attributed = outside.Where(path => concurrent.Any(o => o.Scope is null || o.Scope.Allows(path))).ToHashSet(StringComparer.Ordinal);
        var violations = outside.Where(path => !attributed.Contains(path)).ToList();
        var enforce = AttributeValues.ParseBool(node[WorkspaceAttributes.WriteScopeEnforce]) == true;
        Outcome? failed = null;
        if (violations.Count > 0)
        {
            var listed = string.Join(", ", violations.Take(MaxReportedViolations)) + (violations.Count > MaxReportedViolations ? $", … ({violations.Count} in all)" : "");
            if (enforce && outcome.IsSuccessful)
            {
                failed = ScopeFailure(outcome, $"Changed {violations.Count} file{(violations.Count == 1 ? "" : "s")} outside write_scope \"{string.Join(", ", scope!.Patterns)}\": {listed}");
            }
            else
            {
                Log(node.Id, $"changed {violations.Count} file{(violations.Count == 1 ? "" : "s")} outside write_scope \"{string.Join(", ", scope!.Patterns)}\": {listed}", "warn");
            }
        }

        WriteChanges(watch, workspace, after, changes, visit, concurrent, violations, attributed, enforce, error);
        if (failed is not null)
        {
            WriteStatus(watch, failed);
        }

        ReleaseHold(watch, failed);
        if (changes.Changes.Count > 0 || violations.Count > 0)
        {
            _options.Emit(new WorkspaceChanged(node.Id, watch.Index, changes.Added, changes.Modified, changes.Deleted, changes.Changes.Take(SampleSize).Select(c => c.Path).ToList())
            {
                OutOfScope = violations.Take(MaxReportedViolations).ToList(),
                Concurrent = concurrent.Count > 0,
                Snapshot = after?.Id,
            });
        }

        return failed ?? outcome;
    }

    /// <summary>
    /// Called by the engine for every event: holds the <see cref="StageCompleted"/> of a stage with
    /// <c>write_scope_enforce</c> until <see cref="CompleteAsync"/> has checked its scope. True when the event was held.
    /// </summary>
    public bool Hold(PipelineEvent pipelineEvent)
    {
        if (pipelineEvent is not StageCompleted completed || _holds.IsEmpty || !_holds.TryGetValue((completed.NodeId, completed.Index), out var watch))
        {
            return false;
        }

        lock (watch)
        {
            if (watch.Held is not null || watch.Completed)
            {
                return false;
            }

            watch.Held = completed;
            return true;
        }
    }

    /// <summary>Releases a held <see cref="StageCompleted"/> (as a failure when <paramref name="failed"/> is set).</summary>
    private void ReleaseHold(StageWatch watch, Outcome? failed)
    {
        if (!_holds.TryRemove(new KeyValuePair<(string, int), StageWatch>((watch.Node.Id, watch.Index), watch)))
        {
            return;
        }

        StageCompleted? held;
        lock (watch)
        {
            held = watch.Held;
            watch.Held = null;
        }

        if (failed is not null)
        {
            _options.Emit(new StageFailed(watch.Node.Id, watch.Index, failed.FailureReason, WillRetry: false));
        }

        if (held is not null)
        {
            _options.Emit(failed is null ? held : held with { Status = failed.Status.ToWire(), Notes = failed.Notes, Timestamp = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>A write-scope failure: routing hints and artifact records of the reported outcome are dropped.</summary>
    private static Outcome ScopeFailure(Outcome outcome, string reason)
    {
        var updates = new Dictionary<string, object?>(outcome.ContextUpdates, StringComparer.Ordinal);
        foreach (var artifact in outcome.Artifacts)
        {
            updates.Remove(ArtifactPins.ContextKey(artifact.Name));
        }

        return outcome with
        {
            Status = StageStatus.Fail,
            FailureReason = reason,
            ContextUpdates = updates,
            PreferredLabel = "",
            SuggestedNextIds = [],
            JumpTo = null,
            Artifacts = [],
        };
    }

    private void WriteStatus(StageWatch watch, Outcome outcome)
    {
        if (watch.Context.Run?.StageDirectory(watch.Node.Id) is not { } stageDir)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(stageDir);
            File.WriteAllText(Path.Combine(stageDir, "status.json"), outcome.ToStatusJson().ToJsonString(Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(watch.Node.Id, "could not rewrite status.json: " + ex.Message, "warn");
        }
    }

    private void WriteChanges(
        StageWatch watch,
        Workspace workspace,
        WorkspaceSnapshot? after,
        ChangeSet changes,
        int visit,
        IReadOnlyList<StageWatch> concurrent,
        List<string> violations,
        IReadOnlySet<string> attributed,
        bool enforce,
        string? error)
    {
        if (watch.Context.Run?.StageDirectory(watch.Node.Id) is not { } stageDir)
        {
            return;
        }

        var scope = watch.Scope;
        var violating = violations.ToHashSet(StringComparer.Ordinal);
        var git = workspace.Snapshotter as GitSnapshotter;
        var idKey = git is null ? "sha256" : "blob";
        var listed = new JsonArray();
        foreach (var change in changes.Changes.Take(MaxListedChanges))
        {
            var entry = new JsonObject { ["path"] = change.Path, ["change"] = FileChange.Wire(change.Kind) };
            if (change.Size is { } size)
            {
                entry["size"] = size;
            }

            if (change.Id is { } id)
            {
                entry[idKey] = id;
            }

            if (change.OldSize is { } oldSize)
            {
                entry["old_size"] = oldSize;
            }

            if (change.OldId is { } oldId)
            {
                entry["old_" + idKey] = oldId;
            }

            if (change.Type is { } type)
            {
                entry["type"] = type;
            }

            if (violating.Contains(change.Path))
            {
                entry["out_of_scope"] = true;
            }

            listed.Add(entry);
        }

        var record = new JsonObject
        {
            ["node"] = watch.Node.Id,
            ["visit"] = visit,
            ["index"] = watch.Index,
            ["mode"] = workspace.Snapshotter?.Mode,
            ["workspace"] = workspace.Directory,
            ["workspace_label"] = workspace.Label,
        };
        if (git is not null)
        {
            record["ref"] = git.RefName;
        }

        record["before"] = watch.Before?.ToJson();
        record["after"] = after?.ToJson();
        record["summary"] = new JsonObject
        {
            ["added"] = changes.Added,
            ["modified"] = changes.Modified,
            ["deleted"] = changes.Deleted,
            ["total"] = changes.Changes.Count,
        };
        record["changes"] = listed;
        record["truncated"] = changes.Truncated || changes.Changes.Count > MaxListedChanges || (after?.Truncated ?? false);
        record["concurrent"] = concurrent.Count > 0;
        if (concurrent.Count > 0)
        {
            record["concurrent_with"] = new JsonArray(concurrent.Select(w => w.Node.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id => (JsonNode?)id).ToArray());
        }

        var skipped = (watch.Before?.Skipped ?? []).Concat(after?.Skipped ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (skipped.Count > 0)
        {
            record["skipped"] = new JsonArray(skipped.Take(100).Select(p => (JsonNode?)p).ToArray());
        }

        if (scope is not null)
        {
            var scopeRecord = new JsonObject
            {
                ["patterns"] = new JsonArray(scope.Patterns.Select(p => (JsonNode?)p).ToArray()),
                ["enforced"] = enforce,
                ["violations"] = new JsonArray(violations.Take(MaxListedChanges).Select(p => (JsonNode?)p).ToArray()),
            };
            if (attributed.Count > 0)
            {
                // Outside this stage's scope, but a concurrent stage may have written them.
                scopeRecord["attributed_to_concurrent"] = new JsonArray(attributed.Order(StringComparer.Ordinal).Take(MaxListedChanges).Select(p => (JsonNode?)p).ToArray());
            }

            record["write_scope"] = scopeRecord;
        }

        if (watch.Promotion is not null)
        {
            record["promotion"] = watch.Promotion;
        }

        if (watch.Note is not null)
        {
            record["note"] = watch.Note;
        }

        if (error is not null)
        {
            record["error"] = error;
        }

        try
        {
            Directory.CreateDirectory(stageDir);
            File.WriteAllText(Path.Combine(stageDir, ChangesFileName), record.ToJsonString(Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(watch.Node.Id, "could not write changes.json: " + ex.Message, "warn");
        }
    }

    /// <summary>The workspace for a directory, opening its snapshotter on first use; null when it cannot be tracked.</summary>
    private async Task<Workspace?> GetWorkspaceAsync(string directory, string nodeId, CancellationToken cancellationToken)
    {
        var workspace = _workspaces.GetOrAdd(Normalize(directory), d => new Workspace(d, PathComparer.Equals(d, MainDirectory) ? "main" : LabelFor(d)));
        var opening = workspace.Opening;
        if (opening is null)
        {
            lock (workspace)
            {
                opening = workspace.Opening ??= OpenAsync(workspace, nodeId, cancellationToken);
            }
        }

        try
        {
            return await opening.ConfigureAwait(false) ? workspace : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (workspace)
            {
                // Let a later stage try again.
                if (ReferenceEquals(workspace.Opening, opening))
                {
                    workspace.Opening = null;
                }
            }

            throw;
        }
    }

    private async Task<bool> OpenAsync(Workspace workspace, string nodeId, CancellationToken cancellationToken)
    {
        if (workspace.Snapshotter is not null)
        {
            return true;
        }

        var directory = workspace.Directory;
        if (!Directory.Exists(directory))
        {
            Log(nodeId, $"workspace tracking is off: the working directory '{directory}' does not exist", "warn");
            return false;
        }

        if (workspace.Branch is null && IsUnder(directory, RunDirectory))
        {
            Log(nodeId, $"workspace tracking is off: the working directory '{directory}' is inside the run directory", "warn");
            return false;
        }

        try
        {
            var location = await GitLocation.ProbeAsync(directory, cancellationToken).ConfigureAwait(false);
            if (location is not null)
            {
                StartPruning(location, nodeId);
                workspace.Snapshotter = await GitSnapshotter.OpenAsync(
                    location,
                    Path.Combine(StateDirectory, IndexName(workspace.Label)),
                    RefFor(workspace.Label),
                    RunExcludes(directory),
                    seedIndex: null,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                workspace.Snapshotter = new WalkSnapshotter(directory, IsRunDirectory, _options.WalkFileLimit);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(nodeId, $"workspace tracking is off for '{directory}': {ex.Message}", "warn");
            return false;
        }

        if (workspace.Label == "main")
        {
            var git = workspace.Snapshotter as GitSnapshotter;
            lock (_gate)
            {
                _runRecord["run_id"] = _options.RunId;
                _runRecord["mode"] = workspace.Snapshotter.Mode;
                _runRecord["directory"] = directory;
                if (git is not null)
                {
                    _runRecord["repository"] = git.Location.TopLevel;
                    _runRecord["prefix"] = git.Location.Prefix;
                    _runRecord["git_dir"] = git.Location.CommonDir;
                    _runRecord["ref"] = git.RefName;
                    _runRecord["refs"] = $"refs/reactor/runs/{SafeRefPart(_options.RunId)}/";
                }
            }

            SaveRunRecord();
            Log(nodeId, git is not null
                ? $"workspace snapshots use git (a private index; snapshot commits on {git.RefName})"
                : $"workspace snapshots use a file walk ('{directory}' is not in a git work tree{(await GitCli.IsAvailableAsync().ConfigureAwait(false) ? "" : ", or git is not installed")})");
        }

        return true;
    }

    /// <summary>
    /// Takes a snapshot of <paramref name="workspace"/> (one at a time per workspace); null when it failed (logged).
    /// </summary>
    private async Task<WorkspaceSnapshot?> TakeSnapshotAsync(Workspace workspace, string message, string nodeId, CancellationToken cancellationToken) =>
        (await TakeSnapshotAsync(workspace, null, message, nodeId, cancellationToken).ConfigureAwait(false)).Snapshot;

    /// <summary>
    /// Takes a snapshot of <paramref name="workspace"/> (one at a time per workspace) and, given <paramref name="before"/>,
    /// the changes since then. Nulls when it failed (logged).
    /// </summary>
    private async Task<(WorkspaceSnapshot? Snapshot, ChangeSet? Changes)> TakeSnapshotAsync(
        Workspace workspace,
        WorkspaceSnapshot? before,
        string message,
        string nodeId,
        CancellationToken cancellationToken)
    {
        if (workspace.Snapshotter is not { } snapshotter || workspace.Disabled)
        {
            return (null, null);
        }

        await workspace.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (snapshotter is GitSnapshotter git)
            {
                // Snapshots of a workspace never overlap, so a lock file left behind is from a killed git.
                DeleteQuietly(git.IndexPath + ".lock");
            }

            var started = Stopwatch.StartNew();
            var label = $"reactor {_options.RunId}: {message}";
            WorkspaceSnapshot snapshot;
            ChangeSet? changes = null;
            if (before is null)
            {
                snapshot = await snapshotter.SnapshotAsync(label, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (snapshot, changes) = await snapshotter.SnapshotAndDiffAsync(before, label, MaxListedChanges, cancellationToken).ConfigureAwait(false);
            }

            lock (workspace)
            {
                workspace.Last = snapshot;
                workspace.Stale = false;
                workspace.Snapshots++;
                if (workspace.Snapshots == 1 && started.Elapsed > TimeSpan.FromSeconds(10))
                {
                    Log(nodeId, $"the first workspace snapshot of '{workspace.Directory}' took {AttributeValues.FormatDuration(started.Elapsed)}; later ones only look at changed files (track_changes=false turns snapshots off)", "warn");
                }
            }

            return (snapshot, changes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            lock (workspace)
            {
                workspace.Stale = true;
                if (ex is TimeoutException)
                {
                    // Every later snapshot would wait as long: stop recording this workspace for the rest of the run.
                    workspace.Disabled = true;
                }
            }

            Log(nodeId, ex is TimeoutException
                ? $"workspace snapshot of '{workspace.Directory}' timed out ({ex.Message}); no more changes are recorded for it in this run (track_changes=false turns snapshots off)"
                : $"workspace snapshot of '{workspace.Directory}' failed: {ex.Message}", "warn");
            return (null, null);
        }
        finally
        {
            workspace.Gate.Release();
        }
    }

    /// <summary>The last snapshot when nothing ran since it was taken, else a new one.</summary>
    private async Task<WorkspaceSnapshot?> FreshSnapshotAsync(Workspace workspace, string message, string nodeId, CancellationToken cancellationToken)
    {
        lock (workspace)
        {
            if (workspace.Last is { } last && !workspace.Stale && workspace.Active.Count == 0)
            {
                return last;
            }
        }

        return await TakeSnapshotAsync(workspace, message, nodeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The main workspace's state for the checkpoint snapshot <paramref name="snapshotName"/> of top-level stage
    /// <paramref name="node"/>: the last snapshot when nothing ran since, else a new one (always a new one after a parallel
    /// stage, whose branches may have included untracked stages). Null unless this real run tracks changes in a git work
    /// tree (file walks keep no contents, so they cannot be restored) or when the snapshot failed.
    /// </summary>
    public async Task<CheckpointWorkspace?> CheckpointSnapshotAsync(Node node, string handlerType, string snapshotName, int index, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!RealRun || !_options.TrackChanges || AttributeValues.ParseBool(_options.Graph[WorkspaceAttributes.TrackChanges]) == false)
        {
            return null;
        }

        var workspace = await GetWorkspaceAsync(MainDirectory, node.Id, cancellationToken).ConfigureAwait(false);
        if (workspace?.Snapshotter is not GitSnapshotter git)
        {
            return null;
        }

        var message = $"checkpoint {snapshotName}";
        var snapshot = handlerType == HandlerTypes.Parallel
            ? await TakeSnapshotAsync(workspace, message, node.Id, cancellationToken).ConfigureAwait(false)
            : await FreshSnapshotAsync(workspace, message, node.Id, cancellationToken).ConfigureAwait(false);
        if (snapshot?.Tree is not { } tree)
        {
            return null;
        }

        return new CheckpointWorkspace
        {
            Checkpoint = snapshotName,
            Index = index,
            Node = node.Id,
            Mode = git.Mode,
            Commit = snapshot.Commit,
            Tree = tree,
            Ref = git.RefName,
            Repository = git.Location.TopLevel,
            GitDir = git.Location.CommonDir,
            Prefix = git.Location.Prefix,
            Directory = workspace.Directory,
            TakenAt = snapshot.TakenAt,
        };
    }

    /// <summary>
    /// Pathspecs that leave the run directory (and its loop-restart siblings) out of a git snapshot of
    /// <paramref name="directory"/>. They start with <c>**/</c>: git rejects an exclude naming an ignored directory.
    /// </summary>
    private List<string> RunExcludes(string directory) => ExcludesFor(directory, RunDirectory);

    /// <summary>
    /// Pathspecs that leave <paramref name="excluded"/> (and its <c>-restart-N</c> siblings) out of a git snapshot of
    /// <paramref name="directory"/>; none when it is outside the directory or already left out (under <c>.reactor</c> or
    /// <c>node_modules</c>).
    /// </summary>
    internal static List<string> ExcludesFor(string directory, string excluded)
    {
        var excludes = new List<string>();
        excluded = Normalize(excluded);
        if (!IsUnder(excluded, directory) || PathComparer.Equals(excluded, Normalize(directory)))
        {
            return excludes;
        }

        var relative = Path.GetRelativePath(directory, excluded).Replace('\\', '/');
        if (relative.Split('/').Any(WalkSnapshotter.IsSkippedName))
        {
            // Under .reactor (the default) or node_modules: left out already.
            return excludes;
        }

        var escaped = string.Concat(relative.Select(c => c is '*' or '?' or '[' or ']' or '\\' ? "\\" + c : c.ToString()));
        excludes.Add(":(exclude,glob)**/" + escaped + "/**");
        excludes.Add(":(exclude,glob)**/" + escaped + "-restart-*/**");
        return excludes;
    }

    /// <summary>True for the run directory and its loop-restart siblings (left out of file walks).</summary>
    private bool IsRunDirectory(string fullPath)
    {
        var path = Path.TrimEndingDirectorySeparator(fullPath);
        return PathComparer.Equals(path, RunDirectory) ||
               (path.StartsWith(RunDirectory + "-restart-", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                PathComparer.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(RunDirectory)));
    }

    private void SaveRunRecord()
    {
        try
        {
            string text;
            lock (_gate)
            {
                text = _runRecord.ToJsonString(Indented);
            }

            Directory.CreateDirectory(RunDirectory);
            File.WriteAllText(Path.Combine(RunDirectory, RunFileName), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The record is informational.
        }
    }

    private void Log(string nodeId, string message, string level = "info") => _options.Emit(new StageLog(nodeId, message, level));

    private string RefFor(string label) => $"refs/reactor/runs/{SafeRefPart(_options.RunId)}/{label}";

    private static string IndexName(string label) => label.Replace('/', '.') + ".index";

    private static string LabelFor(string directory) =>
        "dir-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..10];

    /// <summary>A string safe as one component of a git ref name.</summary>
    internal static string SafeRefPart(string value)
    {
        var chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var safe = new string(chars).Trim('-');
        return safe.Length == 0 ? "run" : safe;
    }

    internal static string Normalize(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or inside it.</summary>
    internal static bool IsUnder(string path, string directory)
    {
        var relative = Path.GetRelativePath(Normalize(directory), Normalize(path));
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>One working directory the run snapshots.</summary>
    internal sealed class Workspace(string directory, string label)
    {
        public string Directory { get; } = directory;

        /// <summary><c>main</c>, or <c>branches/&lt;parallel&gt;/&lt;branch&gt;</c> for a branch workspace.</summary>
        public string Label { get; } = label;

        public IWorkspaceSnapshotter? Snapshotter { get; set; }

        public Task<bool>? Opening { get; set; }

        /// <summary>Serializes snapshots of this workspace.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public WorkspaceSnapshot? Last { get; set; }

        /// <summary>The workspace may have changed since <see cref="Last"/> without a tracked stage seeing it.</summary>
        public bool Stale { get; set; } = true;

        /// <summary>Snapshots stopped for the rest of the run (one timed out).</summary>
        public bool Disabled { get; set; }

        public int Snapshots { get; set; }

        /// <summary>Tracked stage visits running here now.</summary>
        public List<StageWatch> Active { get; } = [];

        /// <summary>Set for branch workspaces.</summary>
        public BranchWorkspace? Branch { get; set; }
    }
}

/// <summary>One stage visit as seen by the <see cref="WorkspaceTracker"/>. Dispose it when the visit ends.</summary>
internal sealed class StageWatch(WorkspaceTracker owner, Node node, string handlerType, PipelineContext context, int index) : IDisposable
{
    private int _disposed;

    public Node Node { get; } = node;

    public string HandlerType { get; } = handlerType;

    public PipelineContext Context { get; } = context;

    public int Index { get; } = index;

    internal WorkspaceTracker.Workspace? Workspace { get; set; }

    internal SemaphoreSlim? Group { get; set; }

    internal WorkspaceSnapshot? Before { get; set; }

    /// <summary>The node's <c>write_scope</c> (null = may change anything).</summary>
    internal WriteScope? Scope { get; } = WriteScope.Parse(node[WorkspaceAttributes.WriteScope]);

    /// <summary>Tracked stages that ran in the same workspace at the same time (guarded by the workspace's lock).</summary>
    internal HashSet<StageWatch> Concurrent { get; } = [];

    /// <summary>What a fan-in promoted (written to its <c>changes.json</c>).</summary>
    internal JsonObject? Promotion { get; set; }

    /// <summary>An explanation written to <c>changes.json</c> as <c>note</c>.</summary>
    internal string? Note { get; init; }

    internal StageCompleted? Held { get; set; }

    internal bool Completed { get; set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        owner.Release(this);
        Group?.Release();
    }
}
