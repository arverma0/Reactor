import { api } from "../../api/client";
import type { Telemetry } from "../../api/types";
import { writeDot } from "../../model/dot";
import type { StudioDoc } from "../../model/types";
import { buildScenario, calibrate, defaultSettings, landing, predictedCost, type SimReport } from "../tunnel/scenario";

export interface ForecastSide {
  /** Clean landing rate: flights that reach the exit with no failed stage behind them. */
  rate: number;
  ci: [number, number];
  /** Share of flights that land with failed stages behind them (their failure flowed on). */
  dirty: number;
  /** Predicted AI units per flight (calibrated forecasts with real cost data only). */
  cost?: number;
  /** Simulated USD per flight (stages with a cost: calibrated from the record's cost_usd). */
  costUsd?: { mean: number; p90: number };
  /** Simulated time per flight (ms): stage durations from the record, else typical durations by stage kind. */
  durationMs?: { mean: number; p90: number };
  steps: number;
  stepLimit: number;
}

export interface ForecastCompare {
  basis: "calibrated" | "realistic";
  runs: number;
  /** Real flights the calibration used. */
  flights?: number;
  current: ForecastSide | null;
  /** Why the current design could not be flown (it has errors, for example). */
  currentError?: string;
  proposal: ForecastSide;
}

const RUNS = 1000;
const SEED = 4242;

/** The forecast numbers of one wind tunnel report. */
export function forecastSide(report: SimReport, aiUnits?: number): ForecastSide {
  const ready = landing(report);
  const time = report.simulated_duration_ms;
  const usd = report.simulated_cost_usd;
  return {
    rate: ready.clean,
    ci: ready.ci,
    dirty: ready.dirtyShare,
    cost: aiUnits,
    costUsd: usd && usd.max > 0 ? { mean: usd.mean, p90: usd.p90 } : undefined,
    durationMs: time && time.max > 0 ? { mean: time.mean, p90: time.p90 } : undefined,
    steps: report.steps.mean,
    stepLimit: (report.outcomes.step_limit ?? 0) / report.runs,
  };
}

async function fly(doc: StudioDoc, record: Telemetry | null): Promise<ForecastSide> {
  const settings = record ? calibrate(doc, record, defaultSettings()) : defaultSettings();
  const report = await api.post<SimReport>("/studio/api/simulate", { dot: writeDot(doc), scenario: buildScenario(doc, settings), runs: RUNS, max_steps: 300, seed: SEED });
  return forecastSide(report, settings.calibration ? predictedCost(report, settings.calibration)?.mean : undefined);
}

/**
 * Flies the current design and the proposal through the wind tunnel under the same scenario and the same seed (common
 * random numbers, so the difference is the design, not the dice): calibrated to the pipeline's real flights when it has
 * a record, else the realistic preset (with typical stage durations, so the forecast includes time).
 */
export async function compareForecast(current: StudioDoc | null, proposal: StudioDoc, record: Telemetry | null): Promise<ForecastCompare> {
  let currentError: string | undefined;
  const [now, next] = await Promise.all([
    current
      ? fly(current, record).catch((error: Error) => {
          currentError = error.message;
          return null;
        })
      : Promise.resolve(null),
    fly(proposal, record),
  ]);
  return { basis: record ? "calibrated" : "realistic", runs: RUNS, flights: record?.runs, current: now, currentError, proposal: next };
}
