import { useEffect, useMemo } from "react";
import { api } from "../../api/client";
import { canvasActions } from "../../canvas/actions";
import { Canvas } from "../../canvas/Canvas";
import { formatDuration } from "../../canvas/Elapsed";
import { Empty, Spinner, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import type { StudioDoc } from "../../model/types";
import { toast, useStudio } from "../../state/store";
import { overlayAt, digest } from "./replay";
import { RunPanel, StatusPill } from "./RunPanel";
import { loadDetail, refreshRuns, scrubTo, selectRun, selectStage, setPlaying, setSpeed, startClock, stopClock, useFlights } from "./store";
import { Timeline } from "./Timeline";

const relative = (iso?: string | null) => {
  if (!iso) return "";
  const s = (Date.now() - Date.parse(iso)) / 1000;
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.round(s / 60)}m ago`;
  if (s < 86400) return `${Math.round(s / 3600)}h ago`;
  return new Date(iso).toLocaleDateString();
};

export function FlightsMode() {
  const runs = useFlights((s) => s.runs);
  const selected = useFlights((s) => s.selected);
  const details = useFlights((s) => s.details);
  const allEvents = useFlights((s) => s.events);
  const docs = useFlights((s) => s.docs);
  const stage = useFlights((s) => s.stage);
  const at = useFlights((s) => s.at);
  const playing = useFlights((s) => s.playing);
  const speed = useFlights((s) => s.speed);
  const now = useFlights((s) => s.now);
  const designName = useStudio((s) => s.doc.name);

  useEffect(() => {
    void refreshRuns();
    const timer = setInterval(refreshRuns, 3000);
    return () => clearInterval(timer);
  }, []);

  useEffect(() => {
    if (!selected && runs.length > 0) selectRun((runs.find((r) => r.name === designName) ?? runs[0]).id);
  }, [runs, selected, designName]);

  const events = selected ? allEvents[selected] : undefined;
  const replay = useMemo(() => digest(events ?? []), [events]);
  const detail = selected ? details[selected] : undefined;
  const docEntry = selected ? docs[selected] : undefined;
  const doc = docEntry && typeof docEntry === "object" ? (docEntry as StudioDoc) : null;

  useEffect(() => {
    startClock(() => (replay.startedAt ? { start: replay.startedAt, end: replay.endedAt ?? Date.now() } : null));
    return stopClock;
  }, [replay]);

  useEffect(() => {
    canvasActions.answer = (questionId, value, text) => {
      if (!selected) return;
      api
        .answer(selected, questionId, value, text)
        .then(() => {
          toast("Answer sent.", "success", undefined, 1500);
          void loadDetail(selected);
        })
        .catch((e) => toast((e as Error).message, "error"));
    };
  }, [selected]);

  // Quantize "now" for the overlay so cards re-render a few times a second, not on every tick.
  const coarseNow = Math.floor(now / 500) * 500;
  const overlay = useMemo(
    () => (doc ? overlayAt(replay, { at, now: coarseNow, pending: detail?.pending_questions, allNodes: doc.nodes.map((n) => n.id) }) : null),
    [doc, replay, at, coarseNow, detail?.pending_questions],
  );

  const pendingTotal = runs.reduce((n, r) => n + (r.pending_questions ?? 0), 0);

  return (
    <div className="rs-flights">
      <aside className="rs-flights__list">
        <header className="rs-panel-head">
          <span className="label">Flights</span>
          {pendingTotal > 0 ? <span className="rs-attention">{pendingTotal} need you</span> : null}
        </header>
        <div className="rs-panel-scroll">
          {runs.length === 0 ? (
            <Empty title="No flights yet." icon="flights">
              Press <span className="mono">Run</span> in Design to launch the open pipeline.
            </Empty>
          ) : null}
          {runs.map((run) => (
            <button key={run.id} type="button" className={cx("rs-flight", run.id === selected && "is-selected")} onClick={() => selectRun(run.id)}>
              <span className={cx("rs-led", `is-${run.status === "running" ? "running" : run.status === "queued" ? "waiting" : run.status}`)} />
              <span className="rs-flight__body">
                <span className="rs-flight__name">
                  {run.name}
                  {run.simulated ? <span className="rs-flight__sim mono">sim</span> : null}
                </span>
                <span className="rs-flight__meta">
                  {run.status === "running" && run.current_node ? <span className="rs-flight__now">▸ {run.current_node}</span> : <span>{relative(run.created_at)}</span>}
                  {run.started_at && run.finished_at ? <span className="num"> · {formatDuration(Date.parse(run.finished_at) - Date.parse(run.started_at))}</span> : null}
                </span>
              </span>
              {run.pending_questions ? <span className="rs-flight__ask">{run.pending_questions}</span> : null}
            </button>
          ))}
        </div>
      </aside>

      <section className="rs-flights__main">
        <header className="rs-flights__bar">
          {detail ? (
            <>
              <StatusPill status={detail.status} />
              <span className="rs-flights__title">{detail.name}</span>
              {detail.current_node ? <span className="rs-flights__current">▸ {detail.current_node}</span> : null}
              <span className="rs-flights__spacer" />
              {detail.pending_questions.length > 0 ? (
                <span className="rs-attention">
                  <Glyph name="human" size={12} /> {detail.pending_questions.length} question{detail.pending_questions.length > 1 ? "s" : ""} waiting on the canvas
                </span>
              ) : null}
            </>
          ) : (
            <span className="faint">No flight selected</span>
          )}
        </header>
        <div className="rs-flights__canvas">
          {doc ? (
            <Canvas doc={doc} overlay={overlay} selectedNode={stage} onSelectNode={selectStage} />
          ) : docEntry === "loading" ? (
            <div className="rs-flights__center">
              <Spinner size={18} />
            </div>
          ) : selected ? (
            <Empty title="This run's graph isn't available here." icon="flights">
              It was started outside this browser. The flight log and stages are still on the right.
            </Empty>
          ) : null}
        </div>
        {selected && replay.startedAt ? (
          <Timeline
            replay={replay}
            doc={doc}
            now={now}
            at={at}
            playing={playing}
            speed={speed}
            selectedStage={stage}
            onScrub={scrubTo}
            onPlay={setPlaying}
            onSpeed={setSpeed}
            onSelectStage={selectStage}
          />
        ) : null}
      </section>

      <aside className="rs-flights__detail">
        <RunPanel detail={detail} replay={replay} doc={doc} stage={stage} onSelectStage={selectStage} />
      </aside>
    </div>
  );
}
