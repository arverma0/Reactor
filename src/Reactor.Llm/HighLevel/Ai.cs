using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;

namespace Reactor.Llm;

/// <summary>
/// High-level API (Layer 4): <see cref="GenerateAsync(GenerateOptions, CancellationToken)"/>, <see cref="Stream"/>,
/// <see cref="GenerateObjectAsync(GenerateObjectOptions, CancellationToken)"/>, and <see cref="StreamObject"/>.
/// These add prompt standardization, automatic tool loops, per-step retries, timeouts, and cancellation on top of
/// <see cref="Client"/>.
/// </summary>
public static class Ai
{
    private static readonly object DefaultGate = new();
    private static Client? _defaultClient;

    /// <summary>The module-level default client, lazily created from environment variables on first use.</summary>
    public static Client DefaultClient
    {
        get
        {
            lock (DefaultGate)
            {
                return _defaultClient ??= Client.FromEnv();
            }
        }
    }

    /// <summary>Overrides the module-level default client.</summary>
    public static void SetDefaultClient(Client? client)
    {
        lock (DefaultGate)
        {
            _defaultClient = client;
        }
    }

    /// <summary>Generates text for a simple prompt.</summary>
    public static Task<GenerateResult> GenerateAsync(string model, string prompt, CancellationToken cancellationToken = default) =>
        GenerateAsync(new GenerateOptions { Model = model, Prompt = prompt }, cancellationToken);

    /// <summary>
    /// Non-streaming generation with an automatic tool loop: when the model calls active tools, they are executed
    /// concurrently and their results fed back until the model stops calling tools, <see cref="GenerateOptions.MaxToolRounds"/>
    /// is reached, or <see cref="GenerateOptions.StopWhen"/> returns true. Each LLM call is retried independently.
    /// </summary>
    public static async Task<GenerateResult> GenerateAsync(GenerateOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        var client = options.Client ?? DefaultClient;
        var conversation = BuildInitialMessages(options);
        var toolDefinitions = options.Tools?.Select(t => t.Definition).ToList();
        var steps = new List<StepResult>();

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Timeout?.Total is { } total)
        {
            totalCts.CancelAfter(total);
        }

        try
        {
            for (var round = 0; ; round++)
            {
                var request = BuildRequest(options, conversation, toolDefinitions);
                var response = await CompleteStepAsync(client, request, options, totalCts.Token).ConfigureAwait(false);

                var calls = response.ToolCalls;
                var execute = ShouldExecuteTools(options, response, calls, round);
                IReadOnlyList<ToolResult> results = execute
                    ? await ToolExecution.ExecuteAllAsync(options.Tools, calls, conversation.ToList(), options, totalCts.Token).ConfigureAwait(false)
                    : [];

                steps.Add(StepResult.From(response, results));
                conversation.Add(response.Message);
                if (!execute)
                {
                    break;
                }

                conversation.AddRange(results.Select(r => r.ToMessage()));
                if (options.StopWhen?.Invoke(steps) == true)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            var translated = TranslateFailure(ex, cancellationToken, totalCts, options.Timeout?.Total);
            if (ReferenceEquals(translated, ex))
            {
                throw;
            }

            throw translated;
        }

        return GenerateResult.FromSteps(steps, conversation);
    }

    /// <summary>
    /// Streaming generation. Returns immediately; enumerate the result for events. Only the initial connection of each
    /// step is retried; once data has been delivered, failures surface as an ERROR event.
    /// </summary>
    public static StreamResult Stream(GenerateOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        var client = options.Client ?? DefaultClient;
        return new StreamResult((result, ct) => StreamLoop(result, options, client, ct), cancellationToken);
    }

    /// <summary>Generates structured output conforming to <see cref="GenerateObjectOptions.Schema"/>.</summary>
    /// <exception cref="NoObjectGeneratedError">The output could not be parsed or failed schema validation.</exception>
    public static async Task<GenerateResult> GenerateObjectAsync(GenerateObjectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        var client = options.Client ?? DefaultClient;
        var strategy = ObjectGeneration.ResolveStrategy(options, client);
        var prepared = ObjectGeneration.Prepare(options, strategy, client);
        var result = await GenerateAsync(prepared, cancellationToken).ConfigureAwait(false);
        var output = ObjectGeneration.Extract(result.Response, strategy, options);
        return result with { Output = output };
    }

    /// <summary>Streams structured output, yielding progressively more complete partial objects.</summary>
    public static ObjectStreamResult StreamObject(GenerateObjectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        var client = options.Client ?? DefaultClient;
        var strategy = ObjectGeneration.ResolveStrategy(options, client);
        var prepared = ObjectGeneration.Prepare(options, strategy, client);
        return new ObjectStreamResult(Stream(prepared, cancellationToken), strategy, options);
    }

    private static async IAsyncEnumerable<StreamEvent> StreamLoop(
        StreamResult result,
        GenerateOptions options,
        Client client,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Timeout?.Total is { } total)
        {
            totalCts.CancelAfter(total);
        }

        var conversation = BuildInitialMessages(options);
        var toolDefinitions = options.Tools?.Select(t => t.Definition).ToList();

        for (var round = 0; ; round++)
        {
            var request = BuildRequest(options, conversation, toolDefinitions);
            var accumulator = new StreamAccumulator();
            result.Accumulator = accumulator;
            StreamEvent? finishEvent = null;
            Exception? failure = null;

            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
            if (options.Timeout?.PerStep is { } perStep)
            {
                stepCts.CancelAfter(perStep);
            }

            IAsyncEnumerator<StreamEvent>? enumerator = null;
            var hasCurrent = false;
            try
            {
                (enumerator, hasCurrent) = await OpenStreamAsync(client, request, options.EffectiveRetryPolicy, stepCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = TranslateFailure(ex, cancellationToken, totalCts, options.Timeout?.Total, stepCts, options.Timeout?.PerStep);
            }

            if (failure is not null)
            {
                result.Fail(failure);
                Rethrow(failure);
            }

            try
            {
                while (hasCurrent)
                {
                    var evt = enumerator!.Current;
                    accumulator.Process(evt);
                    if (evt.Type == StreamEventType.Finish)
                    {
                        finishEvent = evt;
                    }
                    else
                    {
                        yield return evt;
                    }

                    if (evt.Type == StreamEventType.Error)
                    {
                        break;
                    }

                    try
                    {
                        hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failure = TranslateFailure(ex, cancellationToken, totalCts, options.Timeout?.Total, stepCts, options.Timeout?.PerStep);
                        hasCurrent = false;
                    }
                }
            }
            finally
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (failure is not null)
            {
                result.Fail(failure);
                if (failure is AbortError)
                {
                    Rethrow(failure);
                }

                // Data has already been delivered for this step, so the failure is reported in-band rather than retried.
                yield return new StreamEvent
                {
                    Type = StreamEventType.Error,
                    Error = failure as SdkError ?? new StreamError(failure.Message, failure),
                };
                yield break;
            }

            if (accumulator.Error is { } streamError)
            {
                result.Fail(streamError);
                yield break;
            }

            if (finishEvent is null)
            {
                var incomplete = new StreamError("The stream ended without a finish event.");
                result.Fail(incomplete);
                yield return new StreamEvent { Type = StreamEventType.Error, Error = incomplete };
                yield break;
            }

            var response = accumulator.Response;
            var calls = response.ToolCalls;
            var execute = ShouldExecuteTools(options, response, calls, round);
            IReadOnlyList<ToolResult> toolResults = [];
            if (execute)
            {
                try
                {
                    toolResults = await ToolExecution.ExecuteAllAsync(options.Tools, calls, conversation.ToList(), options, totalCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = TranslateFailure(ex, cancellationToken, totalCts, options.Timeout?.Total);
                }

                if (failure is not null)
                {
                    result.Fail(failure);
                    Rethrow(failure);
                }
            }

            var step = StepResult.From(response, toolResults);
            result.StepsList.Add(step);
            result.TotalUsage += step.Usage;
            conversation.Add(response.Message);

            if (!execute)
            {
                result.Complete(response);
                yield return finishEvent;
                yield break;
            }

            conversation.AddRange(toolResults.Select(r => r.ToMessage()));
            yield return new StreamEvent
            {
                Type = StreamEventType.StepFinish,
                Response = response,
                FinishReason = response.FinishReason,
                Usage = response.Usage,
            };

            if (options.StopWhen?.Invoke(result.StepsList) == true)
            {
                result.Complete(response);
                yield return finishEvent;
                yield break;
            }
        }
    }

    private static async Task<(IAsyncEnumerator<StreamEvent> Enumerator, bool HasCurrent)> OpenStreamAsync(
        Client client,
        Request request,
        RetryPolicy policy,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var enumerator = client.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                var hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
                return (enumerator, hasCurrent);
            }
            catch (Exception error) when (error is not AbortError && !cancellationToken.IsCancellationRequested)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
                if (attempt >= policy.MaxRetries || !policy.IsRetryable(error) || !Retry.TryGetRetryDelay(policy, error, attempt, out var delay))
                {
                    throw;
                }

                policy.OnRetry?.Invoke(error, attempt + 1, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task<Response> CompleteStepAsync(Client client, Request request, GenerateOptions options, CancellationToken cancellationToken)
    {
        var perStep = options.Timeout?.PerStep;
        return await Retry.ExecuteAsync(
            async token =>
            {
                if (perStep is null)
                {
                    return await client.CompleteAsync(request, token).ConfigureAwait(false);
                }

                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                stepCts.CancelAfter(perStep.Value);
                try
                {
                    return await client.CompleteAsync(request, stepCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AbortError or OperationCanceledException && stepCts.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    throw new RequestTimeoutError($"LLM call exceeded the per-step timeout of {perStep.Value.TotalSeconds:0.###}s.", ex);
                }
            },
            options.EffectiveRetryPolicy,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool ShouldExecuteTools(GenerateOptions options, Response response, IReadOnlyList<ToolCall> calls, int round) =>
        calls.Count > 0 &&
        response.FinishReason.Reason == FinishReason.ToolCallsReason &&
        options.MaxToolRounds > 0 &&
        round < options.MaxToolRounds &&
        ToolExecution.CanExecuteAll(options.Tools, calls);

    private static void Validate(GenerateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ConfigurationError("A model is required.");
        }

        if (options.Prompt is not null && options.Messages is not null)
        {
            throw new ConfigurationError("Provide either 'prompt' or 'messages', not both.");
        }

        if (options.Prompt is null && (options.Messages is null || options.Messages.Count == 0))
        {
            throw new ConfigurationError("Either 'prompt' or a non-empty 'messages' list is required.");
        }

        if (options.MaxToolRounds < 0)
        {
            throw new ConfigurationError("MaxToolRounds must be >= 0.");
        }

        if (options.Tools is not null)
        {
            var duplicate = options.Tools.GroupBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw new ConfigurationError($"Duplicate tool name '{duplicate.Key}'.");
            }
        }
    }

    private static List<Message> BuildInitialMessages(GenerateOptions options)
    {
        var messages = new List<Message>();
        if (!string.IsNullOrEmpty(options.System))
        {
            messages.Add(Message.System(options.System));
        }

        if (options.Prompt is not null)
        {
            messages.Add(Message.User(options.Prompt));
        }
        else if (options.Messages is not null)
        {
            messages.AddRange(options.Messages);
        }

        return messages;
    }

    private static Request BuildRequest(GenerateOptions options, List<Message> conversation, IReadOnlyList<ToolDefinition>? tools) => new()
    {
        Model = options.Model,
        Messages = conversation.ToList(),
        Provider = options.Provider,
        Tools = tools is { Count: > 0 } ? tools : null,
        ToolChoice = options.ToolChoice,
        ResponseFormat = options.ResponseFormat,
        Temperature = options.Temperature,
        TopP = options.TopP,
        MaxTokens = options.MaxTokens,
        StopSequences = options.StopSequences,
        ReasoningEffort = options.ReasoningEffort,
        Metadata = options.Metadata,
        ProviderOptions = options.ProviderOptions is null ? null : (JsonObject)options.ProviderOptions.DeepClone(),
    };

    private static Exception TranslateFailure(
        Exception error,
        CancellationToken userToken,
        CancellationTokenSource totalCts,
        TimeSpan? total,
        CancellationTokenSource? stepCts = null,
        TimeSpan? perStep = null)
    {
        var isCancellation = error is AbortError or OperationCanceledException;
        if (userToken.IsCancellationRequested)
        {
            return error as AbortError ?? new AbortError(cause: error);
        }

        if (isCancellation && stepCts is not null && perStep is not null && stepCts.IsCancellationRequested && !totalCts.IsCancellationRequested)
        {
            return new RequestTimeoutError($"LLM call exceeded the per-step timeout of {perStep.Value.TotalSeconds:0.###}s.", error);
        }

        if (isCancellation && total is not null && totalCts.IsCancellationRequested)
        {
            return new RequestTimeoutError($"Operation exceeded the total timeout of {total.Value.TotalSeconds:0.###}s.", error);
        }

        return error is OperationCanceledException ? new AbortError(cause: error) : error;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Rethrow(Exception error) => ExceptionDispatchInfo.Capture(error).Throw();
}
