import { describe, expect, it } from "vitest";
import type { Telemetry } from "../../api/types";
import { diffDocs } from "../../model/diff";
import { emptyDoc, type StudioDoc } from "../../model/types";
import type { SimReport } from "../tunnel/scenario";
import { buildEvidence, proposalOverlay } from "./evidence";
import { findingSummary, parseFinding, reviewVerdict } from "./findings";

const doc = (): StudioDoc => ({
  ...emptyDoc("flaky"),
  nodes: [
    { id: "start", attrs: { shape: "Mdiamond" }, group: null },
    { id: "test", attrs: { shape: "parallelogram", label: "Run tests", tool_command: "pytest" }, group: null },
    { id: "fix", attrs: { label: "Fix failures", prompt: "Fix" }, group: null },
    { id: "exit", attrs: { shape: "Msquare" }, group: null },
  ],
  edges: [
    { uid: "e0", from: "start", to: "test", attrs: {} },
    { uid: "e1", from: "test", to: "exit", attrs: { condition: "outcome=success" } },
    { uid: "e2", from: "test", to: "fix", attrs: { condition: "outcome=fail" } },
    { uid: "e3", from: "fix", to: "test", attrs: {} },
  ],
});

describe("architect evidence", () => {
  it("summarizes the flight record and the wind tunnel with the numbers that matter", () => {
    const record: Telemetry = {
      name: "flaky",
      runs: 10,
      window: { from: "2026-10-20T10:00:00Z", to: "2026-10-23T10:00:00Z" },
      outcomes: { success: 9, fail: 1 },
      duration_ms: { mean: 3000, p50: 2400, p95: 7800 },
      cost: { ai_units: { mean: 12.4, total: 124 } },
      nodes: {
        test: { runs: 10, visits: 32, mean_visits: 3.2, outcomes: { success: 10, fail: 22 }, fail_rate: 0.6875, retries: 0, duration_ms: { mean: 700, p50: 711, p95: 1200 }, top_failures: [{ reason: "Tool command exited with code 1", count: 22 }] },
        fix: { runs: 6, visits: 22, mean_visits: 3.67, outcomes: { success: 22 }, retries: 0, cost: { ai_units: 2.5 }, models: { "claude-sonnet-5.5": 22 } },
      },
      edges: [
        { from: "test", to: "fix", count: 22, runs: 6 },
        { from: "fix", to: "test", count: 22, runs: 6 },
        { from: "start", to: "test", count: 10, runs: 10 },
      ],
      series: [],
    };
    const report = {
      runs: 500,
      success_rate: 0.898,
      success_ci: [0.869, 0.922],
      outcomes: { success: 449, fail: 51 },
      steps: { mean: 10.9, max: 45 },
      failures: [{ kind: "fail", reason: "[Simulated] failure at deploy", count: 51, sample: { seed: 1, run_index: 3 }, last_node: "deploy", nodes: { deploy: 51 } }],
      nodes: { test: { mean_visits: 3 }, fix: { mean_visits: 2 } },
    } as unknown as SimReport;

    const text = buildEvidence(doc(), record, report, "calibrated to the flight record");

    expect(text).toContain("### Flight record: the last 10 real flights (2026-10-20 to 2026-10-23)");
    expect(text).toContain("- Landed 90% (success 9, fail 1 of 10). Duration p50 2.4s, p95 7.8s. Cost 12.4 AI units per flight.");
    expect(text).toContain('`test` (Run tests): reached in 100% of flights, ×3.2 visits per flight, 31% of 32 visits succeed, p50 711ms. Fails with: "Tool command exited with code 1" ×22.');
    expect(text).toContain("`fix` (Fix failures): reached in 60% of flights, ×3.7 visits per flight, 100% of 22 visits succeed, 2.50 AIU per visit, model claude-sonnet-5.5.");
    expect(text).toContain("- Repeated routes: test → fix ×3.7 per flight that took it; fix → test ×3.7 per flight that took it.");
    expect(text).toContain("### Wind tunnel: 500 simulated flights (calibrated to the flight record)");
    expect(text).toContain("- Landed clean 89.8% (95% CI 86.9%–92.2%). failed: 10.2%. Stage executions per flight: mean 10.9, max 45.");
    expect(text).toContain("- fail 10.2%: [Simulated] failure at deploy\n");
    expect(text).toContain("- Hot spots: test ×3.0, fix ×2.0.");
    expect(text).not.toContain("Simulated per flight");
    expect(buildEvidence(doc(), null, null, null)).toBe("");
  });

  it("states clean landings, failed-stage landings, time, cost, step-limit spread, and metrics at landing", () => {
    const report = {
      runs: 1000,
      success_rate: 0.95,
      success_ci: [0.935, 0.962],
      clean_success_rate: 0.9,
      clean_success_ci: [0.88, 0.917],
      failed_stage_landings: 50,
      outcomes: { success: 950, step_limit: 50 },
      steps: { mean: 47.8, max: 300 },
      simulated_duration_ms: { mean: 18_000_000, p50: 16_000_000, p90: 27_360_000, max: 40_000_000 },
      simulated_cost_usd: { mean: 12.5, p50: 11, p90: 19.25, max: 40 },
      failures: [{ kind: "step_limit", reason: "Maximum step count (300) exceeded.", count: 50, sample: { seed: 1, run_index: 9 }, last_node: "fix", nodes: { fix: 30, test: 20 } }],
      nodes: { fix: { mean_visits: 1, failed_stage_landings: 50 }, test: { mean_visits: 1 } },
      landed_metrics: { score: { flights: 950, mean: 4.234, min: 3, p10: 3, p50: 4, p90: 5, max: 5 } },
    } as unknown as SimReport;

    const text = buildEvidence(doc(), null, report, "realistic scenario");

    expect(text).toContain(
      "- Landed clean 90.0% (95% CI 88.0%–91.7%). Another 5.0% landed with failed stages behind them (a failure flowed on along an unconditional edge: `fix` in 5.0%). never converged: 5.0%. Stage executions per flight: mean 47.8, max 300.",
    );
    expect(text).toContain("- Simulated per flight: time mean 5h 0m, p90 7h 36m, max 11h 6m; cost ~$12.50 mean, $19.25 p90.");
    expect(text).toContain("- step limit 5.0%: Maximum step count (300) exceeded. (last stages fix ×30, test ×20)");
    expect(text).toContain("- Metrics at landing: score mean 4.23 (p10 3, p90 5).");
  });

  it("marks new and changed stages and new routes on the proposal", () => {
    const before = doc();
    const after = doc();
    after.nodes[2] = { ...after.nodes[2], attrs: { ...after.nodes[2].attrs, prompt: "Fix the failing tests", inputs: "test_report" } };
    after.nodes.push({ id: "lint", attrs: { shape: "parallelogram", tool_command: "ruff check" }, group: null });
    after.edges.push({ uid: "e4", from: "fix", to: "lint", attrs: {} });
    const overlay = proposalOverlay(diffDocs(before, after));
    expect(overlay.nodes.lint).toEqual({ state: "success", badge: "new" });
    expect(overlay.nodes.fix?.badge).toBe("changed");
    expect(overlay.nodes.fix?.readouts?.map((r) => `${r.label}:${r.value}`)).toEqual(["prompt:edited", "inputs:added"]);
    expect(overlay.edges["fix->lint"]).toEqual({ state: "taken", text: "new" });
    expect(overlay.nodes.test).toBeUndefined();
  });
});

describe("architect findings", () => {
  it("reads design-rule, contract, forecast, wind tunnel, and self-review findings", () => {
    expect(parseFinding("[fail_flows_onward] stage 'plan': If 'plan' fails, the run continues to 'build' as if it had succeeded: nothing routes outcome=fail. (fix: Put condition=\"outcome=success\" on the edge to 'build'.)")).toEqual({
      kind: "lint",
      rule: "fail_flows_onward",
      node: "plan",
      edge: undefined,
      text: "If 'plan' fails, the run continues to 'build' as if it had succeeded: nothing routes outcome=fail.",
      fix: "Put condition=\"outcome=success\" on the edge to 'build'.",
    });
    expect(parseFinding("[failure_evidence] edge check -> fix: The fixer starts blind.")).toMatchObject({ kind: "lint", rule: "failure_evidence", edge: "check -> fix", text: "The fixer starts blind." });
    expect(parseFinding("Contract: the brief names `qa/shots/`, but the draft never uses it and uses `qa/screenshots/` (build, qa) instead. Use the brief's paths exactly.")).toMatchObject({
      kind: "contract",
      text: "the brief names `qa/shots/`, but the draft never uses it and uses `qa/screenshots/` (build, qa) instead. Use the brief's paths exactly.",
    });
    expect(parseFinding("Forecast: a typical flight takes 5.2 h (p90 7.6 h), over the brief's time budget (a few hours).").kind).toBe("forecast");
    expect(parseFinding("Wind tunnel: even with every stage succeeding, 100% of simulated flights do not land (step_limit: Maximum step count exceeded., last stage 'polish').")).toMatchObject({ kind: "tunnel", node: "polish" });
    expect(parseFinding("Self-review (not applied: no drafts left): 1. Raise the build timeout.")).toMatchObject({ kind: "review", text: "1. Raise the build timeout." });
    expect(parseFinding("The graph is renamed from 'a' to 'b'.").kind).toBe("note");
    expect(findingSummary("[unbounded_loop] stage 'fix': The loop fix → test has no bound.")).toBe("unbounded_loop (fix): The loop fix → test has no bound.");
    expect(findingSummary("Contract: uses `qa/screenshots/`.")).toBe("Contract: uses qa/screenshots/.");
  });

  it("reads the self-review verdict like the server", () => {
    expect(reviewVerdict("Looks right.\n\nVERDICT: PASS")).toBe("pass");
    expect(reviewVerdict("**VERDICT: REVISE**\n1. Bound the polish loop.")).toBe("revise");
    expect(reviewVerdict("No verdict line.")).toBe("pass");
    expect(reviewVerdict("(not reviewed: backend error)")).toBe("skipped");
    expect(reviewVerdict("")).toBeNull();
  });
});
