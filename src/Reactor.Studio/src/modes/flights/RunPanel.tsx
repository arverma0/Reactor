import { useEffect, useMemo, useState } from "react";
import { api } from "../../api/client";
import type { HistoryStage, PipelineEventJson } from "../../api/types";
import { Elapsed, formatDuration } from "../../canvas/Elapsed";
import { Button, Empty, Spinner, TextArea, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { Markdown } from "../../components/Markdown";
import { findNode, kindOf, nodeLabel } from "../../model/doc";
import type { StudioDoc } from "../../model/types";
import { toast, useStudio } from "../../state/store";
import { ForkDialog } from "./ForkDialog";
import { checkpointBefore, checkpointNode, type RunReplay, type StageExecution } from "./replay";
import { loadDetail, loadHistory, refreshRuns, selectRun, useFlights } from "./store";
import { costText, type FlightView } from "./view";

const STATUS_LABEL: Record<string, string> = {
  queued: "queued",
  running: "in flight",
  success: "landed",
  partial_success: "landed (partial)",
  fail: "failed",
  cancelled: "cancelled",
  error: "error",
  interrupted: "interrupted",
};

const NO_FEATURES: string[] = [];

export function StatusPill({ status }: { status: string }) {
  return (
    <span className={cx("rs-status", `is-${status}`)}>
      <span className="rs-status__dot" />
      {STATUS_LABEL[status] ?? status}
    </span>
  );
}

export function RunPanel({ view, replay, doc, stage, onSelectStage }: { view: FlightView | null; replay: RunReplay; doc: StudioDoc | null; stage: string | null; onSelectStage: (id: string | null) => void }) {
  const features = useStudio((s) => s.info?.features) ?? NO_FEATURES;
  const flights = useFlights((s) => s.flights);
  /** undefined: dialog closed; null: open without a preset stage. */
  const [forkFrom, setForkFrom] = useState<StageExecution | null | undefined>(undefined);
  const children = useMemo(() => (view?.dir ? flights.filter((f) => f.forkedFrom?.id === view.dir) : []), [flights, view?.dir]);
  if (!view) return <Empty title="Choose a flight." icon="flights">Runs started from the Studio, the API, the console, or <span className="mono">reactor run</span> appear on the left.</Empty>;
  const canFork = features.includes("fork") && !!view.dir && !view.active && view.checkpoints.length > 0;
  const parent = view.forkedFrom ? flights.find((f) => f.dir === view.forkedFrom!.id) : undefined;
  const cost = costText(view.cost);
  const label = (id: string) => {
    const node = doc ? findNode(doc, id) : undefined;
    return node && doc ? nodeLabel(doc, node) : id;
  };
  return (
    <div className="rs-runpanel">
      <header className="rs-runpanel__head">
        <div className="rs-runpanel__kicker">
          <StatusPill status={view.status} />
          {view.simulated ? <span className="rs-chip">simulated{view.simulationMode ? ` · ${view.simulationMode}` : ""}</span> : null}
          <span className="faint mono" title={view.dir ?? view.liveId ?? ""}>
            {view.liveId ?? view.dir}
          </span>
        </div>
        <h2 className="rs-runpanel__title">{view.name}</h2>
        {view.goal ? <p className="rs-runpanel__goal serif">{view.goal}</p> : null}
        <div className="rs-runpanel__facts">
          <span>
            <span className="label">duration</span>{" "}
            {view.active && view.startedAt ? <Elapsed since={view.startedAt} className="num" /> : <span className="num">{view.durationMs != null ? formatDuration(view.durationMs) : "—"}</span>}
          </span>
          <span>
            <span className="label">stages</span> <span className="num">{replay.executions.length}</span>
          </span>
          {replay.restarts ? (
            <span>
              <span className="label">restarts</span> <span className="num">{replay.restarts}</span>
            </span>
          ) : null}
          {cost ? (
            <span title={view.cost ? Object.entries(view.cost).map(([k, v]) => `${k}: ${v}`).join("\n") : undefined}>
              <span className="label">cost</span> <span className="num">{cost}</span>
            </span>
          ) : null}
          <span>
            <span className="label">events</span> <span className="num">{replay.events}</span>
          </span>
        </div>
        <div className="rs-runpanel__actions">
          {view.active && view.liveId ? (
            <Button
              size="sm"
              variant="danger"
              icon="stop"
              onClick={async () => {
                await api.cancel(view.liveId!).catch((e) => toast((e as Error).message, "error"));
                loadDetail(view.key);
              }}
            >
              Cancel
            </Button>
          ) : null}
          {!view.active && view.liveId && view.status !== "success" && !view.simulated ? (
            <Button
              size="sm"
              icon="retry"
              title="Continue this run from its last checkpoint"
              onClick={async () => {
                try {
                  await api.resume(view.liveId!);
                  selectRun(view.key);
                  void refreshRuns();
                } catch (e) {
                  toast((e as Error).message, "error");
                }
              }}
            >
              Resume
            </Button>
          ) : null}
          {canFork ? (
            <Button size="sm" icon="fork" title="Fork this flight from just before one of its stages, as flown or with your current design" onClick={() => setForkFrom(null)}>
              Re-fly…
            </Button>
          ) : null}
          <span className="rs-runpanel__logs mono" title={view.location}>
            {view.location}
          </span>
        </div>
        {view.failure ? <div className="rs-runpanel__failure">{view.failure}</div> : null}
        {parent || view.forkedFrom || children.length > 0 ? (
          <div className="rs-lineage">
            {view.forkedFrom ? (
              <button type="button" className="rs-lineage__item" disabled={!parent} onClick={() => parent && selectRun(parent.key)} title={view.forkedFrom.id}>
                <Glyph name="fork" size={12} />
                <span>
                  forked from {parent ? <b>{parent.name}</b> : <span className="mono">{view.forkedFrom.run_id}</span>} after <b>{label(checkpointNode(view.forkedFrom.checkpoint))}</b>
                </span>
                {parent ? <span className={cx("rs-led", `is-${parent.status}`)} /> : null}
              </button>
            ) : null}
            {children.map((child) => (
              <button key={child.key} type="button" className="rs-lineage__item" onClick={() => selectRun(child.key)}>
                <Glyph name="fork" size={12} />
                <span>
                  re-flown after <b>{label(checkpointNode(child.forkedFrom!.checkpoint))}</b>
                  {child.startedAt ? <span className="faint"> · {new Date(child.startedAt).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" })}</span> : null}
                </span>
                <span className={cx("rs-led", `is-${child.status === "running" ? "running" : child.status}`)} />
              </button>
            ))}
          </div>
        ) : null}
        {view.forkedFrom && parent?.dir && view.dir ? <Compare parentDir={parent.dir} view={view} doc={doc} /> : null}
      </header>
      {stage ? (
        <StagePanel key={`${view.key}:${stage}`} view={view} stage={stage} doc={doc} replay={replay} onClose={() => onSelectStage(null)} onFork={canFork ? setForkFrom : undefined} />
      ) : (
        <FlightLog replay={replay} doc={doc} onSelectStage={onSelectStage} />
      )}
      {forkFrom !== undefined ? <ForkDialog view={view} replay={replay} doc={doc} initial={forkFrom ?? undefined} onClose={() => setForkFrom(undefined)} /> : null}
    </div>
  );
}

/** Stage-by-stage comparison of a re-flown flight with the flight it was forked from. */
function Compare({ parentDir, view, doc }: { parentDir: string; view: FlightView; doc: StudioDoc | null }) {
  const parent = useFlights((s) => s.history[parentDir]);
  const [open, setOpen] = useState(false);
  useEffect(() => {
    if (open && !parent) void loadHistory(parentDir);
  }, [open, parent, parentDir]);
  const rows = useMemo(() => {
    if (!parent) return [];
    const mine = new Map(view.stages.map((s) => [s.node, s]));
    const theirs = new Map(parent.stages.map((s) => [s.node, s]));
    const order = [...parent.stages.map((s) => s.node), ...view.stages.map((s) => s.node).filter((n) => !theirs.has(n))];
    return order.map((node) => ({ node, before: theirs.get(node), after: mine.get(node) }));
  }, [parent, view.stages]);
  const cell = (s: HistoryStage | undefined) => {
    if (!s) return <span className="faint">—</span>;
    const units = typeof s.backend?.ai_units === "number" ? (s.backend.ai_units as number) : undefined;
    return (
      <span className="rs-compare__cell">
        <span className={cx("rs-led", `is-${s.status === "running" ? "running" : s.status ?? "idle"}`)} />
        <span className="num">{s.duration_ms != null ? formatDuration(s.duration_ms) : ""}</span>
        {s.visits > 1 ? <span className="num faint">×{s.visits}</span> : null}
        {units !== undefined ? <span className="num faint">{units.toFixed(1)}</span> : null}
      </span>
    );
  };
  return (
    <div className={cx("rs-compare", open && "is-open")}>
      <button type="button" className="rs-linkbtn" onClick={() => setOpen(!open)}>
        {open ? "Hide comparison" : "Compare with the original flight"}
      </button>
      {open ? (
        parent ? (
          <table className="rs-compare__table">
            <thead>
              <tr>
                <th>stage</th>
                <th>original</th>
                <th>re-flown</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.node} className={cx(r.before?.status !== r.after?.status && r.after && r.before && "is-changed")}>
                  <td>{doc && findNode(doc, r.node) ? nodeLabel(doc, findNode(doc, r.node)!) : r.node}</td>
                  <td>{cell(r.before)}</td>
                  <td>{r.after ? cell(r.after) : r.before ? <span className="faint">restored</span> : <span className="faint">—</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : (
          <Spinner />
        )
      ) : null}
    </div>
  );
}

const FILE_ORDER = ["response.md", "prompt.md", "prompt.effective.md", "status.json", "backend.json", "transcript.md", "steering.md", "stdout.txt", "stderr.txt", "command.txt"];

const sortFiles = (names: string[]) => [...names].sort((a, b) => ((FILE_ORDER.indexOf(a) + 99) % 99) - ((FILE_ORDER.indexOf(b) + 99) % 99) || a.localeCompare(b));

function StagePanel({
  view,
  stage,
  doc,
  replay,
  onClose,
  onFork,
}: {
  view: FlightView;
  stage: string;
  doc: StudioDoc | null;
  replay: RunReplay;
  onClose: () => void;
  onFork?: (exec: StageExecution) => void;
}) {
  const node = doc ? findNode(doc, stage) : undefined;
  const features = useStudio((s) => s.info?.features) ?? NO_FEATURES;
  const recorded = view.stages.find((s) => s.node === stage);
  /** The visit whose files are shown; null = the latest. Visits count from 1 in each run directory (loop restarts start a new one). */
  const [shown, setShown] = useState<{ segment: number; visit: number } | null>(null);
  const [files, setFiles] = useState<string[] | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [content, setContent] = useState<string | null>(null);
  const [backend, setBackend] = useState<Record<string, unknown> | null>(null);
  const [steer, setSteer] = useState("");
  const execs = replay.executions.filter((x) => x.node === stage);
  const running = execs.some((x) => x.end === undefined) && replay.endedAt === null;
  const logs = replay.logs.filter((l) => l.node === stage).slice(-80);
  const artifacts = replay.artifacts.filter((a) => a.node === stage);
  const lastSegment = execs.reduce((m, x) => Math.max(m, x.segment), 0);
  const visitsIn = (segment: number) => execs.filter((x) => x.attempt === 1 && x.segment === segment).length;
  const latestVisit = Math.max(1, visitsIn(lastSegment));
  const target = shown ?? { segment: lastSegment, visit: latestVisit };
  const older = target.segment !== lastSegment || target.visit < latestVisit;
  const segmentDir = (segment: number) => (!view.dir ? null : segment === 0 ? view.dir : `${view.dir}-restart-${segment}`);
  const directory = target.segment === lastSegment ? (recorded?.segment ?? segmentDir(lastSegment)) : segmentDir(target.segment);
  // The latest visit of a directory is the stage directory itself; earlier ones are history/visit-N.
  const visitParam = target.visit < visitsIn(target.segment) ? target.visit : undefined;
  const canPickVisit = !!recorded && !!view.dir && execs.filter((x) => x.attempt === 1).length > 1;
  // While the run is live the server's stage listing is freshest; afterwards the run directory has every visit.
  const fromLive = !!view.liveId && (view.active || !recorded);

  useEffect(() => {
    let cancelled = false;
    if (fromLive && !older) {
      api
        .stage(view.liveId!, stage)
        .then((info) => {
          if (cancelled) return;
          const names = sortFiles(info.files.map((f) => f.name));
          setFiles(names);
          setOpen((current) => (current && names.includes(current) ? current : (names.find((f) => f === "response.md") ?? names.find((f) => f === "stdout.txt") ?? names[0] ?? null)));
          if (names.includes("backend.json")) {
            api.stageFile(view.liveId!, stage, "backend.json").then((b) => !cancelled && setBackend(typeof b === "string" ? JSON.parse(b) : (b as Record<string, unknown>))).catch(() => undefined);
          } else setBackend(null);
        })
        .catch(() => !cancelled && setFiles([]));
    } else if (recorded && directory) {
      const names = sortFiles(recorded.files);
      setFiles(names);
      setOpen((current) => (current && names.includes(current) ? current : (names.find((f) => f === "response.md") ?? names.find((f) => f === "stdout.txt") ?? names[0] ?? null)));
      if (!older) setBackend(recorded.backend ?? null);
      else
        api
          .historyFile(directory, stage, "backend.json", visitParam)
          .then((b) => !cancelled && setBackend(typeof b === "string" ? JSON.parse(b) : (b as Record<string, unknown>)))
          .catch(() => !cancelled && setBackend(null));
    } else setFiles([]);
    return () => {
      cancelled = true;
    };
  }, [view.liveId, stage, execs.length, running, fromLive, older, visitParam, recorded, directory]);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setContent(null);
    const read = fromLive && !older ? api.stageFile(view.liveId!, stage, open) : directory ? api.historyFile(directory, stage, open, visitParam) : Promise.reject(new Error("not available"));
    read
      .then((c) => !cancelled && setContent(typeof c === "string" ? c : JSON.stringify(c, null, 2)))
      .catch((e) => !cancelled && setContent(`(${(e as Error).message === "Not found." ? "not recorded for this visit" : (e as Error).message})`));
    return () => {
      cancelled = true;
    };
  }, [view.liveId, stage, open, fromLive, older, visitParam, directory, execs.length]);

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

  const visitNumbers = new Map<number, number>();
  let currentVisit = 0;
  const segmentVisits = [...new Set(execs.map((x) => x.segment))].sort((a, b) => a - b).map((s) => [s, visitsIn(s)] as const);
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
        {execs.map((x, i) => {
          if (x.attempt === 1) {
            currentVisit = (visitNumbers.get(x.segment) ?? 0) + 1;
            visitNumbers.set(x.segment, currentVisit);
          }
          const thisVisit = currentVisit;
          const checkpoint = onFork && x.attempt === 1 ? checkpointBefore(x, view.checkpoints) : null;
          const isShown = canPickVisit && target.segment === x.segment && target.visit === thisVisit;
          const status = x.status ?? (x.end === undefined ? (replay.endedAt === null ? "running" : "interrupted") : "");
          return (
            <div
              key={i}
              className={cx("rs-exec", isShown && "is-shown", canPickVisit && "is-selectable")}
              onClick={() => canPickVisit && setShown(x.segment === lastSegment && thisVisit === latestVisit ? null : { segment: x.segment, visit: thisVisit })}
            >
              <span className={cx("rs-led", `is-${x.end === undefined ? (replay.endedAt === null ? "running" : "fail") : (x.status ?? "success")}`)} />
              <span className="mono">#{x.index}</span>
              <span>{x.attempt > 1 ? `attempt ${x.attempt}` : `visit ${thisVisit}`}</span>
              <span className="faint">
                {status}
                {x.segment > 0 ? ` · after restart ${x.segment}` : ""}
                {x.parent ? ` · in ${x.parent}` : ""}
              </span>
              <span className="num rs-exec__dur">{formatDuration((x.end ?? replay.endedAt ?? Date.now()) - x.start)}</span>
              {checkpoint ? (
                <button
                  type="button"
                  className="rs-exec__fork"
                  title={
                    x.parent
                      ? `Re-fly from just before ${x.parent}, the parallel stage this ran in (restores ${checkpoint})`
                      : `Re-fly from just before this visit (restores ${checkpoint})`
                  }
                  onClick={(e) => {
                    e.stopPropagation();
                    onFork?.(x);
                  }}
                >
                  <Glyph name="fork" size={12} /> re-fly
                </button>
              ) : null}
              {x.error ? <div className="rs-exec__error">{x.error}</div> : null}
            </div>
          );
        })}
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
      {view.active && running && view.liveId && features.includes("steering") && node && doc && kindOf(doc, node) === "agent" ? (
        <div className="rs-steer">
          <div className="label">Steer this agent</div>
          <TextArea value={steer} onChange={setSteer} placeholder="e.g. Skip the docs for now and get the tests passing first." minRows={2} />
          <Button
            size="sm"
            icon="steer"
            disabled={!steer.trim()}
            onClick={async () => {
              try {
                await api.steer(view.liveId!, steer.trim(), stage);
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
      {canPickVisit ? (
        <div className="rs-visits">
          <span className="label">visit</span>
          {segmentVisits.flatMap(([segment, count]) =>
            Array.from({ length: count }, (_, i) => i + 1).map((v) => (
              <button
                key={`${segment}:${v}`}
                type="button"
                className={cx("rs-visits__btn num", target.segment === segment && target.visit === v && "is-on")}
                title={segment > 0 ? `Visit ${v} after loop restart ${segment}` : `Visit ${v}`}
                onClick={() => setShown(segment === lastSegment && v === latestVisit ? null : { segment, visit: v })}
              >
                {segment > 0 ? `${segment}·${v}` : v}
              </button>
            )),
          )}
          <span className="faint">{older ? "earlier visit" : "latest"}</span>
        </div>
      ) : null}
      <div className="rs-files-tabs">
        {files === null ? <Spinner /> : null}
        {files?.map((f) => (
          <button key={f} type="button" className={cx("rs-files-tab", open === f && "is-on")} onClick={() => setOpen(f)}>
            {f}
          </button>
        ))}
      </div>
      <div className="rs-filebody">{content === null ? null : open?.endsWith(".md") ? <Markdown text={content} /> : <pre className="mono">{content}</pre>}</div>
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

const STATUS_ICON: Record<string, string> = { fail: "cross", retry: "retry", partial_success: "check", skipped: "chevronRight" };

const iconFor = (e: PipelineEventJson) => (e.type === "StageCompleted" && e.status && STATUS_ICON[e.status] ? STATUS_ICON[e.status] : (EVENT_ICON[e.type] ?? "more"));

function FlightLog({ replay, doc, onSelectStage }: { replay: RunReplay; doc: StudioDoc | null; onSelectStage: (id: string) => void }) {
  const flights = useFlightsEvents();
  const items = useMemo(() => flights.filter((e) => e.type !== "CheckpointSaved" && e.type !== "StageLog").slice(-300), [flights]);
  const label = (id?: string) => (id && doc ? (findNode(doc, id) ? nodeLabel(doc, findNode(doc, id)!) : id) : (id ?? ""));
  return (
    <div className="rs-flightlog">
      <div className="label rs-flightlog__head">Flight log · {replay.events} events</div>
      {items.length === 0 ? <Empty title="Waiting for the first event…" /> : null}
      {items.map((e, i) => (
        <button key={i} type="button" className={cx("rs-flightlog__row", `is-${e.type}`, e.type === "StageCompleted" && `is-status-${e.status}`)} onClick={() => e.node_id && onSelectStage(e.node_id)}>
          <span className="rs-flightlog__time num">{new Date(e.timestamp).toLocaleTimeString([], { hour12: false })}</span>
          <span className="rs-flightlog__icon">
            <Glyph name={iconFor(e)} size={12} />
          </span>
          <span className="rs-flightlog__text">{describe(e, label)}</span>
        </button>
      ))}
    </div>
  );
}

const REASONS: Record<string, string> = {
  condition: "condition matched",
  preferred_label: "chosen label",
  suggested_next: "suggested next",
  weight: "highest weight",
  lexical: "first by name",
  unconditional: "only route",
};

function describe(e: PipelineEventJson, label: (id?: string) => string): string {
  switch (e.type) {
    case "StageStarted":
      return `${label(e.node_id)} started${(e.attempt ?? 1) > 1 ? ` (attempt ${e.attempt})` : ""}`;
    case "StageCompleted":
      return `${label(e.node_id)} — ${e.status}`;
    case "StageFailed":
      return `${label(e.node_id)} failed${e.will_retry ? ", retrying" : ""}: ${e.error ?? ""}`;
    case "EdgeSelected": {
      const reason = e.reason ? (REASONS[e.reason] ?? e.reason) : "";
      return `${label(e.from)} → ${label(e.to)}${e.label ? ` · ${e.label}` : ""}${reason ? ` (${reason})` : ""}`;
    }
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
