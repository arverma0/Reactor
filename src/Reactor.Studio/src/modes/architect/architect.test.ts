import { describe, expect, it } from "vitest";
import type { Telemetry } from "../../api/types";
import { diffDocs } from "../../model/diff";
import { emptyDoc, type StudioDoc } from "../../model/types";
import type { SimReport } from "../tunnel/scenario";
import { buildEvidence, proposalOverlay } from "./evidence";

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
      window: { from: "2026-07-20T10:00:00Z", to: "2026-07-23T10:00:00Z" },
      outcomes: { success: 9, fail: 1 },
      duration_ms: { mean: 3000, p50: 2400, p95: 7800 },
      cost: { ai_units: { mean: 12.4, total: 124 } },
      nodes: {
        test: { runs: 10, visits: 32, mean_visits: 3.2, outcomes: { success: 10, fail: 22 }, fail_rate: 0.6875, retries: 0, duration_ms: { mean: 700, p50: 711, p95: 1200 }, top_failures: [{ reason: "Tool command exited with code 1", count: 22 }] },
        fix: { runs: 6, visits: 22, mean_visits: 3.67, outcomes: { success: 22 }, retries: 0, cost: { ai_units: 2.5 }, models: { "claude-sonnet-5": 22 } },
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
      failures: [{ kind: "fail", reason: "[Simulated] failure", count: 51, sample: { seed: 1, run_index: 3 }, last_node: "deploy" }],
      nodes: { test: { mean_visits: 3 }, fix: { mean_visits: 2 } },
    } as unknown as SimReport;

    const text = buildEvidence(doc(), record, report, "calibrated to the flight record");

    expect(text).toContain("### Flight record: the last 10 real flights (2026-07-20 to 2026-07-23)");
    expect(text).toContain("- Landed 90% (success 9, fail 1 of 10). Duration p50 2.4s, p95 7.8s. Cost 12.4 AI units per flight.");
    expect(text).toContain('`test` (Run tests): reached in 100% of flights, ×3.2 visits per flight, 31% of 32 visits succeed, p50 711ms. Fails with: "Tool command exited with code 1" ×22.');
    expect(text).toContain("`fix` (Fix failures): reached in 60% of flights, ×3.7 visits per flight, 100% of 22 visits succeed, 2.50 AIU per visit, model claude-sonnet-5.");
    expect(text).toContain("- Repeated routes: test → fix ×3.7 per flight that took it; fix → test ×3.7 per flight that took it.");
    expect(text).toContain("### Wind tunnel: 500 simulated flights (calibrated to the flight record)");
    expect(text).toContain("- Landed 89.8% (95% CI 86.9%–92.2%). failed: 10.2%. Stage executions per flight: mean 10.9, max 45.");
    expect(text).toContain("- fail 10.2%: [Simulated] failure (last stage deploy)");
    expect(text).toContain("- Hot spots: test ×3.0, fix ×2.0.");
    expect(buildEvidence(doc(), null, null, null)).toBe("");
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
