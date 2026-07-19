using System.Diagnostics;
using System.Text;
using Reactor.Execution;
using Reactor.Graphs;

namespace Reactor.Rendering;

/// <summary>Graphviz is not installed or failed to render.</summary>
public sealed class GraphvizException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Renders pipeline graphs with Graphviz, optionally colored by stage status.</summary>
public static class GraphRenderer
{
    private static readonly HashSet<string> VisualAttributes = new(StringComparer.Ordinal)
    {
        "label", "shape", "style", "color", "fillcolor", "fontcolor", "fontname", "fontsize", "penwidth", "rankdir", "rank",
        "tooltip", "xlabel", "weight", "constraint", "arrowhead", "dir", "nodesep", "ranksep", "splines", "bgcolor",
    };

    /// <summary>Fill colors by stage status (wire names) plus <c>running</c>.</summary>
    public static IReadOnlyDictionary<string, string> StatusColors { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["success"] = "#c8e6c9",
        ["partial_success"] = "#fff59d",
        ["fail"] = "#ffcdd2",
        ["retry"] = "#ffe0b2",
        ["skipped"] = "#eeeeee",
        ["running"] = "#bbdefb",
    };

    /// <summary>
    /// DOT for display: only visual attributes are kept (prompts are omitted), conditions become edge labels when the
    /// edge has none, and nodes are filled by <paramref name="statuses"/> (node id → status wire name).
    /// <paramref name="currentNode"/> is highlighted as running.
    /// </summary>
    public static string ToDisplayDot(Graph graph, IReadOnlyDictionary<string, string>? statuses = null, string? currentNode = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var display = graph.Clone();
        foreach (var edge in display.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge[AttrKeys.Label]) && !string.IsNullOrWhiteSpace(edge.Condition))
            {
                edge.Attributes[AttrKeys.Label] = edge.Condition;
            }
        }

        if (string.IsNullOrWhiteSpace(display[AttrKeys.Label]) && !string.IsNullOrWhiteSpace(display.Goal))
        {
            display.Attributes[AttrKeys.Label] = display.Goal;
        }

        return DotWriter.Write(
            display,
            node =>
            {
                var status = node.Id == currentNode ? "running" : statuses?.GetValueOrDefault(node.Id);
                if (status is null || !StatusColors.TryGetValue(status, out var color))
                {
                    return null;
                }

                var style = node["style"] is { Length: > 0 } existing && !existing.Contains("filled", StringComparison.Ordinal) ? existing + ",filled" : "filled";
                var overrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["style"] = style, ["fillcolor"] = color };
                if (status == "running")
                {
                    overrides["penwidth"] = "2.5";
                }

                return overrides;
            },
            key => VisualAttributes.Contains(key));
    }

    /// <summary>Statuses from a run's node outcomes (for <see cref="ToDisplayDot"/>).</summary>
    public static IReadOnlyDictionary<string, string> StatusesFrom(IReadOnlyDictionary<string, Outcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        return outcomes.ToDictionary(kv => kv.Key, kv => kv.Value.Status.ToWire(), StringComparer.Ordinal);
    }

    /// <summary>Runs Graphviz <c>dot</c> to render DOT source.</summary>
    /// <param name="dot">DOT source.</param>
    /// <param name="format">Output format (<c>svg</c>, <c>png</c>, <c>pdf</c>, ...).</param>
    /// <param name="dotExecutable">Path to <c>dot</c> (default: <c>dot</c> on PATH).</param>
    /// <param name="cancellationToken">Cancels rendering (the process is killed).</param>
    /// <exception cref="GraphvizException">Graphviz is missing or reported an error.</exception>
    public static async Task<byte[]> RenderAsync(string dot, string format = "svg", string? dotExecutable = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dot);
        if (string.IsNullOrWhiteSpace(format) || !format.All(char.IsLetterOrDigit))
        {
            throw new ArgumentException($"Invalid output format '{format}'.", nameof(format));
        }

        var startInfo = new ProcessStartInfo(dotExecutable ?? "dot")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add("-T" + format);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GraphvizException("Graphviz 'dot' was not found. Install Graphviz (https://graphviz.org) and make sure 'dot' is on PATH.", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(dot.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(copy, errors).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new GraphvizException($"Graphviz failed (exit code {process.ExitCode}): {(await errors.ConfigureAwait(false)).Trim()}");
            }

            return output.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GraphvizException("Graphviz timed out after 30 seconds.");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
