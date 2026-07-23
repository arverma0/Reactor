import { describe, expect, it } from "vitest";
import type { HistorySummary, PipelineEventJson, RunSummary, Telemetry } from "../../api/types";
import { describeDiff, diffDocs } from "../../model/diff";
import { emptyDoc, type StudioDoc } from "../../model/types";
import { MAX_DURATION_MS, buildScenario, calibrate, defaultSettings, predictedCost, withoutDurations, type SimReport } from "../tunnel/scenario";
import { checkpointBefore, checkpointNode, digest } from "./replay";
import { mergeFlights } from "./view";

const doc = (): StudioDoc => ({
  ...emptyDoc("flaky"),
  nodes: [
    { id: "start", attrs: { shape: "Mdiamond" }, group: null },
    { id: "plan", attrs: { prompt: "Plan" }, group: null },
    { id: "test", attrs: { shape: "parallelogram", tool_command: "pytest" }, group: null },
    { id: "fix", attrs: { prompt: "Fix" }, group: null },
    { id: "review", attrs: { prompt: "Review" }, group: null },
    { id: "gate", attrs: { shape: "hexagon", label: "Ship?" }, group: null },
    { id: "exit", attrs: { shape: "Msquare" }, group: null },
  ],
  edges: [
    { uid: "e0", from: "start", to: "plan", attrs: {} },
    { uid: "e1", from: "plan", to: "test", attrs: {} },
    { uid: "e2", from: "test", to: "review", attrs: { condition: "outcome=success" } },
    { uid: "e3", from: "test", to: "fix", attrs: { condition: "outcome=fail" } },
    { uid: "e4", from: "fix", to: "test", attrs: {} },
    { uid: "e5", from: "review", to: "gate", attrs: { label: "Approve" } },
    { uid: "e6", from: "review", to: "fix", attrs: { label: "Rework" } },
    { uid: "e7", from: "gate", to: "exit", attrs: { label: "[S] Ship" } },
    { uid: "e8", from: "gate", to: "plan", attrs: { label: "[R] Replan" } },
  ],
});

const at = (s: number) => new Date(Date.UTC(2026, 6, 23, 12, 0, s)).toISOString();

describe("fork checkpoints", () => {
  const events: PipelineEventJson[] = [
    { type: "PipelineStarted", timestamp: at(0) },
    { type: "StageStarted", timestamp: at(1), node_id: "start", index: 1, attempt: 1 },
    { type: "StageCompleted", timestamp: at(1), node_id: "start", status: "success" },
    { type: "StageStarted", timestamp: at(2), node_id: "plan", index: 2, attempt: 1 },
    { type: "StageCompleted", timestamp: at(3), node_id: "plan", status: "success" },
    { type: "StageStarted", timestamp: at(4), node_id: "test", index: 3, attempt: 1 },
    { type: "StageCompleted", timestamp: at(5), node_id: "test", status: "fail" },
    { type: "LoopRestarted", timestamp: at(6) },
    { type: "StageStarted", timestamp: at(7), node_id: "plan", index: 1, attempt: 1 },
    { type: "StageCompleted", timestamp: at(8), node_id: "plan", status: "success" },
    { type: "StageStarted", timestamp: at(9), node_id: "test", index: 2, attempt: 1 },
    { type: "StageFailed", timestamp: at(10), node_id: "test", error: "boom", will_retry: true },
    { type: "StageStarted", timestamp: at(11), node_id: "test", index: 2, attempt: 2 },
    { type: "StageCompleted", timestamp: at(12), node_id: "test", status: "success" },
  ];
  const checkpoints = ["0001-start", "0002-plan", "0003-test", "restart-1/0001-plan", "restart-1/0002-test"];

  it("restores the snapshot saved just before a stage, within its loop-restart segment", () => {
    const replay = digest(events);
    const [start, plan, test, plan2, test2] = replay.executions;
    expect(replay.executions.map((x) => [x.node, x.segment])).toEqual([
      ["start", 0],
      ["plan", 0],
      ["test", 0],
      ["plan", 1],
      ["test", 1],
      ["test", 1],
    ]);
    expect(checkpointBefore(start, checkpoints)).toBeNull();
    expect(checkpointBefore(plan, checkpoints)).toBe("0001-start");
    expect(checkpointBefore(test, checkpoints)).toBe("0002-plan");
    // The first stage after a loop restart has nothing earlier in its own directory.
    expect(checkpointBefore(plan2, checkpoints)).toBeNull();
    expect(checkpointBefore(test2, checkpoints)).toBe("restart-1/0001-plan");
    expect(checkpointNode("restart-1/0001-plan")).toBe("plan");
    expect(checkpointNode("0012-review_2")).toBe("review_2");
  });

  it("skips gaps left by parallel branch stages (they have no snapshots)", () => {
    const replay = digest([
      { type: "StageStarted", timestamp: at(1), node_id: "review", index: 7, attempt: 1 },
    ]);
    expect(checkpointBefore(replay.executions[0], ["0003-fanout", "0006-join"])).toBe("0006-join");
  });

  it("re-flies a parallel branch stage from before its parallel stage, whose snapshot is saved after every branch", () => {
    // plan → fanout → {sec, perf} → join → review; the fan-out takes index 3 before its branches take 4 and 5.
    const replay = digest([
      { type: "PipelineStarted", timestamp: at(0) },
      { type: "StageStarted", timestamp: at(1), node_id: "start", index: 1, attempt: 1 },
      { type: "StageCompleted", timestamp: at(1), node_id: "start", status: "success" },
      { type: "StageStarted", timestamp: at(2), node_id: "plan", index: 2, attempt: 1 },
      { type: "StageCompleted", timestamp: at(3), node_id: "plan", status: "success" },
      { type: "StageStarted", timestamp: at(4), node_id: "fanout", index: 3, attempt: 1 },
      { type: "StageStarted", timestamp: at(5), node_id: "sec", index: 4, attempt: 1 },
      { type: "StageStarted", timestamp: at(5), node_id: "perf", index: 5, attempt: 1 },
      { type: "StageCompleted", timestamp: at(7), node_id: "sec", status: "success" },
      { type: "StageCompleted", timestamp: at(8), node_id: "perf", status: "fail" },
      { type: "StageCompleted", timestamp: at(9), node_id: "fanout", status: "partial_success" },
      { type: "StageStarted", timestamp: at(10), node_id: "join", index: 6, attempt: 1 },
      { type: "StageCompleted", timestamp: at(11), node_id: "join", status: "success" },
      { type: "StageStarted", timestamp: at(12), node_id: "review", index: 7, attempt: 1 },
    ]);
    const checkpoints = ["0001-start", "0002-plan", "0003-fanout", "0006-join"];
    const by = (node: string) => replay.executions.find((x) => x.node === node)!;
    expect(replay.executions.map((x) => [x.node, x.parent ?? null, x.parentIndex ?? null])).toEqual([
      ["start", null, null],
      ["plan", null, null],
      ["fanout", null, null],
      ["sec", "fanout", 3],
      ["perf", "fanout", 3],
      ["join", null, null],
      ["review", null, null],
    ]);
    // Branch stages restore the state before the fan-out (which then runs every branch again), not 0003-fanout:
    // that snapshot already has the branches' results, and resuming it jumps straight to the join.
    expect(checkpointBefore(by("sec"), checkpoints)).toBe("0002-plan");
    expect(checkpointBefore(by("perf"), checkpoints)).toBe("0002-plan");
    expect(checkpointBefore(by("fanout"), checkpoints)).toBe("0002-plan");
    expect(checkpointBefore(by("join"), checkpoints)).toBe("0003-fanout");
    expect(checkpointBefore(by("review"), checkpoints)).toBe("0006-join");
  });

  it("does not nest stages under one a stopped run left open when the run resumes", () => {
    const replay = digest([
      { type: "PipelineStarted", timestamp: at(0) },
      { type: "StageStarted", timestamp: at(1), node_id: "implement", index: 2, attempt: 1 },
      { type: "PipelineStarted", timestamp: at(30) },
      { type: "StageStarted", timestamp: at(31), node_id: "implement", index: 2, attempt: 1 },
      { type: "StageCompleted", timestamp: at(35), node_id: "implement", status: "success" },
      { type: "StageStarted", timestamp: at(36), node_id: "test", index: 3, attempt: 1 },
    ]);
    expect(replay.executions.map((x) => x.parent ?? null)).toEqual([null, null, null]);
  });
});

describe("doc diff", () => {
  it("reports attribute, group, edge, and graph changes", () => {
    const before = doc();
    const after = doc();
    after.nodes[1] = { ...after.nodes[1], attrs: { prompt: "Plan carefully", llm_model: "claude-sonnet-5" } };
    after.nodes.push({ id: "lint", attrs: { shape: "parallelogram", tool_command: "ruff" }, group: null });
    after.nodes = after.nodes.filter((n) => n.id !== "gate");
    after.edges = after.edges.filter((e) => e.from !== "gate" && e.to !== "gate");
    after.edges.push({ uid: "e9", from: "review", to: "exit", attrs: { label: "Approve" } });
    after.edges[2] = { ...after.edges[2], attrs: { condition: "outcome=success", weight: "2" } };
    after.graphAttrs = { goal: "New goal" };

    const diff = diffDocs(before, after);
    expect(diff.added).toEqual(["lint"]);
    expect(diff.removed).toEqual(["gate"]);
    expect(diff.changed).toEqual([{ id: "plan", changes: [{ key: "prompt", before: "Plan", after: "Plan carefully" }, { key: "llm_model", before: undefined, after: "claude-sonnet-5" }] }]);
    expect(diff.edgesChanged).toEqual([{ from: "test", to: "review", changes: [{ key: "weight", before: undefined, after: "2" }] }]);
    expect(diff.edgesAdded.map((e) => `${e.from}->${e.to}`)).toEqual(["review->exit"]);
    expect(diff.edgesRemoved.map((e) => `${e.from}->${e.to}`).sort()).toEqual(["gate->exit", "gate->plan", "review->gate"]);
    expect(diff.graph).toEqual([{ key: "goal", before: undefined, after: "New goal" }]);
    expect(describeDiff(diff)).toContain("1 stage changed (plan: prompt, llm_model)");
    expect(diffDocs(before, doc()).total).toBe(0);
    expect(describeDiff(diffDocs(before, doc()))).toBe("identical");
  });

  it("pairs duplicate edges by attributes before order", () => {
    const a = doc();
    a.edges.push({ uid: "x1", from: "fix", to: "test", attrs: { label: "again" } });
    const b = doc();
    b.edges.splice(4, 0, { uid: "x1", from: "fix", to: "test", attrs: { label: "again" } });
    expect(diffDocs(a, b).total).toBe(0);
  });
});

describe("flight list", () => {
  const live = (id: string, status: RunSummary["status"], extra: Partial<RunSummary> = {}): RunSummary => ({ id, name: "flaky", goal: "", status, created_at: at(id.length), pending_questions: 0, ...extra });
  const disk = (id: string, extra: Partial<HistorySummary> = {}): HistorySummary => ({ id, run_id: id.slice(-4), name: "flaky", status: "success", restarts: [], started_at: at(10), ...extra });

  it("joins live runs with their directories and keeps runs the history does not list", () => {
    const flights = mergeFlights(
      [live("aaaa", "running", { current_node: "test", pending_questions: 1, started_at: at(30) }), live("sim1", "success", { simulated: true, simulation_mode: "interactive", started_at: at(20) })],
      [disk("20260723-flaky-aaaa", { live_id: "aaaa", status: "running", started_at: at(30), cost: {} }), disk("20260722-flaky-old1", { started_at: at(5), cost: { ai_units: 3.5 } })],
    );
    expect(flights.map((f) => f.key)).toEqual(["aaaa", "sim1", "dir:20260722-flaky-old1"]);
    expect(flights[0]).toMatchObject({ liveId: "aaaa", dir: "20260723-flaky-aaaa", status: "running", currentNode: "test", pending: 1, cost: null });
    expect(flights[1]).toMatchObject({ liveId: "sim1", dir: null, simulated: true, simulationMode: "interactive" });
    expect(flights[2]).toMatchObject({ liveId: null, dir: "20260722-flaky-old1", cost: { ai_units: 3.5 } });
  });

  it("puts queued runs (no start time yet) first", () => {
    const flights = mergeFlights([live("q", "queued", { created_at: "" })], [disk("d1", { started_at: at(50) })]);
    expect(flights.map((f) => f.key)).toEqual(["q", "dir:d1"]);
  });
});

describe("wind tunnel calibration", () => {
  const telemetry: Telemetry = {
    name: "flaky",
    runs: 10,
    outcomes: { success: 8, fail: 2 },
    cost: { ai_units: { mean: 12, total: 120 } },
    nodes: {
      start: { runs: 10, visits: 10, mean_visits: 1, outcomes: { success: 10 }, retries: 0 },
      plan: { runs: 10, visits: 12, mean_visits: 1.2, outcomes: { success: 12 }, retries: 3, duration_ms: { mean: 900, p50: 800, p95: 2000 }, cost: { ai_units: 2 } },
      test: { runs: 10, visits: 30, mean_visits: 3, outcomes: { success: 10, fail: 20 }, fail_rate: 0.667, retries: 0, duration_ms: { mean: 700, p50: 650, p95: 900 } },
      fix: { runs: 8, visits: 20, mean_visits: 2.5, outcomes: { success: 18, fail: 2 }, retries: 0, cost: { ai_units: 4 } },
      review: { runs: 10, visits: 10, mean_visits: 1, outcomes: { success: 10 }, retries: 0 },
      gate: { runs: 10, visits: 12, mean_visits: 1.2, outcomes: { success: 12 }, retries: 0 },
    },
    edges: [
      { from: "review", to: "gate", count: 9, runs: 9 },
      { from: "review", to: "fix", count: 3, runs: 2 },
      { from: "gate", to: "exit", count: 8, runs: 8 },
      { from: "gate", to: "plan", count: 2, runs: 2 },
    ],
    series: [],
  };

  it("fits each flown stage's outcomes, retries, routes, and duration", () => {
    const settings = calibrate(doc(), telemetry, defaultSettings());
    expect(settings.preset).toBe("calibrated");
    // plan: 12 visits, all succeeded, plus 3 retried attempts → retry share 3 / 15.
    expect(settings.nodes.plan).toEqual({ outcomes: { success: 0.8, retry: 0.2 }, duration_ms: 800 });
    expect(settings.nodes.test.outcomes).toEqual({ success: 0.333, fail: 0.667 });
    expect(settings.nodes.test.duration_ms).toBe(650);
    expect(settings.nodes.review.labels).toEqual({ Approve: 0.75, Rework: 0.25 });
    expect(settings.nodes.gate.choices).toEqual({ "[S] Ship": 0.8, "[R] Replan": 0.2 });
    expect(settings.nodes.start).toBeUndefined();
    // Global dials: agents (plan, fix, review) 40 ok of 42 completed + 3 retries; the tool 10 of 30.
    expect(settings.agentSuccess).toBeCloseTo(40 / 45, 3);
    expect(settings.agentRetry).toBeCloseTo(3 / 45, 3);
    expect(settings.toolSuccess).toBeCloseTo(1 / 3, 3);
    expect(settings.calibration).toMatchObject({ runs: 10, successRate: 0.8, meanCost: 12, costPerVisit: { plan: 2, fix: 4 } });
  });

  it("predicts cost per flight from simulated visits and real cost per visit", () => {
    const settings = calibrate(doc(), telemetry, defaultSettings());
    const report = { runs: 100, nodes: { plan: { visits: 120 }, fix: { visits: 250 } } } as unknown as SimReport;
    expect(predictedCost(report, settings.calibration!)).toEqual({ mean: 1.2 * 2 + 2.5 * 4, covered: 2 });
  });

  it("caps durations at the simulator's hour, skips stages that always run for real, and strips durations for live runs", () => {
    const withRouter: StudioDoc = { ...doc(), nodes: [...doc().nodes, { id: "route", attrs: { shape: "diamond" }, group: null }] };
    const record: Telemetry = {
      ...telemetry,
      nodes: {
        ...telemetry.nodes,
        gate: { ...telemetry.nodes.gate, duration_ms: { mean: 9_000_000, p50: 7_200_000, p95: 20_000_000 } },
        route: { runs: 10, visits: 10, mean_visits: 1, outcomes: { success: 10 }, retries: 0, duration_ms: { mean: 2, p50: 2, p95: 3 } },
      },
    };
    const settings = calibrate(withRouter, record, defaultSettings());
    expect(settings.nodes.gate.duration_ms).toBe(MAX_DURATION_MS);
    expect(settings.nodes.route).toBeUndefined();
    const scenario = withoutDurations(buildScenario(withRouter, settings));
    expect(Object.values(scenario.nodes ?? {}).some((b) => b.duration_ms !== undefined)).toBe(false);
    expect(scenario.nodes?.gate.choices).toEqual({ "[S] Ship": 0.8, "[R] Replan": 0.2 });
    expect(scenario.nodes?.plan).toEqual({ outcomes: { success: 0.8, retry: 0.2 } });
  });
});
