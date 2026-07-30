import { describe, expect, it } from "vitest";
import type { Telemetry } from "../../api/types";
import { emptyDoc, type StudioDoc } from "../../model/types";
import { forecastSide } from "../architect/forecast";
import {
  DURATION_PRIORS,
  MAX_DURATION_MS,
  PRESETS,
  PRESET_CATALOG,
  buildScenario,
  calibrate,
  defaultSettings,
  failedStageLandings,
  formatMetricSpec,
  formatUsd,
  landing,
  parseMetricSpec,
  withoutDurations,
  type SimReport,
  type TunnelSettings,
} from "./scenario";

const doc = (): StudioDoc => ({
  ...emptyDoc("review"),
  nodes: [
    { id: "start", attrs: { shape: "Mdiamond" }, group: null },
    { id: "write", attrs: { prompt: "Write" }, group: null },
    { id: "review", attrs: { prompt: "Review" }, group: null },
    { id: "test", attrs: { shape: "parallelogram", tool_command: "pytest" }, group: null },
    { id: "exit", attrs: { shape: "Msquare" }, group: null },
  ],
  edges: [
    { uid: "e0", from: "start", to: "write", attrs: {} },
    { uid: "e1", from: "write", to: "review", attrs: {} },
    { uid: "e2", from: "review", to: "test", attrs: { label: "Approve" } },
    { uid: "e3", from: "review", to: "write", attrs: { label: "Rework" } },
    { uid: "e4", from: "test", to: "exit", attrs: { condition: "outcome=success" } },
  ],
});

const report = (patch: Partial<SimReport> = {}): SimReport => ({
  runs: 1000,
  seed: 1,
  duration_ms: 500,
  max_steps: 200,
  outcomes: { success: 950, fail: 50 },
  success_rate: 0.95,
  success_ci: [0.935, 0.962],
  clean_success_rate: 0.9,
  clean_success_ci: [0.88, 0.917],
  failed_stage_landings: 50,
  steps: { min: 4, mean: 6.2, max: 30, bucket_size: 1, histogram: [] },
  simulated_duration_ms: { mean: 18_000_000, p50: 16_000_000, p90: 27_360_000, max: 40_000_000 },
  simulated_cost_usd: { mean: 12.5, p50: 11, p90: 19.25, max: 40 },
  nodes: {
    write: { visits: 1300, runs: 1000, mean_visits: 1.3, max_visits: 5, outcomes: { success: 1250, fail: 50 }, retries: 0, failures_continued: 50, failed_stage_landings: 35 },
    review: { visits: 1300, runs: 1000, mean_visits: 1.3, max_visits: 5, outcomes: { success: 1300 }, retries: 0, failed_stage_landings: 0 },
    test: { visits: 1000, runs: 1000, mean_visits: 1, max_visits: 1, outcomes: { success: 950, fail: 50 }, retries: 0, failures_continued: 20, failed_stage_landings: 20 },
  },
  edges: [],
  failures: [{ kind: "fail", reason: "[Simulated] failure at test", count: 50, sample: { seed: 1, run_index: 7 }, last_node: "test", nodes: { test: 50 } }],
  samples: { first_failure: null, longest: { seed: 1, run_index: 2 }, typical: { seed: 1, run_index: 3 } },
  warnings: [],
  ...patch,
});

describe("wind tunnel presets", () => {
  it("come from the server's catalog, and the Studio builds the same scenarios from them", () => {
    expect(Object.keys(PRESETS)).toEqual(["happy", "realistic", "stress", "chaos"]);
    expect(PRESETS.realistic).toEqual({ label: "Realistic", blurb: expect.any(String), agentSuccess: 0.9, agentRetry: 0.03, toolSuccess: 0.75 });
    expect(DURATION_PRIORS.codergen).toBe(600_000);
    for (const preset of PRESET_CATALOG.presets) {
      const settings: TunnelSettings = { ...defaultSettings(), agentSuccess: preset.agent_success, agentRetry: preset.agent_retry, toolSuccess: preset.tool_success };
      const built = buildScenario(emptyDoc("x"), settings);
      expect(built.defaults).toEqual(preset.scenario.defaults);
      expect(built.duration_priors).toEqual(preset.scenario.duration_priors);
    }
  });

  it("choose labeled routes evenly and send typical durations unless cleared", () => {
    const scenario = buildScenario(doc(), defaultSettings());
    expect(scenario.nodes?.review?.labels).toEqual({ Approve: 0.5, Rework: 0.5 });
    expect(scenario.duration_priors).toEqual(DURATION_PRIORS);
    expect(buildScenario(doc(), { ...defaultSettings(), durationPriors: {} }).duration_priors).toBeUndefined();
    expect(buildScenario(doc(), { ...defaultSettings(), durationPriors: { codergen: 1_200_000 } }).duration_priors).toEqual({ codergen: 1_200_000 });

    const managed = withoutDurations(buildScenario(doc(), { ...defaultSettings(), nodes: { write: { duration_ms: 5000, cost: 0.3 } } }));
    expect(managed.duration_priors).toBeUndefined();
    expect(managed.nodes?.write).toEqual({ cost: 0.3 });
  });
});

describe("outcome metric syntax", () => {
  it.each([
    ["4", 4],
    ["1..5", { min: 1, max: 5 }],
    ["0.0..1.0", { min: 0, max: 1, integer: false }],
    ["0.5 .. 0.9", { min: 0.5, max: 0.9 }],
    ["3, 4, 5", { values: [3, 4, 5] }],
    ["3:0.2, 4:0.5, 5:0.3", { values: { "3": 0.2, "4": 0.5, "5": 0.3 } }],
    ["success: 0; fail: 1..5", { success: 0, fail: { min: 1, max: 5 } }],
    ["*: 2", { "*": 2 }],
  ])("parses %s", (text, spec) => {
    expect(parseMetricSpec(text)).toEqual(spec);
  });

  it.each(["", "lots", "5..1", "3:-1", "3:0, 4:0", "maybe: 1", "success: x"])("rejects %j", (text) => {
    expect(parseMetricSpec(text)).toBeNull();
  });

  it("formats specs back to the same text", () => {
    for (const text of ["4", "1..5", "0.0..1.0", "3, 4, 5", "3:0.2, 4:0.5, 5:0.3", "success: 0; fail: 1..5"]) {
      expect(formatMetricSpec(parseMetricSpec(text)!)).toBe(text);
    }
  });
});

describe("calibration", () => {
  it("carries real per-execution cost (USD), durations up to a day, and the record's means into the scenario", () => {
    const t: Telemetry = {
      name: "review",
      runs: 4,
      outcomes: { success: 3, fail: 1 },
      duration_ms: { mean: 7_200_000, p50: 7_000_000, p95: 9_000_000 },
      cost: { ai_units: { mean: 40, total: 160 }, cost_usd: { mean: 6.5, total: 26 } },
      nodes: {
        write: { runs: 4, visits: 6, mean_visits: 1.5, outcomes: { success: 6 }, retries: 0, duration_ms: { mean: 5_400_000, p50: 5_400_000, p95: 6_000_000 }, cost: { ai_units: 20, cost_usd: 2.12346 } },
        test: { runs: 4, visits: 4, mean_visits: 1, outcomes: { success: 3, fail: 1 }, retries: 0, duration_ms: { mean: 90_000_000, p50: 90_000_000, p95: 90_000_000 } },
      },
      edges: [],
      series: [],
    };

    const settings = calibrate(doc(), t, defaultSettings());

    expect(settings.nodes.write).toMatchObject({ duration_ms: 5_400_000, cost: 2.1235 });
    expect(settings.nodes.test.duration_ms).toBe(MAX_DURATION_MS);
    expect(settings.nodes.test.cost).toBeUndefined();
    expect(settings.calibration).toMatchObject({ meanCost: 40, meanCostUsd: 6.5, meanDurationMs: 7_200_000, costPerVisit: { write: 20 } });
    expect(settings.durationPriors).toEqual(DURATION_PRIORS);
  });
});

describe("readiness", () => {
  it("judges on clean landings and lists the stages behind dirty ones", () => {
    expect(landing(report())).toEqual({ clean: 0.9, ci: [0.88, 0.917], landed: 950, dirty: 50, dirtyShare: 0.05, down: 50 });
    expect(failedStageLandings(report())).toEqual([
      { node: "write", flights: 35 },
      { node: "test", flights: 20 },
    ]);
    // Servers that predate clean landings: fall back to all landings.
    const legacy = report({ clean_success_rate: undefined, clean_success_ci: undefined, failed_stage_landings: undefined });
    expect(landing(legacy)).toMatchObject({ clean: 0.95, ci: [0.935, 0.962], dirty: 0 });
  });

  it("forecasts on the clean rate with time and cost", () => {
    expect(forecastSide(report(), 12)).toEqual({
      rate: 0.9,
      ci: [0.88, 0.917],
      dirty: 0.05,
      cost: 12,
      costUsd: { mean: 12.5, p90: 19.25 },
      durationMs: { mean: 18_000_000, p90: 27_360_000 },
      steps: 6.2,
      stepLimit: 0,
    });
    const untimed = forecastSide(report({ simulated_duration_ms: { mean: 0, p50: 0, p90: 0, max: 0 }, simulated_cost_usd: { mean: 0, p50: 0, p90: 0, max: 0 } }));
    expect(untimed.durationMs).toBeUndefined();
    expect(untimed.costUsd).toBeUndefined();
  });

  it("formats dollars for people", () => {
    expect([formatUsd(0), formatUsd(0.0423), formatUsd(12.5), formatUsd(4321.4)]).toEqual(["$0", "$0.042", "$12.50", "$4,321"]);
  });
});
