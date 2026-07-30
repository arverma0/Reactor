using System.Globalization;
using Reactor.Graphs;
using Reactor.Handlers;

namespace Reactor.Execution;

/// <summary>
/// Reactor extension: a stage's time limits — the hard <c>timeout</c>, the soft-deadline reminder (<c>soft_timeout</c>),
/// and what a timeout does (<c>on_timeout</c>, with <c>timeout_grace</c> for <c>wrap_up</c>). Reminders and the wrap-up
/// message are steering messages, so they apply only to codergen stages whose backend supports steering; the time limit
/// itself is mentioned in every codergen prompt.
/// </summary>
public sealed record StageTiming(TimeSpan? HardTimeout, TimeSpan? SoftDeadline, TimeSpan? WrapUpGrace, string OnTimeout)
{
    public const string OnTimeoutRetry = "retry";
    public const string OnTimeoutFail = "fail";
    public const string OnTimeoutWrapUp = "wrap_up";

    /// <summary>The <c>on_timeout</c> values.</summary>
    public static IReadOnlyList<string> OnTimeoutValues { get; } = [OnTimeoutRetry, OnTimeoutFail, OnTimeoutWrapUp];

    /// <summary>Stages with a shorter timeout get no default soft-deadline reminder.</summary>
    public static TimeSpan SoftDeadlineMinimumTimeout { get; } = TimeSpan.FromMinutes(5);

    /// <summary>How long after a tool stage's own time limit the engine stops it (the tool normally stops itself first).</summary>
    public static TimeSpan ToolBackstop { get; } = TimeSpan.FromSeconds(15);

    /// <summary>The default soft deadline as a fraction of the timeout.</summary>
    public const double SoftDeadlineFraction = 0.8;

    /// <summary>Values of <c>soft_timeout</c> that turn the reminder off.</summary>
    public static bool IsOff(string? value) =>
        value?.Trim().ToLowerInvariant() is "off" or "none" or "false" or "no" or "0" or "0s";

    /// <summary>Resolves a node's time limits for <paramref name="handler"/> running on <paramref name="backend"/>.</summary>
    public static StageTiming For(Node node, INodeHandler handler, string handlerType, ICodergenBackend? backend)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(handler);
        var onTimeout = (node[AttrKeys.OnTimeout] ?? "").Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            OnTimeoutFail => OnTimeoutFail,
            OnTimeoutWrapUp or "wrapup" => OnTimeoutWrapUp,
            _ => OnTimeoutRetry,
        };
        if (handler is ISelfTimedHandler || node.Timeout is not { } timeout || timeout <= TimeSpan.Zero)
        {
            return new StageTiming(null, null, null, onTimeout);
        }

        if (handlerType == HandlerTypes.Tool)
        {
            // A tool stage stops its own command at the limit and records what it printed; the engine's limit is a backstop.
            return new StageTiming(timeout + ToolBackstop, null, null, onTimeout);
        }

        var steerable = handlerType == HandlerTypes.Codergen && backend is not null && backend.SupportsSteeringFor(node);
        if (!steerable)
        {
            return new StageTiming(timeout, null, null, onTimeout);
        }

        TimeSpan? soft = null;
        var softText = node[AttrKeys.SoftTimeout];
        if (!IsOff(softText))
        {
            soft = AttributeValues.ParseDuration(softText) is { } explicitSoft && explicitSoft > TimeSpan.Zero
                ? explicitSoft
                : string.IsNullOrWhiteSpace(softText) && timeout >= SoftDeadlineMinimumTimeout ? timeout * SoftDeadlineFraction : null;
        }

        TimeSpan? grace = null;
        if (onTimeout == OnTimeoutWrapUp)
        {
            grace = AttributeValues.ParseDuration(node[AttrKeys.TimeoutGrace]) is { } explicitGrace && explicitGrace > TimeSpan.Zero
                ? explicitGrace
                : DefaultGrace(timeout);
        }

        return new StageTiming(timeout, soft is { } s && s < timeout ? s : null, grace, onTimeout);
    }

    /// <summary>10% of the timeout, between 1 and 15 minutes (never more than the timeout itself).</summary>
    public static TimeSpan DefaultGrace(TimeSpan timeout)
    {
        var grace = TimeSpan.FromTicks(Math.Clamp(timeout.Ticks / 10, TimeSpan.FromMinutes(1).Ticks, TimeSpan.FromMinutes(15).Ticks));
        return grace < timeout ? grace : timeout;
    }

    /// <summary>The soft-deadline reminder sent to the agent.</summary>
    public static string SoftDeadlineMessage(TimeSpan left, TimeSpan limit) =>
        $"⏰ Time check from the pipeline: about {Describe(left)} remain before this stage is stopped (its time limit is {AttributeValues.FormatDuration(limit)}). " +
        "Wrap up now: finish the change in progress, run the checks that matter most, and end your turn with a short summary of what is done " +
        "and what is left. Do not start new work.";

    /// <summary>The message sent when the time limit is reached under <c>on_timeout=wrap_up</c>.</summary>
    public static string TimeIsUpMessage(TimeSpan grace) =>
        $"⏰ Time is up for this stage. Stop now: do not start anything new, and end your turn within {Describe(grace)} with a summary of what is done and what is left.";

    /// <summary>The time-limit line added to codergen prompts.</summary>
    public static string PromptLine(TimeSpan limit) =>
        $"Time limit: this stage is stopped after {AttributeValues.FormatDuration(limit)}. Pace the work so you can finish, verify, and summarize well before then.";

    private static string Describe(TimeSpan span) => span >= TimeSpan.FromMinutes(2)
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(span.TotalMinutes):0} minutes")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, Math.Round(span.TotalSeconds)):0} seconds");
}
