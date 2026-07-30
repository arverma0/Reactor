import type { PendingQuestion, PipelineEventJson, QuestionOption } from "../../api/types";
import { edgeKey, type CanvasOverlay, type EdgeOverlay, type NodeOverlay, type NodeState } from "../../canvas/build";
import { formatDuration } from "../../canvas/Elapsed";
import { stripAnsi } from "../../model/ansi";
import { formatUsd } from "../../model/cost";

export interface StageExecution {
  node: string;
  attempt: number;
  index: number;
  /** Loop-restart segment (0 = the run's own directory, n = `<run>-restart-n`). */
  segment: number;
  /**
   * The top-level stage this one ran inside (a parallel node, for its branch stages); undefined for top-level stages.
   * Only top-level stages get checkpoint snapshots.
   */
  parent?: string;
  /** Execution index of {@link parent}. */
  parentIndex?: number;
  start: number;
  end?: number;
  status?: string;
  notes?: string;
  error?: string;
  /** The execution failed and the run went on along an unconditional edge to this stage anyway (`FailedStageContinued`). */
  continuedTo?: string;
  /** Outcome metrics this execution reported (`MetricsRecorded`). */
  metrics?: Record<string, number>;
}

/** A failure that flowed onward: nothing routed it, so an unconditional edge took the run on as if the stage had succeeded. */
export interface ContinuedFailure {
  node: string;
  to: string;
  at: number;
  reason?: string;
}

/** Outcome metrics one visit of a stage reported; `visit` counts the stage's visits across the run (from 1). */
export interface MetricRecord {
  node: string;
  at: number;
  visit: number;
  metrics: Record<string, number>;
}

/** A stage's `max_visits` was used up and the run went to `target` instead (or failed, without one). */
export interface VisitLimitRecord {
  node: string;
  max: number;
  target: string | null;
  at: number;
}

/** The run stopped before `next` because its recorded cost reached its budget (`BudgetExceeded`). */
export interface BudgetRecord {
  spent: number;
  limit: number;
  next: string | null;
  at: number;
}

export interface Traversal {
  from: string;
  to: string;
  at: number;
  seq: number;
  reason?: string;
  label?: string;
}

export interface LogLine {
  node: string;
  at: number;
  text: string;
  level: string;
}

export interface QuestionRecord {
  id: string;
  node: string;
  text: string;
  type: string;
  options: QuestionOption[];
  asked: number;
  answered?: number;
  answer?: string;
}

export interface ArtifactRecord {
  node: string;
  name: string;
  at: number;
  path?: string;
  bytes?: number;
  version?: number;
  kind?: string;
}

/** A WorkspaceChanged event: what one stage visit changed in its working directory. */
export interface WorkspaceChangeRecord {
  node: string;
  at: number;
  added: number;
  modified: number;
  deleted: number;
  outOfScope: number;
}

export interface RunReplay {
  startedAt: number | null;
  endedAt: number | null;
  outcome: "running" | "success" | "fail";
  failureReason?: string;
  executions: StageExecution[];
  traversals: Traversal[];
  logs: LogLine[];
  questions: QuestionRecord[];
  artifacts: ArtifactRecord[];
  changes: WorkspaceChangeRecord[];
  continued: ContinuedFailure[];
  metrics: MetricRecord[];
  visitLimits: VisitLimitRecord[];
  /** Set while the run's latest stop is its cost budget (a resume clears it). */
  budget: BudgetRecord | null;
  restarts: number;
  events: number;
}

/** Parses "00:00:01.2340000" / "1.02:03:04.5" (TimeSpan) or a number of ms. */
export function parseDuration(value: unknown): number | undefined {
  if (typeof value === "number") return value;
  if (typeof value !== "string") return undefined;
  const match = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(value);
  if (!match) return undefined;
  const [, days, h, m, s] = match;
  return ((Number(days ?? 0) * 24 + Number(h)) * 3600 + Number(m) * 60 + Number(s)) * 1000;
}

const time = (e: PipelineEventJson) => Date.parse(e.timestamp);

const finite = (value: unknown): number | undefined => (typeof value === "number" && Number.isFinite(value) ? value : undefined);

/** The numeric entries of an event's `metrics` object. */
function numbers(value: unknown): Record<string, number> {
  const out: Record<string, number> = {};
  if (value && typeof value === "object" && !Array.isArray(value)) {
    for (const [name, v] of Object.entries(value as Record<string, unknown>)) {
      const n = finite(v);
      if (n !== undefined) out[name] = n;
    }
  }
  return out;
}

/** The latest execution of a node that started at or before `at`. */
function lastExecution(executions: StageExecution[], node: string, at: number): StageExecution | undefined {
  for (let i = executions.length - 1; i >= 0; i--) if (executions[i].node === node && executions[i].start <= at) return executions[i];
  return undefined;
}

/** Folds an event stream into stage executions, traversals, logs, questions, artifacts, and flow-control records. */
export function digest(events: PipelineEventJson[]): RunReplay {
  const replay: RunReplay = {
    startedAt: null,
    endedAt: null,
    outcome: "running",
    executions: [],
    traversals: [],
    logs: [],
    questions: [],
    artifacts: [],
    changes: [],
    continued: [],
    metrics: [],
    visitLimits: [],
    budget: null,
    restarts: 0,
    events: events.length,
  };
  const open = new Map<string, StageExecution>();
  const visits = new Map<string, number>();
  let seq = 0;
  for (const e of events) {
    seq = e.seq ?? seq + 1;
    const at = time(e);
    switch (e.type) {
      case "PipelineStarted":
        if (replay.startedAt === null) replay.startedAt = at;
        replay.endedAt = null;
        replay.outcome = "running";
        replay.budget = null;
        // A resume: stages the stopped run left open never completed, and nothing runs inside them now.
        open.clear();
        break;
      case "PipelineCompleted":
        replay.endedAt = at;
        replay.outcome = "success";
        break;
      case "PipelineFailed":
        replay.endedAt = at;
        replay.outcome = "fail";
        replay.failureReason = e.error === undefined ? undefined : stripAnsi(e.error);
        break;
      case "StageStarted": {
        // Top-level stages run one at a time, so a stage that starts while another is open runs inside it (a branch
        // of a parallel node); the earliest open stage is the top-level one.
        let parent: StageExecution | undefined;
        for (const candidate of open.values()) if (candidate.node !== e.node_id && (!parent || candidate.start < parent.start)) parent = candidate;
        const exec: StageExecution = {
          node: e.node_id!,
          attempt: e.attempt ?? 1,
          index: e.index ?? 0,
          segment: replay.restarts,
          parent: parent ? (parent.parent ?? parent.node) : undefined,
          parentIndex: parent ? (parent.parentIndex ?? parent.index) : undefined,
          start: at,
        };
        open.set(exec.node, exec);
        replay.executions.push(exec);
        if (exec.attempt === 1) visits.set(exec.node, (visits.get(exec.node) ?? 0) + 1);
        break;
      }
      case "StageCompleted": {
        const exec = open.get(e.node_id!);
        if (exec) {
          exec.end = at;
          exec.status = e.status;
          exec.notes = e.notes;
          open.delete(e.node_id!);
        }
        break;
      }
      case "StageFailed": {
        const exec = open.get(e.node_id!);
        if (exec) {
          exec.error = e.error === undefined ? undefined : stripAnsi(e.error);
          if (e.will_retry) {
            exec.end = at;
            exec.status = "retry";
            open.delete(e.node_id!);
          }
        }
        break;
      }
      case "StageLog":
        // Agent and tool output often carries terminal escape sequences.
        replay.logs.push({ node: e.node_id ?? "", at, text: stripAnsi(e.message), level: e.level ?? "info" });
        break;
      case "EdgeSelected":
        replay.traversals.push({ from: e.from!, to: e.to!, at, seq, reason: e.reason, label: e.label });
        break;
      case "InterviewStarted":
        replay.questions.push({ id: e.question_id!, node: e.stage ?? "", text: e.question ?? "", type: String(e.question_type ?? "multiple_choice"), options: e.options ?? [], asked: at });
        break;
      case "InterviewCompleted":
      case "InterviewTimeout": {
        const q = replay.questions.find((x) => x.id === e.question_id);
        if (q) {
          q.answered = at;
          q.answer = e.type === "InterviewTimeout" ? "(timed out)" : e.answer;
        }
        break;
      }
      case "ArtifactProduced":
        replay.artifacts.push({
          node: e.node_id ?? "",
          name: String(e.name ?? ""),
          at,
          path: e.path as string | undefined,
          bytes: e.bytes as number | undefined,
          version: e.version as number | undefined,
          kind: e.kind as string | undefined,
        });
        break;
      case "LoopRestarted":
        replay.restarts++;
        break;
      case "WorkspaceChanged":
        replay.changes.push({
          node: e.node_id ?? "",
          at,
          added: Number(e.added ?? 0),
          modified: Number(e.modified ?? 0),
          deleted: Number(e.deleted ?? 0),
          outOfScope: Array.isArray(e.out_of_scope) ? e.out_of_scope.length : 0,
        });
        break;
      case "FailedStageContinued": {
        const node = e.node_id ?? "";
        const to = String(e.to ?? "");
        const reason = typeof e.failure_reason === "string" && e.failure_reason ? stripAnsi(e.failure_reason) : undefined;
        replay.continued.push({ node, to, at, reason });
        const exec = lastExecution(replay.executions, node, at);
        if (exec) exec.continuedTo = to;
        break;
      }
      case "MetricsRecorded": {
        const node = e.node_id ?? "";
        const metrics = numbers(e.metrics);
        if (Object.keys(metrics).length === 0) break;
        replay.metrics.push({ node, at, visit: Math.max(1, visits.get(node) ?? 1), metrics });
        const exec = lastExecution(replay.executions, node, at);
        if (exec) exec.metrics = { ...exec.metrics, ...metrics };
        break;
      }
      case "VisitLimitReached":
        replay.visitLimits.push({ node: e.node_id ?? "", max: finite(e.max_visits) ?? 0, target: typeof e.target === "string" && e.target ? e.target : null, at });
        break;
      case "BudgetExceeded":
        replay.budget = { spent: finite(e.spent_usd) ?? 0, limit: finite(e.limit_usd) ?? 0, next: typeof e.next_node_id === "string" ? e.next_node_id : null, at };
        break;
    }
  }
  return replay;
}

/**
 * Stages the run carries as failed: their last failure flowed onward along an unconditional edge, and no later execution
 * of them succeeded (as the wind tunnel counts "landed with failed stages").
 */
export function failedStagesBehind(replay: RunReplay): string[] {
  const behind: string[] = [];
  for (const node of new Set(replay.continued.map((c) => c.node))) {
    const last = replay.continued.filter((c) => c.node === node).at(-1)!;
    const redeemed = replay.executions.some((x) => x.node === node && x.start > last.at && (x.status === "success" || x.status === "partial_success"));
    if (!redeemed) behind.push(node);
  }
  return behind;
}

/** A metric value for readouts: whole numbers as is, others to at most two decimals. */
export const metricText = (n: number) => (Number.isInteger(n) ? String(n) : String(Math.round(n * 100) / 100));

export interface OverlayOptions {
  /** Instant to show (ms epoch); null = latest. */
  at: number | null;
  /** Now (for running stages and edge flashes). */
  now: number;
  /** Pending questions of a live run, shown on their gate with answer buttons. */
  pending?: PendingQuestion[];
  /** Per-node readouts from backend metadata (cost, tokens), keyed by node id. */
  readouts?: Record<string, { label: string; value: string; tone?: "ok" | "fail" | "warn" }[]>;
  allNodes: string[];
}

const STATUS_STATE: Record<string, NodeState> = {
  success: "success",
  partial_success: "partial_success",
  fail: "fail",
  retry: "retry",
  skipped: "skipped",
};

/** The canvas overlay at instant `at` (or the latest state). */
export function overlayAt(replay: RunReplay, options: OverlayOptions): CanvasOverlay {
  const t = options.at ?? Number.POSITIVE_INFINITY;
  const live = options.at === null;
  const nodes: Record<string, NodeOverlay> = {};
  for (const id of options.allNodes) nodes[id] = { state: "dim" };

  const byNode = new Map<string, StageExecution[]>();
  for (const exec of replay.executions) {
    if (exec.start > t) continue;
    byNode.set(exec.node, [...(byNode.get(exec.node) ?? []), exec]);
  }
  for (const [node, execs] of byNode) {
    const last = execs[execs.length - 1];
    const finished = last.end !== undefined && last.end <= t;
    const visits = execs.filter((x) => x.attempt === 1).length;
    const state: NodeState = finished ? STATUS_STATE[last.status ?? "success"] ?? "success" : replay.endedAt !== null && replay.endedAt <= t ? "fail" : "running";
    const total = execs.reduce((sum, x) => sum + ((x.end !== undefined && x.end <= t ? x.end : Math.min(t, options.now, replay.endedAt ?? Number.POSITIVE_INFINITY)) - x.start), 0);
    const readouts: NonNullable<NodeOverlay["readouts"]> = [];
    // A stage the run stopped in (killed or cancelled) never completed: its time is unknown.
    if (state !== "running") readouts.push(finished ? { label: visits > 1 ? "total" : "time", value: formatDuration(total) } : { label: "run", value: "stopped here", tone: "fail" });
    if (last.attempt > 1) readouts.push({ label: "attempt", value: String(last.attempt), tone: "warn" });
    // The files the latest finished visit changed (stages that changed nothing emit no WorkspaceChanged).
    const changed = finished ? replay.changes.findLast((c) => c.node === node && c.at >= last.start && c.at <= t) : undefined;
    if (changed) readouts.push({ label: "files", value: `+${changed.added} ~${changed.modified} −${changed.deleted}`, tone: changed.outOfScope > 0 ? "fail" : undefined });
    const went = replay.continued.filter((c) => c.node === node && c.at >= last.start && c.at <= t).at(-1);
    if (went) readouts.push({ label: "failed, went on", value: `→ ${went.to}`, tone: "fail" });
    const reported = replay.metrics.filter((m) => m.node === node && m.at <= t).at(-1);
    if (reported) for (const [name, value] of Object.entries(reported.metrics).slice(0, 3)) readouts.push({ label: name, value: metricText(value) });
    const limited = replay.visitLimits.filter((v) => v.node === node && v.at <= t).at(-1);
    if (limited) readouts.push({ label: "visit limit", value: `${limited.max} → ${limited.target ?? "fail"}`, tone: "warn" });
    for (const r of options.readouts?.[node] ?? []) readouts.push(r);
    const lines = replay.logs
      .filter((l) => l.node === node && l.at <= t && l.at >= last.start)
      .slice(-3)
      .map((l) => ({ text: l.text, level: l.level }));
    nodes[node] = {
      state,
      badge: visits > 1 ? `×${visits}` : undefined,
      startedAt: state === "running" ? new Date(last.start).toISOString() : undefined,
      readouts: readouts.length > 0 ? readouts : undefined,
      lines: state === "running" && lines.length > 0 ? lines : undefined,
      attempt: last.attempt,
      produced: replay.artifacts.filter((a) => a.node === node && a.at <= t).map((a) => a.name),
    };
  }

  for (const q of replay.questions) {
    if (q.asked > t || (q.answered !== undefined && q.answered <= t)) continue;
    const pending = live ? options.pending?.find((p) => p.id === q.id) : undefined;
    nodes[q.node] = { ...(nodes[q.node] ?? {}), state: "waiting", question: pending };
  }
  if (live) {
    for (const p of options.pending ?? []) {
      if (!nodes[p.stage]?.question) nodes[p.stage] = { ...(nodes[p.stage] ?? {}), state: "waiting", question: p };
    }
  }

  // A budget stop: the run halted before this stage, which never ran.
  const budget = replay.budget;
  if (budget && budget.at <= t && budget.next && nodes[budget.next]) {
    const readouts = [...(nodes[budget.next].readouts ?? []), { label: "budget stop", value: `${formatUsd(budget.spent)} of ${formatUsd(budget.limit)}`, tone: "fail" as const }];
    nodes[budget.next] = { ...nodes[budget.next], state: "stopped", readouts };
  }

  const edges: Record<string, EdgeOverlay> = {};
  const counts: Record<string, number> = {};
  let latest: Traversal | undefined;
  for (const tr of replay.traversals) {
    if (tr.at > t) continue;
    const key = edgeKey(tr.from, tr.to);
    counts[key] = (counts[key] ?? 0) + 1;
    edges[key] = { state: "taken", text: counts[key] > 1 ? `×${counts[key]}` : undefined, pulse: tr.seq };
    if (!latest || tr.at >= latest.at) latest = tr;
  }
  if (latest && (replay.endedAt === null || replay.endedAt > t)) {
    const key = edgeKey(latest.from, latest.to);
    const reference = live ? options.now : t;
    if (reference - latest.at < 4000) edges[key] = { ...edges[key], state: "active" };
  }
  return { kind: live ? "live" : "replay", nodes, edges };
}

/** Rows for the timeline: one per stage, in order of first start. */
export function timelineRows(replay: RunReplay): { node: string; executions: StageExecution[] }[] {
  const rows = new Map<string, StageExecution[]>();
  for (const exec of replay.executions) rows.set(exec.node, [...(rows.get(exec.node) ?? []), exec]);
  return [...rows.entries()].map(([node, executions]) => ({ node, executions }));
}

/**
 * The stage snapshot to fork from to re-run `exec`: the latest snapshot of the same run segment saved before it started
 * (snapshots are named `<execution index>-<node>`, prefixed `restart-N/` in loop-restart directories, and are saved
 * after top-level stages only). A stage that ran inside a parallel stage maps to the snapshot before that parallel
 * stage — its own snapshot is saved after every branch finished — so the whole parallel stage runs again. Null when
 * there is nothing before it in its segment (start a new run instead).
 */
export function checkpointBefore(exec: StageExecution, checkpoints: string[]): string | null {
  const before = exec.parentIndex ?? exec.index;
  const prefix = exec.segment === 0 ? "" : `restart-${exec.segment}/`;
  let best: { name: string; index: number } | null = null;
  for (const name of checkpoints) {
    if (exec.segment === 0 ? name.includes("/") : !name.startsWith(prefix)) continue;
    const match = /^(\d+)-(.+)$/.exec(name.slice(prefix.length));
    if (!match) continue;
    const index = Number(match[1]);
    if (index < before && (!best || index > best.index)) best = { name, index };
  }
  return best?.name ?? null;
}

/** The node a snapshot was taken after (`0004-plan` → `plan`). */
export const checkpointNode = (name: string) => name.slice(name.lastIndexOf("/") + 1).replace(/^\d+-/, "");
