using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Reactor.Server.Tests;

/// <summary>Artifact pins (Reactor extension) through the HTTP API: prompt previews, the event stream, and the context.</summary>
public sealed class ArtifactApiTests
{
    private const string Pipeline = """
        digraph pins {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            architect [prompt="Design", outputs="design:docs/DESIGN.md, notes"]
            build [prompt="Build", inputs="design, notes"]
            start -> architect -> build -> exit
        }
        """;

    [Fact]
    public async Task Prompt_preview_shows_artifact_sections()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = new ControllableBackend() });

        var build = await PreviewAsync(server, new { dot = Pipeline, node = "build" });
        Assert.EndsWith("Build\n\n## Inputs\n- `design` — not produced yet in this run.\n- `notes` — not produced yet in this run.", build);

        var seeded = await PreviewAsync(server, new
        {
            dot = Pipeline,
            node = "build",
            context = new Dictionary<string, object>
            {
                ["artifacts.design"] = new { name = "design", kind = "file", path = "docs/DESIGN.md", producer = "architect", version = 2 },
                ["artifacts.notes"] = "Use SQLite.",
            },
        });
        Assert.Contains("- `design` — file `docs/DESIGN.md` (version 2, written by stage `architect`). Read it.\n- `notes` — text:\n  > Use SQLite.", seeded);
        Assert.DoesNotContain("artifacts.", seeded);

        var architect = await PreviewAsync(server, new { dot = Pipeline, node = "architect" });
        Assert.EndsWith(
            "## Outputs\nProduce these before you finish:\n- `design` → write the file `docs/DESIGN.md` (relative to the working directory)\n" +
            "- `notes` → your final response is recorded as this artifact",
            architect);
    }

    [Fact]
    public async Task Artifact_events_stream_and_records_appear_in_the_context()
    {
        await using var server = await ServerHarness.StartAsync();
        var id = (await server.StartRunAsync(Pipeline))["id"]!.GetValue<string>();
        await server.WaitForStatusAsync(id, "success");

        var text = (await server.Client.GetStringAsync($"/pipelines/{id}/events")).ReplaceLineEndings("\n");
        var frames = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(block => block.Contains("event: ArtifactProduced\n", StringComparison.Ordinal))
            .Select(block => JsonNode.Parse(block.Split('\n').Single(l => l.StartsWith("data: ", StringComparison.Ordinal))[6..])!)
            .ToList();
        Assert.Equal(2, frames.Count);
        Assert.Equal("architect", frames[0]["node_id"]!.GetValue<string>());
        Assert.Equal("design", frames[0]["name"]!.GetValue<string>());
        Assert.Equal("file", frames[0]["kind"]!.GetValue<string>());
        Assert.Equal("docs/DESIGN.md", frames[0]["path"]!.GetValue<string>());
        Assert.Equal(1, frames[0]["version"]!.GetValue<int>());
        Assert.Equal("notes", frames[1]["name"]!.GetValue<string>());
        Assert.Equal("text", frames[1]["kind"]!.GetValue<string>());

        var context = await server.GetJsonAsync($"/pipelines/{id}/context");
        var notes = context["context"]!["artifacts.notes"]!;
        Assert.Equal("[Simulated] Response for stage: architect", notes["text"]!.GetValue<string>());
        Assert.Equal("architect", notes["producer"]!.GetValue<string>());
    }

    [Fact]
    public async Task Studio_parse_describes_each_nodes_pins_and_info_advertises_artifacts()
    {
        await using var server = await ServerHarness.StartAsync();
        var dot = Pipeline.Replace("outputs=\"design:docs/DESIGN.md, notes\"", "outputs=\" design : docs\\\\DESIGN.md, notes, 1bad\"", StringComparison.Ordinal);

        var response = await server.Client.PostAsJsonAsync("/studio/api/graph/parse", new { dot });
        var body = await ServerHarness.ReadJsonAsync(response);

        var nodes = body["graph"]!["nodes"]!.AsArray().ToDictionary(n => n!["id"]!.GetValue<string>(), n => n!);
        var architect = nodes["architect"]["pins"]!;
        Assert.Empty(architect["inputs"]!.AsArray());
        Assert.Equal(
            """[{"name":"design","kind":"file","path":"docs/DESIGN.md"},{"name":"notes","kind":"text","path":null}]""",
            architect["outputs"]!.ToJsonString());
        Assert.Equal(["design", "notes"], nodes["build"]["pins"]!["inputs"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(nodes["start"]["pins"]!["outputs"]!.AsArray());
        Assert.False(body["valid"]!.GetValue<bool>());
        Assert.Contains(body["diagnostics"]!.AsArray(), d => d!["rule"]!.GetValue<string>() == "artifact_syntax" && d["node_id"]!.GetValue<string>() == "architect");

        var info = await server.GetJsonAsync("/studio/api/info");
        Assert.Contains("artifacts", info["features"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    private static async Task<string> PreviewAsync(ServerHarness server, object body)
    {
        var response = await server.Client.PostAsJsonAsync("/studio/api/graph/prompt-preview", body);
        var json = await ServerHarness.ReadJsonAsync(response);
        Assert.True(response.IsSuccessStatusCode, json.ToJsonString());
        return json["prompt"]!.GetValue<string>();
    }
}
