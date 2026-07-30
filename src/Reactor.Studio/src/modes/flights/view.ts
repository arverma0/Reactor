import type { BudgetStopInfo, ForkOrigin, HistoryDetail, HistoryStage, HistorySummary, PendingQuestion, RunCost, RunDetail, RunSummary } from "../../api/types";
import { stripAnsi } from "../../model/ansi";

/** Everything the Flights panels show about one flight, from the live run (if the server tracks it) and its directory. */
export interface FlightView {
  key: string;
  liveId: string | null;
  dir: string | null;
  name: string;
  goal: string;
  status: string;
  active: boolean;
  startedAt: string | null;
  durationMs: number | null;
  currentNode: string | null;
  location: string;
  pending: PendingQuestion[];
  failure: string | null;
  simulated: boolean;
  simulationMode?: string;
  forkedFrom: ForkOrigin | null;
  cost: RunCost | null;
  /** The flight's working directory (its manifest's, else the live run's); null when the flight did not record one. */
  workingDirectory: string | null;
  checkpoints: string[];
  stages: HistoryStage[];
  /** The run's latest stop was its cost budget (from the history; the event stream has it too). */
  budget: BudgetStopInfo | null;
  /** Stages whose failure flowed onward and never succeeded afterwards. */
  failedStages: string[];
  /** Final values of the outcome metrics. */
  metrics: Record<string, number>;
}

export function flightView(key: string, flight: Flight | undefined, live: RunDetail | undefined, history: HistoryDetail | undefined): FlightView | null {
  if (!flight && !live && !history) return null;
  const summary = history?.summary;
  const status = live?.status ?? flight?.status ?? summary?.status ?? "unknown";
  const dir = flight?.dir ?? summary?.id ?? null;
  const logs = live?.logs_root?.split(/[\\/]/).filter(Boolean) ?? [];
  // Failure reasons quote tool output, which may carry terminal escape sequences.
  const failure = stripAnsi(live?.error || live?.result?.failure_reason || history?.result?.failure_reason || flight?.failure || summary?.failure_reason) || null;
  const active = status === "running" || status === "queued";
  return {
    key,
    liveId: flight?.liveId ?? live?.id ?? null,
    dir,
    name: live?.name ?? flight?.name ?? summary?.name ?? "",
    goal: live?.goal || flight?.goal || summary?.goal || "",
    status,
    active,
    startedAt: live?.started_at ?? flight?.startedAt ?? summary?.started_at ?? null,
    durationMs: live?.duration_seconds != null ? live.duration_seconds * 1000 : (summary?.duration_ms ?? flight?.durationMs ?? null),
    currentNode: live?.current_node ?? flight?.currentNode ?? null,
    location: logs.length > 0 ? logs.slice(-2).join("/") : dir ? `runs/${dir}` : "",
    pending: live?.pending_questions ?? [],
    failure: status === "success" ? null : failure,
    simulated: !!(live?.simulated || flight?.simulated || summary?.simulated),
    simulationMode: live?.simulation_mode ?? flight?.simulationMode,
    forkedFrom: summary?.forked_from ?? flight?.forkedFrom ?? null,
    cost: summary?.cost && Object.keys(summary.cost).length > 0 ? summary.cost : (flight?.cost ?? null),
    workingDirectory: summary?.working_directory ?? live?.working_directory ?? flight?.workingDirectory ?? null,
    checkpoints: history?.checkpoints ?? [],
    stages: history?.stages ?? [],
    budget: active ? null : (summary?.budget ?? flight?.budget ?? null),
    failedStages: summary?.failed_stages ?? flight?.failedStages ?? NONE,
    metrics: summary?.metrics ?? flight?.metrics ?? NO_METRICS,
  };
}

const NONE: string[] = [];
const NO_METRICS: Record<string, number> = {};

/** Short cost text for lists: dollars (comparable across backends), else AI units, premium requests, or tokens. */
export { costText } from "../../model/cost";

/**
 * A budget to suggest when resuming a run that stopped at its budget: twice the old one, or what was spent plus the old
 * one if that is more — rounded up to whole dollars (to cents under a dollar).
 */
export function suggestBudget(spent: number, limit: number): number {
  const base = Math.max(limit * 2, spent + limit, spent * 1.25, 0.01);
  return base < 1 ? Math.ceil(Number((base * 100).toFixed(6))) / 100 : Math.ceil(Number(base.toFixed(6)));
}

/**
 * One row of the flight list: a run the server is tracking (`liveId`, from `/pipelines`), a run directory on disk
 * (`dir`, from the history API), or both. `key` is the live id while the server knows the run, else `dir:<directory>`.
 */
export interface Flight {
  key: string;
  liveId: string | null;
  dir: string | null;
  name: string;
  goal: string;
  status: string;
  createdAt: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  durationMs: number | null;
  currentNode: string | null;
  pending: number;
  simulated: boolean;
  simulationMode?: string;
  forkedFrom: ForkOrigin | null;
  cost: RunCost | null;
  stageExecutions: number | null;
  failure: string | null;
  sourcePath: string | null;
  workingDirectory: string | null;
  restarts: number;
  /** The run's latest stop was its cost budget. */
  budget: BudgetStopInfo | null;
  /** Stages whose failure flowed onward and never succeeded afterwards (a landing with failed stages behind it). */
  failedStages: string[];
  metrics: Record<string, number>;
}

const sortTime = (f: Flight) => Date.parse(f.startedAt ?? f.createdAt ?? "") || Number.MAX_SAFE_INTEGER;

export function mergeFlights(live: RunSummary[], history: HistorySummary[]): Flight[] {
  const liveById = new Map(live.map((r) => [r.id, r]));
  const claimed = new Set<string>();
  const flights: Flight[] = [];
  for (const h of history) {
    const l = h.live_id ? liveById.get(h.live_id) : undefined;
    if (h.live_id) claimed.add(h.live_id);
    flights.push({
      key: h.live_id ?? `dir:${h.id}`,
      liveId: h.live_id ?? null,
      dir: h.id,
      name: h.name || l?.name || h.id,
      goal: h.goal || l?.goal || "",
      status: l?.status ?? h.status,
      createdAt: l?.created_at ?? h.started_at ?? null,
      startedAt: h.started_at ?? l?.started_at ?? null,
      finishedAt: h.finished_at ?? l?.finished_at ?? null,
      durationMs: h.duration_ms ?? null,
      currentNode: l?.current_node ?? null,
      pending: l?.pending_questions ?? 0,
      simulated: !!(h.simulated || l?.simulated),
      simulationMode: l?.simulation_mode,
      forkedFrom: h.forked_from ?? null,
      cost: h.cost && Object.keys(h.cost).length > 0 ? h.cost : null,
      stageExecutions: h.stage_executions ?? null,
      failure: h.failure_reason ?? null,
      sourcePath: h.source_path ?? null,
      workingDirectory: h.working_directory ?? l?.working_directory ?? null,
      restarts: h.restarts?.length ?? 0,
      budget: l && (l.status === "running" || l.status === "queued") ? null : (h.budget ?? null),
      failedStages: h.failed_stages ?? NONE,
      metrics: h.metrics ?? NO_METRICS,
    });
  }
  for (const l of live) {
    if (claimed.has(l.id)) continue;
    flights.push({
      key: l.id,
      liveId: l.id,
      dir: null,
      name: l.name,
      goal: l.goal,
      status: l.status,
      createdAt: l.created_at,
      startedAt: l.started_at ?? null,
      finishedAt: l.finished_at ?? null,
      durationMs: l.started_at && l.finished_at ? Date.parse(l.finished_at) - Date.parse(l.started_at) : null,
      currentNode: l.current_node ?? null,
      pending: l.pending_questions ?? 0,
      simulated: !!l.simulated,
      simulationMode: l.simulation_mode,
      forkedFrom: null,
      cost: null,
      stageExecutions: null,
      failure: null,
      sourcePath: null,
      workingDirectory: l.working_directory ?? null,
      restarts: 0,
      budget: null,
      failedStages: NONE,
      metrics: NO_METRICS,
    });
  }
  return flights.sort((a, b) => sortTime(b) - sortTime(a));
}
