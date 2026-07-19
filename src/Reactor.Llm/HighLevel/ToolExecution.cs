using System.Text.Json.Nodes;
using Reactor.Llm.Utilities;

namespace Reactor.Llm;

/// <summary>Executes tool calls for the high-level tool loop: concurrently, order-preserving, errors as results.</summary>
internal static class ToolExecution
{
    /// <summary>
    /// True when every call can be handled automatically: active tools run, unknown tools produce error results.
    /// A call to a passive tool (no execute handler) means the caller must handle the batch.
    /// </summary>
    public static bool CanExecuteAll(IReadOnlyList<Tool>? tools, IReadOnlyList<ToolCall> calls)
    {
        if (calls.Count == 0)
        {
            return false;
        }

        foreach (var call in calls)
        {
            var tool = Find(tools, call.Name);
            if (tool is not null && !tool.IsActive)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Runs all calls concurrently and waits for all of them; results are in call order.</summary>
    public static async Task<IReadOnlyList<ToolResult>> ExecuteAllAsync(
        IReadOnlyList<Tool>? tools,
        IReadOnlyList<ToolCall> calls,
        IReadOnlyList<Message> conversation,
        GenerateOptions options,
        CancellationToken cancellationToken)
    {
        var tasks = new Task<ToolResult>[calls.Count];
        for (var i = 0; i < calls.Count; i++)
        {
            tasks[i] = ExecuteOneAsync(Find(tools, calls[i].Name), calls[i], conversation, options, cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<ToolResult> ExecuteOneAsync(
        Tool? tool,
        ToolCall call,
        IReadOnlyList<Message> conversation,
        GenerateOptions options,
        CancellationToken cancellationToken)
    {
        if (tool?.Execute is null)
        {
            return ToolResult.FromText(call.Id, $"Unknown tool: {call.Name}", isError: true);
        }

        var error = ArgumentError(tool, call, options);
        if (error is not null && options.RepairToolCall is not null)
        {
            var repaired = await options.RepairToolCall(call, error, cancellationToken).ConfigureAwait(false);
            if (repaired is not null)
            {
                call = repaired with { Id = call.Id };
                error = ArgumentError(tool, call, options);
            }
        }

        if (error is not null)
        {
            return ToolResult.FromText(call.Id, $"Invalid arguments for tool {call.Name}: {error}", isError: true);
        }

        try
        {
            var invocation = new ToolInvocation(call.Id, call.Name, (JsonObject)call.Arguments.DeepClone(), conversation);
            var value = await tool.Execute(invocation, cancellationToken).ConfigureAwait(false);
            return value switch
            {
                ToolResult result => result with { ToolCallId = call.Id },
                _ => new ToolResult(call.Id, JsonHelpers.ToNode(value) ?? JsonValue.Create("")),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.FromText(call.Id, $"Tool error ({call.Name}): {ex.Message}", isError: true);
        }
    }

    private static string? ArgumentError(Tool tool, ToolCall call, GenerateOptions options)
    {
        if (call.ArgumentsError is not null)
        {
            return call.ArgumentsError;
        }

        if (!options.ValidateToolArguments)
        {
            return null;
        }

        var errors = JsonSchemaValidator.Validate(call.Arguments, tool.Parameters);
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private static Tool? Find(IReadOnlyList<Tool>? tools, string name)
    {
        if (tools is null)
        {
            return null;
        }

        foreach (var tool in tools)
        {
            if (tool.Name == name)
            {
                return tool;
            }
        }

        return null;
    }
}
