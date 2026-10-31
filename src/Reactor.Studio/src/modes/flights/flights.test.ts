import { describe, expect, it } from "vitest";
import type { HistorySummary, PipelineEventJson, RunSummary, Telemetry, VisitArchive } from "../../api/types";
import { describeDiff, diffDocs } from "../../model/diff";
import { emptyDoc, type StudioDoc } from "../../model/types";
import { MAX_DURATION_MS, buildScenario, calibrate, defaultSettings, predictedCost, withoutDurations, type SimReport } from "../tunnel/scenario";
import { changeCounts, formatBytes, orderedEntries, parseChanges } from "./changes";
import { attemptRows, defaultFile, execsOfVisit, fileLabel, outcomeText, stageMetricTable, visitCostUsd } from "./attempts";
import { cleanLandingRate, dirtyLandingRate, latestChange, metricNames, trajectory } from "./record";
import { checkpointBefore, checkpointNode, digest, failedStagesBehind, overlayAt } from "./replay";
import { mergeFlights, suggestBudget } from "./view";
import { describeForkRefusal, isAbsolutePath, isServerDirectory, planForkWorkdir, samePath, shortPath } from "./workdir";

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

const at = (s: number) => new Date(Date.UTC(2026, 9, 23, 12, 0, s)).toISOString();

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

describe("flight log text", () => {
  it("strips terminal escape sequences from stage logs and failure reasons", () => {
    const red = (s: string) => `\u001b[31m${s}\u001b[39m`;
    const replay = digest([
      { type: "PipelineStarted", timestamp: at(0) },
      { type: "StageStarted", timestamp: at(1), node_id: "test", index: 1, attempt: 1 },
      { type: "StageLog", timestamp: at(2), node_id: "test", message: `→ shell ${red("npm test")}`, level: "info" },
      { type: "StageFailed", timestamp: at(3), node_id: "test", error: `Tool command exited with code 1: ${red("FAIL")} src/a.test.ts`, will_retry: false },
      { type: "PipelineFailed", timestamp: at(4), error: `Stage 'test' failed: ${red("FAIL")}` },
    ]);
    expect(replay.logs[0].text).toBe("→ shell npm test");
    expect(replay.executions[0].error).toBe("Tool command exited with code 1: FAIL src/a.test.ts");
    expect(replay.failureReason).toBe("Stage 'test' failed: FAIL");
  });
});

describe("doc diff", () => {
  it("reports attribute, group, edge, and graph changes", () => {
    const before = doc();
    const after = doc();
    after.nodes[1] = { ...after.nodes[1], attrs: { prompt: "Plan carefully", llm_model: "claude-sonnet-5.5" } };
    after.nodes.push({ id: "lint", attrs: { shape: "parallelogram", tool_command: "ruff" }, group: null });
    after.nodes = after.nodes.filter((n) => n.id !== "gate");
    after.edges = after.edges.filter((e) => e.from !== "gate" && e.to !== "gate");
    after.edges.push({ uid: "e9", from: "review", to: "exit", attrs: { label: "Approve" } });
    after.edges[2] = { ...after.edges[2], attrs: { condition: "outcome=success", weight: "2" } };
    after.graphAttrs = { goal: "New goal" };

    const diff = diffDocs(before, after);
    expect(diff.added).toEqual(["lint"]);
    expect(diff.removed).toEqual(["gate"]);
    expect(diff.changed).toEqual([{ id: "plan", changes: [{ key: "prompt", before: "Plan", after: "Plan carefully" }, { key: "llm_model", before: undefined, after: "claude-sonnet-5.5" }] }]);
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
      [disk("20261023-flaky-aaaa", { live_id: "aaaa", status: "running", started_at: at(30), cost: {} }), disk("20261022-flaky-old1", { started_at: at(5), cost: { ai_units: 3.5 } })],
    );
    expect(flights.map((f) => f.key)).toEqual(["aaaa", "sim1", "dir:20261022-flaky-old1"]);
    expect(flights[0]).toMatchObject({ liveId: "aaaa", dir: "20261023-flaky-aaaa", status: "running", currentNode: "test", pending: 1, cost: null });
    expect(flights[1]).toMatchObject({ liveId: "sim1", dir: null, simulated: true, simulationMode: "interactive" });
    expect(flights[2]).toMatchObject({ liveId: null, dir: "20261022-flaky-old1", cost: { ai_units: 3.5 } });
  });

  it("puts queued runs (no start time yet) first", () => {
    const flights = mergeFlights([live("q", "queued", { created_at: "" })], [disk("d1", { started_at: at(50) })]);
    expect(flights.map((f) => f.key)).toEqual(["q", "dir:d1"]);
  });
});

describe("re-fly working directory", () => {
  const server = "C:\\Code\\AttractorReimpl\\products";
  const atlas = "C:\\Code\\AttractorReimpl\\products\\attractor-atlas";

  it("continues in the flight's own directory, which a locked-down server refuses unless it is the server's", () => {
    const allowed = planForkWorkdir({ source: atlas, server, allowClient: true });
    expect(allowed).toEqual({ directory: atlas, origin: "source_run", blocked: null, needsConfirmation: false, note: null });

    const locked = planForkWorkdir({ source: atlas, server, allowClient: false });
    expect(locked.directory).toBe(atlas);
    expect(locked.blocked).toContain(atlas);
    expect(locked.blocked).toContain(server);
    expect(locked.blocked).toContain("--allow-client-workdir");

    // The server's own directory, however it is spelled, and the Architect's scratch directories under its .reactor.
    expect(planForkWorkdir({ source: "c:/code/attractorreimpl/products/", server, allowClient: false })).toMatchObject({ blocked: null, needsConfirmation: false });
    const scratch = `${server}\\.reactor\\studio\\architect\\20261029-101010-abc123`;
    expect(planForkWorkdir({ source: scratch, server, allowClient: false })).toMatchObject({ directory: scratch, blocked: null });
  });

  it("asks for confirmation before re-flying anywhere else than the flight's directory", () => {
    const toServer = planForkWorkdir({ source: atlas, server, allowClient: false, override: server });
    expect(toServer).toMatchObject({ directory: server, origin: "request", blocked: null, needsConfirmation: true });

    const elsewhere = "D:\\scratch\\atlas-copy";
    expect(planForkWorkdir({ source: atlas, server, allowClient: false, override: elsewhere }).blocked).toContain("--allow-client-workdir");
    expect(planForkWorkdir({ source: atlas, server, allowClient: true, override: elsewhere })).toMatchObject({ directory: elsewhere, blocked: null, needsConfirmation: true });

    // Naming the flight's own directory is no move at all.
    expect(planForkWorkdir({ source: atlas, server, allowClient: true, override: `${atlas.toUpperCase()}\\` })).toMatchObject({ origin: "request", needsConfirmation: false });
    expect(planForkWorkdir({ source: atlas, server, allowClient: true, override: "   " })).toMatchObject({ directory: atlas, origin: "source_run" });
  });

  it("falls back to the server's directory, and says so, for flights that did not record theirs", () => {
    const old = planForkWorkdir({ source: null, server, allowClient: false });
    expect(old).toMatchObject({ directory: server, origin: "server", blocked: null, needsConfirmation: false });
    expect(old.note).toContain("did not record its working directory");
    expect(planForkWorkdir({ source: null, server, allowClient: true, override: "D:\\other" })).toMatchObject({ origin: "request", needsConfirmation: false });
  });

  it("compares Windows paths without case and POSIX paths exactly", () => {
    expect(samePath("C:\\Repo\\", "c:/repo")).toBe(true);
    expect(samePath("/srv/App", "/srv/app")).toBe(false);
    expect(samePath("/srv/app/", "/srv/app")).toBe(true);
    expect(isServerDirectory("C:\\repo\\.reactorx\\a", "C:\\repo")).toBe(false);
    expect(isServerDirectory("C:\\repo\\.reactor", "C:\\repo")).toBe(false);
    expect(isServerDirectory("/srv/app/.reactor/studio/architect/x", "/srv/app/")).toBe(true);
    expect(isServerDirectory("D:\\.reactor\\runs", "D:\\")).toBe(true);
    expect(isAbsolutePath("C:\\x")).toBe(true);
    expect(isAbsolutePath("\\\\share\\x")).toBe(true);
    expect(isAbsolutePath("/x")).toBe(true);
    expect(isAbsolutePath("x\\y")).toBe(false);
  });

  it("shortens long paths to their last directories", () => {
    expect(shortPath("C:\\repo\\")).toBe("C:\\repo");
    expect(shortPath("/home/me/src/products/attractor-atlas", 24)).toBe("…/attractor-atlas");
    expect(shortPath("C:\\Users\\me\\AppData\\work\\products\\attractor-atlas", 34)).toBe("…\\work\\products\\attractor-atlas");
  });

  it("explains the server's refusals in the dialog's words", () => {
    const context = { source: atlas, server, allowClient: false };
    expect(describeForkRefusal({ code: "working_directory_missing", error: "api text", working_directory: atlas.toLowerCase() }, context)).toBe(
      `This flight's directory, ${atlas}, no longer exists. Restore it, or re-fly in the server's directory (${server}).`,
    );
    expect(describeForkRefusal({ code: "working_directory_missing", error: "api text", working_directory: "D:\\x" }, { ...context, allowClient: true })).toBe("D:\\x does not exist.");
    expect(describeForkRefusal({ code: "working_directory_not_allowed", error: "api text", working_directory: atlas }, context)).toContain("--allow-client-workdir");
    expect(describeForkRefusal({ code: "something_else", error: "api text" }, context)).toBe("api text");
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

  it("keeps hours-long durations (up to the simulator's day), skips stages that always run for real, and strips durations for live runs", () => {
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
    expect(settings.nodes.gate.duration_ms).toBe(7_200_000);
    expect(settings.nodes.gate.duration_ms).toBeLessThan(MAX_DURATION_MS);
    expect(settings.nodes.route).toBeUndefined();
    const scenario = withoutDurations(buildScenario(withRouter, settings));
    expect(Object.values(scenario.nodes ?? {}).some((b) => b.duration_ms !== undefined)).toBe(false);
    expect(scenario.nodes?.gate.choices).toEqual({ "[S] Ship": 0.8, "[R] Replan": 0.2 });
    expect(scenario.nodes?.plan).toEqual({ outcomes: { success: 0.8, retry: 0.2 } });
  });
});

describe("workspace changes", () => {
  const changesJson = {
    node: "build",
    mode: "git",
    workspace: "C:/work/app",
    workspace_label: "branches/fork/a",
    before: { id: "aaa", commit: "aaa", tree: "t1" },
    after: { id: "bbb", commit: "bbb", tree: "t2" },
    summary: { added: 1, modified: 1, deleted: 1, total: 3 },
    changes: [
      { path: "src/a.ts", change: "added", size: 2048, blob: "x" },
      { path: "README.md", change: "modified", size: 10, old_size: 8, out_of_scope: true },
      { path: "old.txt", change: "deleted", old_size: 3 },
    ],
    truncated: false,
    concurrent: true,
    concurrent_with: ["b"],
    write_scope: { patterns: ["src/**"], enforced: true, violations: ["README.md"] },
  };

  it("reads changes.json and lists out-of-scope paths first", () => {
    const parsed = parseChanges(JSON.stringify(changesJson))!;
    expect(parsed).toMatchObject({ mode: "git", added: 1, modified: 1, deleted: 1, concurrent: true, concurrentWith: ["b"], after: "bbb", workspaceLabel: "branches/fork/a" });
    expect(parsed.scope).toEqual({ patterns: ["src/**"], enforced: true, violations: ["README.md"], attributed: [] });
    expect(orderedEntries(parsed).map((e) => `${e.change}:${e.path}:${e.outOfScope}`)).toEqual(["modified:README.md:true", "deleted:old.txt:false", "added:src/a.ts:false"]);
    expect(changeCounts(parsed)).toBe("+1 ~1 −1");
    expect(formatBytes(2048)).toBe("2.0 KB");
    expect(parseChanges("{not json")).toBeNull();
    expect(parseChanges({ summary: {} })).toBeNull();
  });

  it("shows what the latest visit changed as a canvas readout, flagged when it left its write_scope", () => {
    const replay = digest([
      { type: "PipelineStarted", timestamp: at(0) },
      { type: "StageStarted", timestamp: at(1), node_id: "build", index: 1, attempt: 1 },
      { type: "StageCompleted", timestamp: at(2), node_id: "build", status: "success" },
      { type: "WorkspaceChanged", timestamp: at(2), node_id: "build", added: 2, modified: 1, deleted: 0, sample: [], out_of_scope: ["README.md"] },
    ]);
    expect(replay.changes).toEqual([{ node: "build", at: Date.parse(at(2)), added: 2, modified: 1, deleted: 0, outOfScope: 1 }]);
    const overlay = overlayAt(replay, { at: null, now: Date.parse(at(3)), allNodes: ["build"] });
    expect(overlay.nodes.build.readouts).toContainEqual({ label: "files", value: "+2 ~1 −0", tone: "fail" });
  });
});

describe("flow-control events in the replay", () => {
  const events: PipelineEventJson[] = [
    { type: "PipelineStarted", timestamp: at(0) },
    { type: "StageStarted", timestamp: at(1), node_id: "polish", index: 1, attempt: 1 },
    { type: "StageCompleted", timestamp: at(2), node_id: "polish", status: "success" },
    { type: "MetricsRecorded", timestamp: at(2), node_id: "polish", metrics: { score: 2, qa_failed: 5, note: "x" } },
    { type: "StageStarted", timestamp: at(3), node_id: "polish", index: 2, attempt: 1 },
    { type: "StageFailed", timestamp: at(4), node_id: "polish", error: "Stage timed out after 10m", will_retry: true },
    { type: "StageStarted", timestamp: at(5), node_id: "polish", index: 2, attempt: 2 },
    { type: "StageCompleted", timestamp: at(6), node_id: "polish", status: "success" },
    { type: "MetricsRecorded", timestamp: at(6), node_id: "polish", metrics: { score: 4 } },
    { type: "VisitLimitReached", timestamp: at(7), node_id: "polish", max_visits: 2, target: "approve" },
    { type: "EdgeSelected", timestamp: at(7), from: "polish", to: "approve", reason: "max_visits", label: "" },
    { type: "StageStarted", timestamp: at(8), node_id: "docs", index: 3, attempt: 1 },
    { type: "StageCompleted", timestamp: at(9), node_id: "docs", status: "fail" },
    { type: "FailedStageContinued", timestamp: at(9), node_id: "docs", to: "build", failure_reason: "\u001b[31mno README\u001b[39m" },
    { type: "BudgetExceeded", timestamp: at(10), spent_usd: 1.25, limit_usd: 1, next_node_id: "build" },
    { type: "PipelineFailed", timestamp: at(10), error: "Budget exceeded" },
  ];

  it("records metrics per visit, visit limits, failures that flowed on, and budget stops", () => {
    const replay = digest(events);
    expect(replay.metrics.map((m) => [m.node, m.visit, m.metrics])).toEqual([
      ["polish", 1, { score: 2, qa_failed: 5 }],
      ["polish", 2, { score: 4 }],
    ]);
    expect(replay.executions.find((x) => x.node === "polish" && x.attempt === 2)?.metrics).toEqual({ score: 4 });
    expect(replay.visitLimits).toEqual([{ node: "polish", max: 2, target: "approve", at: Date.parse(at(7)) }]);
    expect(replay.traversals.at(-1)).toMatchObject({ from: "polish", to: "approve", reason: "max_visits" });
    expect(replay.continued).toEqual([{ node: "docs", to: "build", at: Date.parse(at(9)), reason: "no README" }]);
    expect(replay.executions.find((x) => x.node === "docs")?.continuedTo).toBe("build");
    expect(replay.budget).toEqual({ spent: 1.25, limit: 1, next: "build", at: Date.parse(at(10)) });
    expect(failedStagesBehind(replay)).toEqual(["docs"]);
    // A resume clears the budget stop; a later success makes the failed stage good.
    const resumed = digest([
      ...events,
      { type: "PipelineStarted", timestamp: at(20) },
      { type: "StageStarted", timestamp: at(21), node_id: "docs", index: 4, attempt: 1 },
      { type: "StageCompleted", timestamp: at(22), node_id: "docs", status: "success" },
    ]);
    expect(resumed.budget).toBeNull();
    expect(failedStagesBehind(resumed)).toEqual([]);
  });

  it("puts them on the canvas: metrics, the visit-limit reroute, the failure that flowed on, and the budget stop", () => {
    const overlay = overlayAt(digest(events), { at: null, now: Date.parse(at(30)), allNodes: ["polish", "approve", "docs", "build"] });
    const readouts = (node: string) => overlay.nodes[node].readouts?.map((r) => `${r.label}=${r.value}`);
    expect(readouts("polish")).toEqual(expect.arrayContaining(["score=4", "visit limit=2 → approve"]));
    expect(readouts("docs")).toEqual(expect.arrayContaining(["failed, went on=→ build"]));
    expect(overlay.nodes.build.state).toBe("stopped");
    expect(readouts("build")).toEqual(["budget stop=$1.25 of $1.00"]);
    expect(overlay.edges["polish->approve"].state).toBe("taken");
  });

  it("tabulates a stage's metrics by visit", () => {
    expect(stageMetricTable(digest(events), "polish")).toEqual({ names: ["score", "qa_failed"], visits: [1, 2], values: { score: [2, 4], qa_failed: [5, undefined] } });
    expect(stageMetricTable(digest(events), "docs").names).toEqual([]);
  });

  it("suggests a higher budget to resume with", () => {
    expect(suggestBudget(1.25, 1)).toBe(3);
    expect(suggestBudget(5.5, 5)).toBe(11);
    expect(suggestBudget(0.06, 0.05)).toBe(0.11);
    expect(suggestBudget(0, 0)).toBe(0.01);
  });
});

describe("stage attempts", () => {
  const visit: VisitArchive = {
    visit: 2,
    latest: true,
    outcome: "fail",
    error: "3 tests fail",
    files: ["backend.json", "response.md", "status.json"],
    backend: { cost_usd: 2, model: "gpt-5" },
    attempts: [
      { attempt: 1, outcome: "error", error: "socket closed", files: ["backend.json"], backend: { cost_usd: 0.25 } },
      { attempt: 2, outcome: "timeout", error: null, files: ["backend.json", "prompt.md", "response.partial.md"], backend: { cost_usd: 0.75, model: "gpt-5" } },
    ],
  };

  it("lists retried attempts with how they ended and what they cost, then the visit's last attempt", () => {
    const execs = digest([
      { type: "StageStarted", timestamp: at(1), node_id: "build", index: 1, attempt: 1 },
      { type: "StageFailed", timestamp: at(2), node_id: "build", error: "socket closed", will_retry: true },
      { type: "StageStarted", timestamp: at(3), node_id: "build", index: 1, attempt: 2 },
      { type: "StageFailed", timestamp: at(4), node_id: "build", error: "Stage 'build' timed out after 10m.", will_retry: true },
      { type: "StageStarted", timestamp: at(5), node_id: "build", index: 1, attempt: 3 },
      { type: "StageCompleted", timestamp: at(6), node_id: "build", status: "fail" },
    ]).executions;
    const rows = attemptRows(visit, execs);
    expect(rows.map((r) => [r.attempt, r.number, r.outcome, r.costUsd, r.lastWords, r.final])).toEqual([
      [1, 1, "error", 0.25, false, false],
      [2, 2, "timeout", 0.75, true, false],
      [null, 3, "fail", 2, false, true],
    ]);
    expect(visitCostUsd(rows)).toBe(3);
    expect(outcomeText("timeout")).toBe("timed out");
    // A tool's timed-out attempt leaves no backend.json: the events say how it ended.
    const bare = { ...visit, outcome: null, attempts: [{ attempt: 1, files: ["command.txt"] }, { attempt: 2, files: [] }] };
    expect(attemptRows(bare, execs).map((r) => [r.outcome, r.error])).toEqual([
      ["fail", "socket closed"],
      ["timeout", "Stage 'build' timed out after 10m."],
      ["fail", "3 tests fail"],
    ]);
    expect(attemptRows({ ...visit, outcome: null, attempts: [] }, [{ ...execs[0], status: undefined, end: undefined, error: undefined }])[0].outcome).toBe("running");
    expect(attemptRows(undefined)).toEqual([]);
  });

  it("orders and names stage files, the last words of a stopped attempt included", () => {
    expect(fileLabel("response.partial.md")).toBe("last words");
    expect(defaultFile(["backend.json", "prompt.md", "response.partial.md"])).toBe("response.partial.md");
    expect(defaultFile(["stdout.txt", "response.md"])).toBe("response.md");
    expect(defaultFile([])).toBeNull();
  });

  it("finds the executions of a visit within its run segment", () => {
    const replay = digest([
      { type: "StageStarted", timestamp: at(1), node_id: "polish", index: 1, attempt: 1 },
      { type: "StageCompleted", timestamp: at(2), node_id: "polish", status: "success" },
      { type: "StageStarted", timestamp: at(3), node_id: "polish", index: 2, attempt: 1 },
      { type: "StageFailed", timestamp: at(4), node_id: "polish", error: "timed out", will_retry: true },
      { type: "StageStarted", timestamp: at(5), node_id: "polish", index: 2, attempt: 2 },
    ]);
    const polish = replay.executions.filter((x) => x.node === "polish");
    expect(execsOfVisit(polish, 0, 2).map((x) => x.attempt)).toEqual([1, 2]);
    expect(execsOfVisit(polish, 1, 1)).toEqual([]);
  });
});

describe("record trajectory", () => {
  const t: Telemetry = {
    name: "quality",
    runs: 4,
    outcomes: { success: 2, fail: 2 },
    failed_stage_landings: 1,
    budget_stops: 1,
    metrics: { score: { runs: 3, mean: 4, min: 3, max: 5, last: 3 } },
    nodes: {},
    edges: [],
    series: [
      { id: "a", status: "success", duration_ms: 1000, cost_usd: 1, metrics: { score: 4, qa_failed: 1 } },
      { id: "b", status: "success", duration_ms: 2000, cost_usd: 2, metrics: { score: 5 } },
      { id: "c", status: "fail", duration_ms: 3000, metrics: { score: 3 } },
      { id: "d", status: "fail", duration_ms: 4000, cost_usd: 4 },
    ],
  };

  it("charts each metric's final value per flight next to time and cost", () => {
    const rows = trajectory(t);
    expect(rows.map((r) => [r.key, r.values])).toEqual([
      ["time", [1000, 2000, 3000, 4000]],
      ["cost", [1, 2, undefined, 4]],
      ["metric:score", [4, 5, 3, undefined]],
      ["metric:qa_failed", [1, undefined, undefined, undefined]],
    ]);
    expect(rows[1].format(2)).toBe("$2.00");
    expect(latestChange(rows[2].values)).toEqual({ last: 3, delta: -2 });
    expect(latestChange([undefined])).toEqual({});
    expect(metricNames(t)).toEqual(["score", "qa_failed"]);
  });

  it("separates clean landings from landings with failed stages", () => {
    expect(cleanLandingRate(t)).toBe(0.25);
    expect(dirtyLandingRate(t)).toBe(0.25);
    expect(cleanLandingRate({ ...t, outcomes: {} })).toBe(0);
  });
});
