import type { VisitArchive } from "../../api/types";
import type { RunReplay, StageExecution } from "./replay";

/** One attempt of a stage visit, for the stage panel's attempt browser. */
export interface AttemptRow {
  /** The archived attempt (`attempts/attempt-N`), or null for the visit's last attempt (the visit's own files). */
  attempt: number | null;
  /** 1-based position in the visit. */
  number: number;
  /** How it ended: `timeout`, `error`, `cancelled` (from backend.json), else the status (`fail`, `retry`, `success`, …). */
  outcome: string | null;
  error: string | null;
  costUsd?: number;
  model?: string;
  files: string[];
  /** It left the agent's last message of an attempt that was stopped (`response.partial.md`). */
  lastWords: boolean;
  final: boolean;
}

const num = (value: unknown) => (typeof value === "number" && Number.isFinite(value) ? value : undefined);
const str = (value: unknown) => (typeof value === "string" && value ? value : undefined);

/** How an attempt the engine retried ended, from its events: timed out, failed (or errored), or asked for a retry. */
function endingOf(exec: StageExecution | undefined): string | null {
  if (!exec) return null;
  if (exec.error && /timed out|timeout/i.test(exec.error)) return "timeout";
  if (exec.error) return "fail";
  return exec.status === "retry" ? "retry" : (exec.status ?? null);
}

/**
 * The attempts of a visit, oldest first: the attempts the engine retried (archived with their own prompt, transcript,
 * usage, and last words), then the visit's last attempt. `execs` are the visit's executions from the event stream; they
 * fill in how an attempt ended when its files do not say (a tool's timed-out attempt has no backend.json) and the last
 * attempt's status while the stage has not written one.
 */
export function attemptRows(visit: VisitArchive | undefined, execs: StageExecution[] = []): AttemptRow[] {
  if (!visit) return [];
  const rows: AttemptRow[] = visit.attempts.map((a, i) => {
    const exec = execs.find((x) => x.attempt === a.attempt);
    return {
      attempt: a.attempt,
      number: i + 1,
      outcome: a.outcome ?? str(a.backend?.outcome) ?? endingOf(exec),
      error: a.error ?? str(a.backend?.error) ?? exec?.error ?? null,
      costUsd: num(a.backend?.cost_usd),
      model: str(a.backend?.model),
      files: a.files,
      lastWords: a.files.includes("response.partial.md"),
      final: false,
    };
  });
  const last = execs.at(-1);
  rows.push({
    attempt: null,
    number: rows.length + 1,
    outcome: visit.outcome ?? last?.status ?? (last && last.end === undefined ? "running" : null),
    error: visit.error ?? null,
    costUsd: num(visit.backend?.cost_usd),
    model: str(visit.backend?.model),
    files: visit.files,
    lastWords: visit.files.includes("response.partial.md"),
    final: true,
  });
  return rows;
}

/** What a visit cost over all its attempts (USD), or undefined when no attempt reported a cost. */
export function visitCostUsd(rows: AttemptRow[]): number | undefined {
  const costs = rows.map((r) => r.costUsd).filter((c): c is number => c !== undefined);
  return costs.length > 0 ? costs.reduce((a, b) => a + b, 0) : undefined;
}

const OUTCOME_TEXT: Record<string, string> = {
  timeout: "timed out",
  error: "errored",
  cancelled: "cancelled",
  fail: "failed",
  retry: "asked for a retry",
  success: "succeeded",
  partial_success: "partly succeeded",
  skipped: "skipped",
};

/** How an attempt ended, for people. */
export const outcomeText = (outcome: string | null | undefined) => (outcome ? (OUTCOME_TEXT[outcome] ?? outcome.replace(/_/g, " ")) : "");

/** Tab captions for stage files: the agent's last words of a stopped attempt are not a file name people know. */
export const fileLabel = (name: string) => (name === "response.partial.md" ? "last words" : name);

/** The file to open first: the response (or the last words of a stopped attempt), else a tool's output. */
export function defaultFile(names: string[]): string | null {
  return names.find((f) => f === "response.md") ?? names.find((f) => f === "response.partial.md") ?? names.find((f) => f === "stdout.txt") ?? names[0] ?? null;
}

/** The executions (attempts) of one visit of a stage in a run segment, in order. */
export function execsOfVisit(execs: StageExecution[], segment: number, visit: number): StageExecution[] {
  let n = 0;
  const out: StageExecution[] = [];
  for (const x of execs) {
    if (x.segment !== segment) continue;
    if (x.attempt === 1) n++;
    if (n === visit) out.push(x);
  }
  return out;
}

/** A stage's metrics by visit: the metric names (in order of first report), the visits that reported any, and each value. */
export interface StageMetricTable {
  names: string[];
  visits: number[];
  values: Record<string, (number | undefined)[]>;
}

export function stageMetricTable(replay: RunReplay, node: string): StageMetricTable {
  const records = replay.metrics.filter((m) => m.node === node);
  const visits = [...new Set(records.map((r) => r.visit))];
  const names: string[] = [];
  for (const r of records) for (const name of Object.keys(r.metrics)) if (!names.includes(name)) names.push(name);
  const values: Record<string, (number | undefined)[]> = {};
  for (const name of names) {
    values[name] = visits.map((v) => {
      const latest = records.filter((r) => r.visit === v && r.metrics[name] !== undefined).at(-1);
      return latest?.metrics[name];
    });
  }
  return { names, visits, values };
}
