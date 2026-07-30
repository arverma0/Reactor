using System.Net;
using System.Net.Http.Json;
using Reactor.Execution;
using Reactor.Graphs;
using Reactor.Handlers;

namespace Reactor.Server.Tests;

/// <summary>Cost budgets (<c>max_cost</c>) through the HTTP API: per-run budgets, the server default, and resuming with a higher one.</summary>
public sealed class BudgetApiTests
{
    private const string Pipeline = """
        digraph budget {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            a [prompt="a"] b [prompt="b"] c [prompt="c"]
            start -> a -> b -> c -> exit
        }
        """;

    /// <summary>Every stage costs $0.60.</summary>
    private sealed class PricedBackend : ICodergenBackend
    {
        public Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken) =>
            Task.FromResult(CodergenResult.FromText("ok").WithMetadata(new Dictionary<string, object?> { ["cost_usd"] = 0.6 }));
    }

    [Fact]
    public async Task A_run_stops_at_its_budget_and_a_resume_with_a_higher_budget_finishes_it()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = new PricedBackend() });

        var response = await server.Client.PostAsJsonAsync("/pipelines", new { dot = Pipeline, max_cost = 1.0 });
        var created = await ServerHarness.ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = created["id"]!.GetValue<string>();
        var stopped = await server.WaitForStatusAsync(id, "fail", "success", "error");
        Assert.Equal("fail", stopped["status"]!.GetValue<string>());
        Assert.Contains("Budget exceeded", stopped.ToJsonString());
        var detail = await server.GetJsonAsync($"/pipelines/{id}");
        Assert.True(detail["result"]!["budget_exceeded"]!.GetValue<bool>());
        Assert.Equal(1.2, detail["result"]!["usage"]!["cost_usd"]!.GetValue<double>(), 6);
        Assert.Equal(1.0, detail["max_cost"]!.GetValue<double>());

        var resume = await server.Client.PostAsJsonAsync($"/pipelines/{id}/resume", new { max_cost = 10.0 });
        Assert.Equal(HttpStatusCode.Accepted, resume.StatusCode);
        var finished = await server.WaitForStatusAsync(id, "success", "fail", "error");
        Assert.Equal("success", finished["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_server_default_applies_and_invalid_budgets_are_rejected()
    {
        await using var server = await ServerHarness.StartAsync(dir => new ReactorServerOptions { WorkingDirectory = dir, Backend = new PricedBackend(), MaxCostPerRunUsd = 0.5 });

        var id = (await server.StartRunAsync(Pipeline))["id"]!.GetValue<string>();
        Assert.Contains("Budget exceeded", (await server.WaitForStatusAsync(id, "fail", "success", "error")).ToJsonString());

        var invalid = await server.Client.PostAsJsonAsync("/pipelines", new { dot = Pipeline, max_cost = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidResume = await server.Client.PostAsJsonAsync($"/pipelines/{id}/resume", new { max_cost = -2 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidResume.StatusCode);
    }
}
