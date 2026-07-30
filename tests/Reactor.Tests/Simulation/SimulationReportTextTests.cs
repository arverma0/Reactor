using Reactor.Simulation;

namespace Reactor.Tests.Simulation;

[Collection(Sim.Collection)]
public sealed class SimulationReportTextTests
{
    /// <summary>a's failures flow on to b; b's failures end the flight.</summary>
    private const string Pipeline = """
        digraph Chain {
            start [shape=Mdiamond] exit [shape=Msquare]
            a [prompt="a"]
            b [prompt="b"]
            start -> a -> b
            b -> exit [condition="outcome=success"]
        }
        """;

    [Fact]
    public async Task The_text_report_leads_with_clean_landings_and_points_at_stages()
    {
        var report = await Sim.ReportAsync(Pipeline, """
            {"nodes": {"a": {"outcomes": {"success": 0.8, "fail": 0.2}, "duration_ms": "90m", "cost": 1.25},
                       "b": {"outcomes": {"success": 0.9, "fail": 0.1}, "metrics": {"score": {"values": [3, 4, 5]}}}}}
            """, runs: 300, seed: 2);

        var text = SimulationReportText.Format(report, new SimulationTextOptions
        {
            Pipeline = "Chain",
            Scenario = "custom",
            MinSuccess = 0.95,
            ReplayCommand = "reactor simulate chain.dot --seed 2 --trace {run}",
        });

        Assert.StartsWith("Wind tunnel: Chain · custom scenario · 300 flights · seed 2 · flown in ", text, StringComparison.Ordinal);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains(lines, l => l.StartsWith("  Landed clean ", StringComparison.Ordinal) && l.Contains("95% CI", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  Landed with failed stages ", StringComparison.Ordinal) && l.Contains("failures flowed on at a ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  failed · ", StringComparison.Ordinal) && l.EndsWith("[Simulated] failure at b", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("      at b · replay run ", StringComparison.Ordinal));
        Assert.Contains("Landed with failed stages (a failure flowed on along an unconditional edge)", text, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("Simulated time per flight", StringComparison.Ordinal) && l.Contains("mean 1h 30m", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Simulated cost per flight", StringComparison.Ordinal) && l.Contains("max $1.25", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  score ", StringComparison.Ordinal) && l.Contains("3: ", StringComparison.Ordinal) && l.Contains("5: ", StringComparison.Ordinal));
        Assert.Contains("Replay a flight: reactor simulate chain.dot --seed 2 --trace <run>", text, StringComparison.Ordinal);
        Assert.Contains("✖ Clean landing rate ", text, StringComparison.Ordinal);
        Assert.Contains("is below the required 95.0%.", text, StringComparison.Ordinal);
        Assert.Contains("none: no stage runs more than once per flight on average", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clean_report_says_so_and_omits_empty_sections()
    {
        var report = await Sim.ReportAsync(Pipeline, runs: 50);

        var text = SimulationReportText.Format(report, new SimulationTextOptions { MinSuccess = 0.95 });

        Assert.Contains("✔ Clean landing rate 100.0% meets the required 95.0%.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("How flights go down", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Landed with failed stages (", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Metrics at landing", text, StringComparison.Ordinal);
        Assert.Contains("not modeled (no durations in the scenario)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(850, "850ms")]
    [InlineData(4_200, "4.2s")]
    [InlineData(725_000, "12m 05s")]
    [InlineData(18_120_000, "5h 02m")]
    [InlineData(27_360_000, "7h 36m")]
    public void Durations_read_naturally(double milliseconds, string expected) => Assert.Equal(expected, SimulationReportText.Duration(milliseconds));

    [Theory]
    [InlineData(0, "$0")]
    [InlineData(0.042, "$0.042")]
    [InlineData(12.5, "$12.50")]
    [InlineData(4321.4, "$4,321")]
    public void Money_reads_naturally(double usd, string expected) => Assert.Equal(expected, SimulationReportText.Money(usd));
}
