import { useEffect, useMemo, useState } from "react";
import { api } from "../../api/client";
import type { PipelineEventJson, RunDetail } from "../../api/types";
import { Elapsed, formatDuration } from "../../canvas/Elapsed";
import { Button, Empty, Spinner, TextArea, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { Markdown } from "../../components/Markdown";
import { findNode, kindOf, nodeLabel } from "../../model/doc";
import type { StudioDoc } from "../../model/types";
import { toast, useStudio } from "../../state/store";
import type { RunReplay } from "./replay";
import { loadDetail, refreshRuns, selectRun, useFlights } from "./store";

const STATUS_LABEL: Record<string, string> = {
  queued: "queued",
  running: "in flight",
  success: "landed",
  partial_success: "landed (partial)",
  fail: "failed",
  cancelled: "cancelled",
  error: "error",
};

export function StatusPill({ status }: { status: string }) {
  return (
    <span className={cx("rs-status", `is-${status}`)}>
      <span className="rs-status__dot" />
      {STATUS_LABEL[status] ?? status}
    </span>
  );
}

export function RunPanel({ detail, replay, doc, stage, onSelectStage }: { detail: RunDetail | undefined; replay: RunReplay; doc: StudioDoc | null; stage: string | null; onSelectStage: (id: string | null) => void }) {
  if (!detail) return <Empty title="Choose a flight." icon="flights">Runs started from the Studio, the API, or the console appear on the left.</Empty>;
  const active = detail.status === "running" || detail.status === "queued";
  const failure = detail.error ?? detail.result?.failure_reason;
  return (
    <div className="rs-runpanel">
      <header className="rs-runpanel__head">
        <div className="rs-runpanel__kicker">
          <StatusPill status={detail.status} />
          {detail.simulated ? <span className="rs-chip">simulated · {detail.simulation_mode}</span> : null}
          <span className="faint mono">{detail.id}</span>
        </div>
        <h2 className="rs-runpanel__title">{detail.name}</h2>
        {detail.goal ? <p className="rs-runpanel__goal serif">{detail.goal}</p> : null}
        <div className="rs-runpanel__facts">
          <span>
            <span className="label">duration</span>{" "}
            {active && detail.started_at ? <Elapsed since={detail.started_at} className="num" /> : <span className="num">{detail.duration_seconds != null ? formatDuration(detail.duration_seconds * 1000) : "—"}</span>}
          </span>
          <span>
            <span className="label">stages</span> <span className="num">{replay.executions.length}</span>
          </span>
          {replay.restarts ? (
            <span>
              <span className="label">restarts</span> <span className="num">{replay.restarts}</span>
            </span>
          ) : null}
          <span>
            <span className="label">events</span> <span className="num">{detail.event_count}</span>
          </span>
        </div>
        <div className="rs-runpanel__actions">
          {active ? (
            <Button
              size="sm"
              variant="danger"
              icon="stop"
              onClick={async () => {
                await api.cancel(detail.id).catch((e) => toast((e as Error).message, "error"));
                void loadDetail(detail.id);
              }}
            >
              Cancel
            </Button>
          ) : detail.status !== "success" ? (
            <Button
              size="sm"
              icon="retry"
              onClick={async () => {
                try {
                  await api.resume(detail.id);
                  selectRun(detail.id);
                  void refreshRuns();
                } catch (e) {
                  toast((e as Error).message, "error");
                }
              }}
            >
              Resume from checkpoint
            </Button>
          ) : null}
          <span className="rs-runpanel__logs mono" title={detail.logs_root}>
            {detail.logs_root.split(/[\\/]/).slice(-2).join("/")}
          </span>
        </div>
        {failure ? <div className="rs-runpanel__failure">{failure}</div> : null}
      </header>
      {stage ? <StagePanel runId={detail.id} stage={stage} doc={doc} replay={replay} onClose={() => onSelectStage(null)} live={active} /> : <FlightLog events={replay} doc={doc} onSelectStage={onSelectStage} />}
    </div>
  );
}

interface StageFile {
  name: string;
  size: number;
}

const FILE_ORDER = ["response.md", "prompt.md", "prompt.effective.md", "status.json", "backend.json", "transcript.md", "stdout.txt", "stderr.txt", "command.txt"];

function StagePanel({ runId, stage, doc, replay, onClose, live }: { runId: string; stage: string; doc: StudioDoc | null; replay: RunReplay; onClose: () => void; live: boolean }) {
  const node = doc ? findNode(doc, stage) : undefined;
  const [files, setFiles] = useState<StageFile[] | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [content, setContent] = useState<string | null>(null);
  const [backend, setBackend] = useState<Record<string, unknown> | null>(null);
  const [steer, setSteer] = useState("");
  const features = useStudio((s) => s.info?.features ?? []);
  const execs = replay.executions.filter((x) => x.node === stage);
  const running = execs.some((x) => x.end === undefined) && replay.endedAt === null;
  const logs = replay.logs.filter((l) => l.node === stage).slice(-80);
  const artifacts = replay.artifacts.filter((a) => a.node === stage);

  useEffect(() => {
    let cancelled = false;
    setFiles(null);
    setBackend(null);
    api
      .stage(runId, stage)
      .then((info) => {
        if (cancelled) return;
        const sorted = [...info.files].sort((a, b) => (FILE_ORDER.indexOf(a.name) + 99) % 99 - (FILE_ORDER.indexOf(b.name) + 99) % 99 || a.name.localeCompare(b.name));
        setFiles(sorted);
        const first = sorted.find((f) => f.name === "response.md") ?? sorted.find((f) => f.name === "stdout.txt") ?? sorted[0];
        setOpen(first?.name ?? null);
        if (sorted.some((f) => f.name === "backend.json")) {
          api.stageFile(runId, stage, "backend.json").then((b) => !cancelled && setBackend(typeof b === "string" ? JSON.parse(b) : (b as Record<string, unknown>))).catch(() => undefined);
        }
      })
      .catch(() => !cancelled && setFiles([]));
    return () => {
      cancelled = true;
    };
  }, [runId, stage, execs.length, running]);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setContent(null);
    api
      .stageFile(runId, stage, open)
      .then((c) => !cancelled && setContent(typeof c === "string" ? c : JSON.stringify(c, null, 2)))
      .catch((e) => !cancelled && setContent(`(${(e as Error).message})`));
    return () => {
      cancelled = true;
    };
  }, [runId, stage, open]);

  const num = (key: string) => (typeof backend?.[key] === "number" ? (backend[key] as number) : undefined);
  const costs = [
    backend?.model ? { label: "model", value: String(backend.model) } : null,
    num("input_tokens") !== undefined ? { label: "in", value: (num("input_tokens") ?? 0).toLocaleString() } : null,
    num("output_tokens") !== undefined ? { label: "out", value: (num("output_tokens") ?? 0).toLocaleString() } : null,
    num("ai_units") !== undefined ? { label: "AIU", value: (num("ai_units") ?? 0).toFixed(2) } : null,
    num("premium_requests") !== undefined ? { label: "premium", value: String(num("premium_requests")) } : null,
    num("cost_usd") !== undefined ? { label: "cost", value: `$${(num("cost_usd") ?? 0).toFixed(3)}` } : null,
    num("tool_calls") !== undefined ? { label: "tools", value: String(num("tool_calls")) } : null,
  ].filter(Boolean) as { label: string; value: string }[];

  return (
    <div className="rs-stagepanel">
      <header className="rs-stagepanel__head">
        <button type="button" className="rs-linkbtn" onClick={onClose}>
          ← flight log
        </button>
        <div className="rs-stagepanel__title">
          {node && doc ? <Glyph name={kindOf(doc, node)} size={14} /> : null}
          <span>{node && doc ? nodeLabel(doc, node) : stage}</span>
          <span className="faint mono">{stage}</span>
        </div>
      </header>
      {costs.length > 0 ? (
        <div className="rs-stagepanel__costs">
          {costs.map((c) => (
            <span key={c.label} className="rs-readout">
              <span className="rs-readout__label">{c.label}</span>
              <span className="rs-readout__value num">{c.value}</span>
            </span>
          ))}
        </div>
      ) : null}
      <div className="rs-stagepanel__execs">
        {execs.length === 0 ? <span className="faint">Not reached in this run.</span> : null}
        {execs.map((x, i) => (
          <div key={i} className="rs-exec">
            <span className={cx("rs-led", `is-${x.end === undefined ? (replay.endedAt === null ? "running" : "fail") : x.status ?? "success"}`)} />
            <span className="mono">#{i + 1}</span>
            <span>{x.attempt > 1 ? `attempt ${x.attempt}` : "visit"}</span>
            <span className="faint">{x.status ?? (x.end === undefined ? "running" : "")}</span>
            <span className="num rs-exec__dur">{formatDuration((x.end ?? Date.now()) - x.start)}</span>
            {x.error ? <div className="rs-exec__error">{x.error}</div> : null}
          </div>
        ))}
      </div>
      {artifacts.length > 0 ? (
        <div className="rs-stagepanel__artifacts">
          {artifacts.map((a, i) => (
            <span key={i} className="rs-chip" title={a.path ?? "text"}>
              ◆ {a.name}
              {a.version ? <span className="faint"> v{a.version}</span> : null}
            </span>
          ))}
        </div>
      ) : null}
      {live && running && features.includes("steering") ? (
        <div className="rs-steer">
          <div className="label">Steer this agent</div>
          <TextArea value={steer} onChange={setSteer} placeholder="e.g. Skip the docs for now and get the tests passing first." minRows={2} />
          <Button
            size="sm"
            icon="steer"
            disabled={!steer.trim()}
            onClick={async () => {
              try {
                await api.post(`/studio/api/pipelines/${encodeURIComponent(runId)}/steer`, { node: stage, message: steer.trim() });
                toast("Steering queued — the agent sees it on its next turn.", "success");
                setSteer("");
              } catch (e) {
                toast((e as Error).message, "error");
              }
            }}
          >
            Send
          </Button>
        </div>
      ) : null}
      {logs.length > 0 ? (
        <div className="rs-stagepanel__logs mono">
          {logs.map((l, i) => (
            <div key={i} className={cx("rs-logline", `is-${l.level}`)}>
              {l.text}
            </div>
          ))}
        </div>
      ) : null}
      <div className="rs-files-tabs">
        {files === null ? <Spinner /> : null}
        {files?.map((f) => (
          <button key={f.name} type="button" className={cx("rs-files-tab", open === f.name && "is-on")} onClick={() => setOpen(f.name)}>
            {f.name}
          </button>
        ))}
      </div>
      <div className="rs-filebody">
        {content === null ? null : open?.endsWith(".md") ? <Markdown text={content} /> : <pre className="mono">{content}</pre>}
      </div>
    </div>
  );
}

const EVENT_ICON: Record<string, string> = {
  PipelineStarted: "play",
  PipelineCompleted: "check",
  PipelineFailed: "cross",
  StageStarted: "chevronRight",
  StageCompleted: "check",
  StageFailed: "cross",
  StageRetrying: "retry",
  EdgeSelected: "link",
  InterviewStarted: "human",
  InterviewCompleted: "human",
  InterviewTimeout: "clock",
  ArtifactProduced: "data",
  LoopRestarted: "retry",
  ParallelStarted: "fanout",
  ParallelCompleted: "join",
};

function FlightLog({ events, doc, onSelectStage }: { events: RunReplay; doc: StudioDoc | null; onSelectStage: (id: string) => void }) {
  const flights = useFlightsEvents();
  const items = useMemo(() => flights.filter((e) => e.type !== "CheckpointSaved" && e.type !== "StageLog").slice(-300), [flights]);
  const label = (id?: string) => (id && doc ? (findNode(doc, id) ? nodeLabel(doc, findNode(doc, id)!) : id) : id ?? "");
  return (
    <div className="rs-flightlog">
      <div className="label rs-flightlog__head">Flight log · {events.events} events</div>
      {items.length === 0 ? <Empty title="Waiting for the first event…" /> : null}
      {items.map((e, i) => (
        <button key={i} type="button" className={cx("rs-flightlog__row", `is-${e.type}`)} onClick={() => e.node_id && onSelectStage(e.node_id)}>
          <span className="rs-flightlog__time num">{new Date(e.timestamp).toLocaleTimeString([], { hour12: false })}</span>
          <span className="rs-flightlog__icon">
            <Glyph name={EVENT_ICON[e.type] ?? "more"} size={12} />
          </span>
          <span className="rs-flightlog__text">{describe(e, label)}</span>
        </button>
      ))}
    </div>
  );
}

function describe(e: PipelineEventJson, label: (id?: string) => string): string {
  switch (e.type) {
    case "StageStarted":
      return `${label(e.node_id)} started${(e.attempt ?? 1) > 1 ? ` (attempt ${e.attempt})` : ""}`;
    case "StageCompleted":
      return `${label(e.node_id)} — ${e.status}`;
    case "StageFailed":
      return `${label(e.node_id)} failed${e.will_retry ? ", retrying" : ""}: ${e.error ?? ""}`;
    case "EdgeSelected":
      return `${label(e.from)} → ${label(e.to)}${e.label ? ` · ${e.label}` : ""}${e.reason ? ` (${e.reason})` : ""}`;
    case "InterviewStarted":
      return `asked: ${e.question ?? ""}`;
    case "InterviewCompleted":
      return `answered: ${e.answer ?? ""}`;
    case "ArtifactProduced":
      return `${label(e.node_id)} produced ${String(e.name)}${e.path ? ` → ${String(e.path)}` : ""}`;
    default:
      return e.description ?? e.type;
  }
}

function useFlightsEvents(): PipelineEventJson[] {
  const selected = useFlights((s) => s.selected);
  const events = useFlights((s) => (selected ? s.events[selected] : undefined));
  return events ?? EMPTY;
}
const EMPTY: PipelineEventJson[] = [];
