namespace Reactor.Cli.Tests;

public class RenderFormatTests
{
    [Theory]
    [InlineData(null, "graph.png", "png")]
    [InlineData(null, "out/Graph.PDF", "pdf")]
    [InlineData(null, "graph.jpeg", "jpg")]
    [InlineData(null, "graph.gv", "dot")]
    [InlineData(null, "graph", "svg")]
    [InlineData(null, "-", "svg")]
    [InlineData(null, null, "svg")]
    [InlineData("PNG", null, "png")]
    [InlineData("png", "graph.png", "png")]
    [InlineData("jpeg", "graph.jpg", "jpg")]
    [InlineData("png", "graph.out", "png")]
    [InlineData("pdf", "-", "pdf")]
    public void Resolves_the_format_from_the_flag_and_the_output_extension(string? format, string? output, string expected)
    {
        Assert.True(RenderFormat.TryResolve(format, output, out var resolved, out var error), error);
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("svg", "graph.png", "does not match")]
    [InlineData("dot", "graph.svg", "does not match")]
    [InlineData(null, "graph.txt", "Cannot tell the output format")]
    [InlineData("s v g", null, "Invalid --format")]
    [InlineData("png:cairo", null, "Invalid --format")]
    public void Rejects_formats_that_disagree_with_the_output_or_cannot_be_told(string? format, string? output, string message)
    {
        Assert.False(RenderFormat.TryResolve(format, output, out _, out var error));
        Assert.Contains(message, error, StringComparison.Ordinal);
    }
}
