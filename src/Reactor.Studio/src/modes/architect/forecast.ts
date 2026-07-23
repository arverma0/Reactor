import { api } from "../../api/client";
import type { Telemetry } from "../../api/types";
import { writeDot } from "../../model/dot";
import type { StudioDoc } from "../../model/types";
import { buildScenario, calibrate, defaultSettings, predictedCost, type SimReport } from "../tunnel/scenario";

export interface ForecastSide {
  rate: number;
  ci: [number, number];
  /** Predicted AI units per flight (calibrated forecasts with real cost data only). */
  cost?: number;
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

async function fly(doc: StudioDoc, record: Telemetry | null): Promise<ForecastSide> {
  const settings = record ? calibrate(doc, record, defaultSettings()) : defaultSettings();
  const report = await api.post<SimReport>("/studio/api/simulate", { dot: writeDot(doc), scenario: buildScenario(doc, settings), runs: RUNS, max_steps: 300, seed: SEED });
  return {
    rate: report.success_rate,
    ci: report.success_ci,
    cost: settings.calibration ? predictedCost(report, settings.calibration)?.mean : undefined,
    steps: report.steps.mean,
    stepLimit: (report.outcomes.step_limit ?? 0) / report.runs,
  };
}

/**
 * Flies the current design and the proposal through the wind tunnel under the same scenario and the same seed (common
 * random numbers, so the difference is the design, not the dice): calibrated to the pipeline's real flights when it has
 * a record, else the realistic preset.
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
