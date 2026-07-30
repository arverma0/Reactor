import type { Telemetry, TelemetryPoint } from "../../api/types";
import { formatDuration } from "../../canvas/Elapsed";
import { formatAiUnits, formatUsd } from "../../model/cost";
import { metricText } from "./replay";

const sum = (values: number[]) => values.reduce((a, b) => a + b, 0);

/** Share of finished flights that landed clean: reached the exit with no failed stage behind them. */
export function cleanLandingRate(t: Telemetry): number {
  const finished = sum(Object.values(t.outcomes));
  if (!finished) return 0;
  const landed = (t.outcomes.success ?? 0) + (t.outcomes.partial_success ?? 0);
  return Math.max(0, landed - (t.failed_stage_landings ?? 0)) / finished;
}

/** Share of finished flights that landed with failed stages behind them (a failure flowed on and was never made good). */
export function dirtyLandingRate(t: Telemetry): number {
  const finished = sum(Object.values(t.outcomes));
  return finished ? (t.failed_stage_landings ?? 0) / finished : 0;
}

/** One row of the record's trajectory chart: a value per flight (oldest → newest), undefined where a flight has none. */
export interface TrajectoryRow {
  key: string;
  label: string;
  kind: "time" | "cost" | "metric";
  values: (number | undefined)[];
  format: (n: number) => string;
}

/** Outcome metric names over a record: the run-level ones first, then any a flight reported. */
export function metricNames(t: Telemetry): string[] {
  const names = Object.keys(t.metrics ?? {});
  for (const point of t.series) for (const name of Object.keys(point.metrics ?? {})) if (!names.includes(name)) names.push(name);
  return names;
}

const costOf = (p: TelemetryPoint) => p.cost_usd ?? undefined;

/**
 * The record's trajectory, flight by flight: duration, cost (dollars, else AI units), and each outcome metric's final
 * value — the quality trend next to what it took to get it.
 */
export function trajectory(t: Telemetry): TrajectoryRow[] {
  const rows: TrajectoryRow[] = [];
  const durations = t.series.map((p) => p.duration_ms);
  if (durations.some((d) => d !== undefined)) rows.push({ key: "time", label: "time", kind: "time", values: durations, format: (n) => formatDuration(n) });
  const usd = t.series.map(costOf);
  if (usd.some((c) => c !== undefined)) rows.push({ key: "cost", label: "cost", kind: "cost", values: usd, format: formatUsd });
  else {
    const units = t.series.map((p) => p.ai_units);
    if (units.some((c) => c !== undefined)) rows.push({ key: "cost", label: "AI units", kind: "cost", values: units, format: formatAiUnits });
  }
  for (const name of metricNames(t)) rows.push({ key: `metric:${name}`, label: name, kind: "metric", values: t.series.map((p) => p.metrics?.[name]), format: metricText });
  return rows;
}

/** The newest defined value of a row, and the change from the value before it (for "4 ↑1"). */
export function latestChange(values: (number | undefined)[]): { last?: number; delta?: number } {
  const known = values.filter((v): v is number => v !== undefined);
  if (known.length === 0) return {};
  return { last: known[known.length - 1], delta: known.length > 1 ? known[known.length - 1] - known[known.length - 2] : undefined };
}
