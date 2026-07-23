import type { PendingQuestion, PipelineEventJson, QuestionOption } from "../../api/types";
import { edgeKey, type CanvasOverlay, type EdgeOverlay, type NodeOverlay, type NodeState } from "../../canvas/build";
import { formatDuration } from "../../canvas/Elapsed";

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

/** Folds an event stream into stage executions, traversals, logs, questions, and artifacts. */
export function digest(events: PipelineEventJson[]): RunReplay {
  const replay: RunReplay = { startedAt: null, endedAt: null, outcome: "running", executions: [], traversals: [], logs: [], questions: [], artifacts: [], restarts: 0, events: events.length };
  const open = new Map<string, StageExecution>();
  let seq = 0;
  for (const e of events) {
    seq = e.seq ?? seq + 1;
    const at = time(e);
    switch (e.type) {
      case "PipelineStarted":
        if (replay.startedAt === null) replay.startedAt = at;
        replay.endedAt = null;
        replay.outcome = "running";
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
        replay.failureReason = e.error;
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
          exec.error = e.error;
          if (e.will_retry) {
            exec.end = at;
            exec.status = "retry";
            open.delete(e.node_id!);
          }
        }
        break;
      }
      case "StageLog":
        replay.logs.push({ node: e.node_id ?? "", at, text: e.message ?? "", level: e.level ?? "info" });
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
    }
  }
  return replay;
}

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
