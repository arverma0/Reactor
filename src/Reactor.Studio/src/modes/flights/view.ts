import type { ForkOrigin, HistoryDetail, HistoryStage, HistorySummary, PendingQuestion, RunCost, RunDetail, RunSummary } from "../../api/types";

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
  checkpoints: string[];
  stages: HistoryStage[];
}

export function flightView(key: string, flight: Flight | undefined, live: RunDetail | undefined, history: HistoryDetail | undefined): FlightView | null {
  if (!flight && !live && !history) return null;
  const summary = history?.summary;
  const status = live?.status ?? flight?.status ?? summary?.status ?? "unknown";
  const dir = flight?.dir ?? summary?.id ?? null;
  const logs = live?.logs_root?.split(/[\\/]/).filter(Boolean) ?? [];
  const failure = live?.error || live?.result?.failure_reason || history?.result?.failure_reason || flight?.failure || summary?.failure_reason || null;
  return {
    key,
    liveId: flight?.liveId ?? live?.id ?? null,
    dir,
    name: live?.name ?? flight?.name ?? summary?.name ?? "",
    goal: live?.goal || flight?.goal || summary?.goal || "",
    status,
    active: status === "running" || status === "queued",
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
    checkpoints: history?.checkpoints ?? [],
    stages: history?.stages ?? [],
  };
}

/** Short cost text for lists: AI units, else dollars, else tokens. */
export function costText(cost: RunCost | null | undefined): string | null {
  if (!cost) return null;
  if (cost.ai_units !== undefined) return `${cost.ai_units.toFixed(cost.ai_units < 10 ? 1 : 0)} AIU`;
  if (cost.cost_usd !== undefined) return `$${cost.cost_usd.toFixed(2)}`;
  if (cost.premium_requests !== undefined) return `${cost.premium_requests} req`;
  const tokens = (cost.input_tokens ?? 0) + (cost.output_tokens ?? 0);
  return tokens > 0 ? `${tokens >= 10000 ? `${Math.round(tokens / 1000)}k` : tokens.toLocaleString()} tok` : null;
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
  restarts: number;
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
      restarts: h.restarts?.length ?? 0,
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
      restarts: 0,
    });
  }
  return flights.sort((a, b) => sortTime(b) - sortTime(a));
}
