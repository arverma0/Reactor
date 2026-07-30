using Reactor.Events;
using Reactor.Graphs;
using Reactor.Workspaces;

namespace Reactor.Execution;

/// <summary>Workspace hooks of the engine (Reactor extension; see <see cref="WorkspaceTracker"/>).</summary>
public sealed partial class PipelineRunner
{
    private sealed partial class PipelineExecution
    {
        private WorkspaceTracker? _workspace;

        /// <summary>The run's workspace service, created on first use (once the run directory is known).</summary>
        private WorkspaceTracker WorkspaceService => _workspace ??= new WorkspaceTracker(new WorkspaceTrackerOptions
        {
            RunId = _runId,
            RunDirectory = _baseLogsRoot,
            WorkingDirectory = WorkingDirectory,
            Graph = _graph,
            Emit = Emit,
            RealRun = Persist && (_runner.Options.RealWorkspace ?? Backend is not null),
            TrackChanges = _options.TrackWorkspaceChanges ?? _runner.Options.TrackWorkspaceChanges,
        });

        /// <summary>
        /// <see cref="RunNodeAsync"/> inside the workspace service: waits for the node's <c>concurrency_group</c>, snapshots
        /// its working directory before and after the visit (all attempts), writes <c>changes.json</c>, and applies
        /// <c>write_scope_enforce</c> (which can turn a successful visit into a failure, without a retry).
        /// </summary>
        private async Task<(Outcome Outcome, int Attempts)> RunTrackedNodeAsync(
            Node node,
            PipelineContext context,
            string fidelity,
            string? threadId,
            int index,
            CancellationToken cancellationToken)
        {
            var type = _runner.Handlers.TryResolve(node, out var resolved) is null ? HandlerTypes.Codergen : resolved;
            StageWatch? watch;
            try
            {
                watch = await WorkspaceService.BeginAsync(node, type, context, index, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Workspace tracking never stops a run.
                Emit(new StageLog(node.Id, "workspace tracking failed: " + ex.Message, "warn"));
                watch = null;
            }

            if (watch is null)
            {
                return await RunNodeAsync(node, context, fidelity, threadId, index, cancellationToken).ConfigureAwait(false);
            }

            using (watch)
            {
                var (outcome, attempts) = await RunNodeAsync(node, context, fidelity, threadId, index, cancellationToken).ConfigureAwait(false);
                int visit;
                lock (_stateLock)
                {
                    visit = _visits.GetValueOrDefault(node.Id);
                }

                try
                {
                    outcome = await WorkspaceService.CompleteAsync(watch, outcome, visit, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Emit(new StageLog(node.Id, "workspace tracking failed: " + ex.Message, "warn"));
                }

                return (outcome, attempts);
            }
        }

        /// <summary>
        /// Records the main workspace's state with the checkpoint snapshot of a top-level stage
        /// (<c>checkpoints/&lt;NNNN-node&gt;.workspace.json</c>), so a fork from that snapshot can rewind the working
        /// directory (git work trees only). It reuses the stage's "after" snapshot when nothing ran since; stages that take
        /// no snapshot of their own (human gates, untracked stages) and parallel stages get a fresh one. Failures are
        /// logged, never fatal.
        /// </summary>
        private async Task RecordCheckpointWorkspaceAsync(Node node, int index, CancellationToken cancellationToken)
        {
            if (!Persist || !_options.KeepCheckpointHistory || _graph.ContainsNode(Checkpoint.SnapshotDirectoryName))
            {
                return;
            }

            var name = Checkpoint.SnapshotName(index, node.Id);
            var type = _runner.Handlers.TryResolve(node, out var resolved) is null ? HandlerTypes.Codergen : resolved;
            try
            {
                if (await WorkspaceService.CheckpointSnapshotAsync(node, type, name, index, cancellationToken).ConfigureAwait(false) is { } record)
                {
                    var path = CheckpointWorkspace.PathFor(Path.Combine(_logsRoot, Checkpoint.SnapshotDirectoryName), name);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, record.ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The run is stopping; the next step observes the cancellation.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Emit(new StageLog(node.Id, "could not record the workspace with the checkpoint snapshot: " + ex.Message, "warn"));
            }
        }

        /// <summary>
        /// After the run: removes branch worktrees (their snapshot refs stay) and marks the run finished for later runs'
        /// stale-worktree cleanup. Events it logs reopen the event log, which is closed again here.
        /// </summary>
        internal async Task ReleaseWorkspaceAsync()
        {
            if (_workspace is null)
            {
                return;
            }

            try
            {
                await _workspace.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Emit(new StageLog("workspace", "workspace cleanup failed: " + ex.Message, "warn"));
            }
            finally
            {
                _eventLog?.Dispose();
            }
        }
    }
}
