using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Handlers;

/// <summary>No-op entry point (spec 4.3).</summary>
public sealed class StartHandler : ISingleExecutionHandler
{
    public Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken) =>
        Task.FromResult<Outcome?>(Outcome.Success("Pipeline started"));
}

/// <summary>No-op exit point (spec 4.4). Goal gates are enforced by the engine before this runs.</summary>
public sealed class ExitHandler : ISingleExecutionHandler
{
    public Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken) =>
        Task.FromResult<Outcome?>(Outcome.Success("Pipeline exit reached"));
}

/// <summary>
/// Pass-through for conditional routing points (spec 4.7); the engine evaluates the outgoing edge conditions.
/// The node re-emits the previous stage's outcome, preferred label, and failure reason so that conditions such as
/// <c>outcome!=success</c> on a diamond's edges refer to the work being evaluated rather than to the no-op node itself.
/// Executed once (never retried).
/// </summary>
public sealed class ConditionalHandler : ISingleExecutionHandler
{
    public Task<Outcome?> ExecuteAsync(Node node, PipelineContext context, Graph graph, string logsRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);

        var previous = context.GetString("outcome");
        if (previous.Length > 0 && StageStatusExtensions.TryParse(previous, out var status) && status != StageStatus.Skipped)
        {
            return Task.FromResult<Outcome?>(new Outcome
            {
                Status = status,
                PreferredLabel = context.GetString("preferred_label"),
                FailureReason = status is StageStatus.Fail or StageStatus.Retry ? context.GetString("failure_reason") : "",
                Notes = "Conditional node evaluated: " + node.Id,
            });
        }

        return Task.FromResult<Outcome?>(Outcome.Success("Conditional node evaluated: " + node.Id));
    }
}
