using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Reactor.Server.Studio;

namespace Reactor.Server.Tests;

public sealed class StudioApiTests
{
    private const string Styled = """
        // A header comment
        digraph styled {
            graph [goal="Ship it", model_stylesheet="* { llm_model: base-model; } .deep-thinking { llm_model: deep-model; reasoning_effort: high; } #review { llm_provider: anthropic; }"]
            node [timeout="5m"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            subgraph cluster_design {
                label="Deep thinking"
                design [prompt="Design $goal"]
            }
            build [prompt="Build", llm_model="pinned-model"]
            review [shape=hexagon, label="Ship?"]
            start -> design -> build -> review
            review -> exit [label="[S] Ship"]
            review -> build [label="[R] Rework"]
        }
        """;

    [Fact]
    public async Task Info_describes_the_server_backend_and_features()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions
        {
            WorkingDirectory = dir,
            BackendKind = "copilot",
            BackendDescription = "GitHub Copilot SDK",
            BackendRoutes = ["copilot", "copilot-cli"],
        });

        var info = await server.GetJsonAsync("/studio/api/info");
        Assert.Equal("Reactor", info["name"]!.GetValue<string>());
        Assert.Equal("copilot", info["backend"]!["kind"]!.GetValue<string>());
        Assert.Equal(["copilot", "copilot-cli"], info["backend"]!["routes"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Contains("codergen", info["handler_types"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Contains("workspace", info["features"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.False(info["auth"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Parse_returns_editor_metadata_with_stylesheet_provenance()
    {
        await using var server = await ServerHarness.StartAsync();
        var response = await server.Client.PostAsync("/studio/api/graph/parse", new StringContent(Styled, Encoding.UTF8, "text/vnd.graphviz"));
        var body = await ServerHarness.ReadJsonAsync(response);

        Assert.True(body["ok"]!.GetValue<bool>(), body.ToJsonString());
        Assert.True(body["valid"]!.GetValue<bool>(), body.ToJsonString());
        var graph = body["graph"]!.AsObject();
        Assert.Equal("styled", graph["name"]!.GetValue<string>());
        Assert.Equal("5m", graph["node_defaults"]!["timeout"]!.GetValue<string>());

        var nodes = graph["nodes"]!.AsArray().ToDictionary(n => n!["id"]!.GetValue<string>(), n => n!.AsObject());
        var design = nodes["design"];
        Assert.Equal(["prompt"], design["explicit"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Equal("5m", design["attributes"]!["timeout"]!.GetValue<string>());
        Assert.Equal(["deep-thinking"], design["classes"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(0, design["subgraph"]!.GetValue<int>());
        Assert.Equal("deep-model", design["effective"]!["llm_model"]!["value"]!.GetValue<string>());
        Assert.Equal(".deep-thinking", design["effective"]!["llm_model"]!["selector"]!.GetValue<string>());
        Assert.Equal("stylesheet", design["effective"]!["reasoning_effort"]!["source"]!.GetValue<string>());

        Assert.Equal("node", nodes["build"]["effective"]!["llm_model"]!["source"]!.GetValue<string>());
        Assert.Equal("pinned-model", nodes["build"]["effective"]!["llm_model"]!["value"]!.GetValue<string>());
        Assert.Equal("wait.human", nodes["review"]["handler"]!.GetValue<string>());
        Assert.Equal("#review", nodes["review"]["effective"]!["llm_provider"]!["selector"]!.GetValue<string>());
        Assert.Null(nodes["build"]["subgraph"]);

        var subgraph = graph["subgraphs"]!.AsArray().Single()!;
        Assert.Equal("cluster_design", subgraph["name"]!.GetValue<string>());
        Assert.Equal("deep-thinking", subgraph["class"]!.GetValue<string>());
        Assert.Equal(["design"], subgraph["nodes"]!.AsArray().Select(n => n!.GetValue<string>()));

        var edges = graph["edges"]!.AsArray();
        Assert.Equal(5, edges.Count);
        Assert.Equal("[S] Ship", edges[3]!["attributes"]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task Parse_reports_syntax_errors_with_positions_and_lint_diagnostics()
    {
        await using var server = await ServerHarness.StartAsync();
        var broken = await server.Client.PostAsJsonAsync("/studio/api/graph/parse", new { dot = "digraph g {\n  a -> \n}" });
        var brokenBody = await ServerHarness.ReadJsonAsync(broken);
        Assert.False(brokenBody["ok"]!.GetValue<bool>());
        Assert.Equal(3, brokenBody["line"]!.GetValue<int>());

        var lint = await server.Client.PostAsJsonAsync("/studio/api/graph/parse", new { dot = "digraph g { start [shape=Mdiamond]; a [prompt=\"x\"]; start -> a }" });
        var lintBody = await ServerHarness.ReadJsonAsync(lint);
        Assert.True(lintBody["ok"]!.GetValue<bool>());
        Assert.False(lintBody["valid"]!.GetValue<bool>());
        Assert.Contains(lintBody["diagnostics"]!.AsArray(), d => d!["rule"]!.GetValue<string>() == "terminal_node");
    }

    [Fact]
    public async Task Prompt_preview_shows_the_prompt_a_stage_will_receive()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = new ControllableBackend() });
        var response = await server.Client.PostAsJsonAsync("/studio/api/graph/prompt-preview", new { dot = Styled, node = "build", history = new[] { "start", "design" } });
        var body = await ServerHarness.ReadJsonAsync(response);
        Assert.True(response.IsSuccessStatusCode, body.ToJsonString());
        var prompt = body["prompt"]!.GetValue<string>();
        Assert.Contains("Goal: Ship it", prompt);
        Assert.Contains("- design: success", prompt);
        Assert.Contains("\n\nBuild\n\n", prompt);
        Assert.EndsWith(Reactor.Execution.StageTiming.PromptLine(TimeSpan.FromMinutes(5)), prompt.TrimEnd());
        Assert.Equal("compact", body["fidelity"]!.GetValue<string>());

        var design = await server.Client.PostAsJsonAsync("/studio/api/graph/prompt-preview", new { dot = Styled, node = "design" });
        Assert.Contains("Design Ship it", (await ServerHarness.ReadJsonAsync(design))["prompt"]!.GetValue<string>());

        var unknown = await server.Client.PostAsJsonAsync("/studio/api/graph/prompt-preview", new { dot = Styled, node = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Models_come_from_the_configured_lister()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions
        {
            WorkingDirectory = dir,
            ListModels = _ => Task.FromResult<IReadOnlyList<ModelDescriptor>>([new ModelDescriptor { Id = "m1", Provider = "copilot", Source = "copilot", ReasoningEfforts = ["low", "high"] }]),
        });
        var body = await server.GetJsonAsync("/studio/api/models");
        var model = body["models"]!.AsArray().Single()!;
        Assert.Equal("m1", model["id"]!.GetValue<string>());
        Assert.Equal(["low", "high"], model["reasoning_efforts"]!.AsArray().Select(e => e!.GetValue<string>()));

        await using var bare = await ServerHarness.StartAsync();
        var none = await bare.GetJsonAsync("/studio/api/models");
        Assert.Empty(none["models"]!.AsArray());
        Assert.NotNull(none["error"]);
    }

    [Fact]
    public async Task Workspace_lists_reads_and_saves_pipelines_with_layout_sidecars()
    {
        await using var server = await ServerHarness.StartAsync();
        var root = server.Dir.Path;
        Directory.CreateDirectory(Path.Combine(root, "pipelines"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        Directory.CreateDirectory(Path.Combine(root, ".reactor"));
        await File.WriteAllTextAsync(Path.Combine(root, "pipelines", "a.dot"), Styled);
        await File.WriteAllTextAsync(Path.Combine(root, "broken.dot"), "digraph {");
        await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "skip.dot"), "digraph s {}");
        await File.WriteAllTextAsync(Path.Combine(root, ".reactor", "skip.dot"), "digraph s {}");

        var list = await server.GetJsonAsync("/studio/api/workspace");
        var files = list["files"]!.AsArray();
        Assert.Equal(["broken.dot", "pipelines/a.dot"], files.Select(f => f!["path"]!.GetValue<string>()));
        Assert.NotNull(files[0]!["error"]);
        Assert.Equal("styled", files[1]!["graph_name"]!.GetValue<string>());
        Assert.Equal(5, files[1]!["node_count"]!.GetValue<int>());
        Assert.False(files[1]!["has_layout"]!.GetValue<bool>());

        var file = await server.GetJsonAsync("/studio/api/workspace/file?path=pipelines/a.dot");
        Assert.Equal(Styled, file["dot"]!.GetValue<string>());
        Assert.Null(file["layout"]);
        var modified = file["modified"]!.GetValue<DateTimeOffset>();

        var layout = new JsonObject { ["version"] = 1, ["nodes"] = new JsonObject { ["design"] = new JsonObject { ["x"] = 10, ["y"] = 20 } } };
        var save = await server.Client.PutAsJsonAsync("/studio/api/workspace/file", new { path = "pipelines/a.dot", dot = Styled + "\n", layout, base_modified = modified });
        Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync());
        Assert.Equal(Styled + "\n", await File.ReadAllTextAsync(Path.Combine(root, "pipelines", "a.dot")));
        var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "pipelines", "a.layout.json")))!;
        Assert.Equal(10, sidecar["nodes"]!["design"]!["x"]!.GetValue<int>());

        // A stale base_modified is a conflict; omitting the layout keeps the sidecar.
        var stale = await server.Client.PutAsJsonAsync("/studio/api/workspace/file", new { path = "pipelines/a.dot", dot = Styled, base_modified = modified.AddMinutes(-5) });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var created = await server.Client.PutAsJsonAsync("/studio/api/workspace/file", new { path = "new/dir/b.dot", dot = "digraph b {}" });
        Assert.True(created.IsSuccessStatusCode);
        Assert.True(File.Exists(Path.Combine(root, "new", "dir", "b.dot")));
        Assert.False(File.Exists(Path.Combine(root, "new", "dir", "b.layout.json")));

        var reread = await server.GetJsonAsync("/studio/api/workspace/file?path=pipelines/a.dot");
        Assert.Equal(20, reread["layout"]!["nodes"]!["design"]!["y"]!.GetValue<int>());
        Assert.True((await server.GetJsonAsync("/studio/api/workspace"))["files"]!.AsArray().Single(f => f!["path"]!.GetValue<string>() == "pipelines/a.dot")!["has_layout"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("../escape.dot")]
    [InlineData("a/../../escape.dot")]
    [InlineData("C:/abs.dot")]
    [InlineData("/abs.dot")]
    [InlineData(".git/config.dot")]
    [InlineData("node_modules/x.dot")]
    [InlineData("notes.txt")]
    public async Task Workspace_rejects_paths_outside_or_hidden_or_non_pipeline(string path)
    {
        await using var server = await ServerHarness.StartAsync();
        var save = await server.Client.PutAsJsonAsync("/studio/api/workspace/file", new { path, dot = "digraph x {}" });
        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        var read = await server.Client.GetAsync("/studio/api/workspace/file?path=" + Uri.EscapeDataString(path));
        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
    }

    [Fact]
    public async Task Studio_shell_and_console_are_public_but_the_studio_api_is_not()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, AuthToken = "s3cret" });
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/")).StatusCode);
        var console = await server.Client.GetAsync("/console");
        Assert.Equal(HttpStatusCode.OK, console.StatusCode);
        Assert.Contains("pipeline console", await console.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync("/studio/api/info")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync("/studio/api/workspace")).StatusCode);
    }

    [Fact]
    public void Layout_sidecar_names_follow_the_pipeline_file()
    {
        Assert.Equal("a/b.layout.json", StudioWorkspace.LayoutPathFor("a/b.dot"));
        Assert.Equal("x/Factory.layout.json", StudioWorkspace.LayoutPathFor("x/Factory.DOT"));
    }
}
