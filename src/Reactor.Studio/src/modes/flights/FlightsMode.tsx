import { useEffect, useMemo } from "react";
import { api } from "../../api/client";
import { canvasActions } from "../../canvas/actions";
import { Canvas } from "../../canvas/Canvas";
import { formatDuration } from "../../canvas/Elapsed";
import { Empty, Segmented, Spinner, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { formatUsd } from "../../model/cost";
import type { StudioDoc } from "../../model/types";
import { toast, useStudio } from "../../state/store";
import { loadTelemetry, telemetryOverlay, telemetrySupported, useTelemetry } from "../../state/telemetry";
import { RecordPanel } from "./RecordPanel";
import { digest, overlayAt } from "./replay";
import { RunPanel, StatusPill, budgetOf } from "./RunPanel";
import { loadDetail, refreshRuns, scrubTo, selectRun, selectStage, setFilter, setLens, setPlaying, setSpeed, startClock, stopClock, useFlights, type Flight } from "./store";
import { Timeline } from "./Timeline";
import { costText, flightView } from "./view";

const relative = (iso?: string | null) => {
  if (!iso) return "";
  const s = (Date.now() - Date.parse(iso)) / 1000;
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.round(s / 60)}m ago`;
  if (s < 86400) return `${Math.round(s / 3600)}h ago`;
  return new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
};

function dayLabel(iso: string | null): string {
  if (!iso) return "Waiting";
  const day = new Date(iso);
  const today = new Date();
  const start = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const diff = Math.round((start(today) - start(day)) / 86400000);
  if (diff <= 0) return "Today";
  if (diff === 1) return "Yesterday";
  return day.toLocaleDateString([], { weekday: diff < 7 ? "long" : undefined, month: "short", day: "numeric" });
}

const LED: Record<string, string> = { running: "running", queued: "waiting", interrupted: "partial_success", error: "fail" };

function FlightRow({ flight, selected }: { flight: Flight; selected: boolean }) {
  const cost = costText(flight.cost);
  const dirty = (flight.status === "success" || flight.status === "partial_success") && flight.failedStages.length > 0;
  return (
    <button type="button" className={cx("rs-flight", selected && "is-selected")} onClick={() => selectRun(flight.key)}>
      <span className={cx("rs-led", `is-${flight.budget ? "stopped" : (LED[flight.status] ?? flight.status)}`)} title={flight.budget ? "stopped at its budget" : flight.status} />
      <span className="rs-flight__body">
        <span className="rs-flight__name">
          {flight.name}
          {flight.simulated ? <span className="rs-flight__sim mono">sim</span> : null}
          {flight.forkedFrom ? (
            <span className="rs-flight__fork" title={`Re-flown from ${flight.forkedFrom.id} after ${flight.forkedFrom.checkpoint}`}>
              <Glyph name="fork" size={10} />
            </span>
          ) : null}
          {flight.budget ? (
            <span className="rs-flight__tag is-budget" title={`Stopped at its budget: ${formatUsd(flight.budget.spent_usd)} of ${formatUsd(flight.budget.limit_usd)}${flight.budget.next_node ? `, before ${flight.budget.next_node}` : ""}. Resume it with a higher budget.`}>
              budget
            </span>
          ) : null}
          {dirty ? (
            <span className="rs-flight__tag is-dirty" title={`Landed with failed stages behind it: ${flight.failedStages.join(", ")} (a failure flowed on along an unconditional edge)`}>
              failed stage{flight.failedStages.length === 1 ? "" : "s"}
            </span>
          ) : null}
        </span>
        <span className="rs-flight__meta">
          {flight.status === "running" && flight.currentNode ? <span className="rs-flight__now">▸ {flight.currentNode}</span> : <span>{relative(flight.startedAt ?? flight.createdAt)}</span>}
          {flight.durationMs != null && flight.status !== "running" ? <span className="num"> · {formatDuration(flight.durationMs)}</span> : null}
          {cost ? <span className="num"> · {cost}</span> : null}
        </span>
      </span>
      {flight.pending ? <span className="rs-flight__ask">{flight.pending}</span> : null}
    </button>
  );
}

export function FlightsMode() {
  const flights = useFlights((s) => s.flights);
  const loaded = useFlights((s) => s.loaded);
  const filter = useFlights((s) => s.filter);
  const selected = useFlights((s) => s.selected);
  const live = useFlights((s) => s.live);
  const history = useFlights((s) => s.history);
  const allEvents = useFlights((s) => s.events);
  const docs = useFlights((s) => s.docs);
  const lens = useFlights((s) => s.lens);
  const stage = useFlights((s) => s.stage);
  const at = useFlights((s) => s.at);
  const playing = useFlights((s) => s.playing);
  const speed = useFlights((s) => s.speed);
  const now = useFlights((s) => s.now);
  const designName = useStudio((s) => s.doc.name);
  const design = useStudio((s) => s.doc);
  const connected = useStudio((s) => s.connected);

  useEffect(() => {
    void refreshRuns();
    const timer = setInterval(refreshRuns, 3000);
    return () => clearInterval(timer);
  }, [connected]);

  const visible = useMemo(() => (filter === "pipeline" ? flights.filter((f) => f.name === designName) : flights), [flights, filter, designName]);

  useEffect(() => {
    if (!selected && visible.length > 0) selectRun((visible.find((f) => f.name === designName) ?? visible[0]).key);
  }, [visible, selected, designName]);

  const flight = selected ? flights.find((f) => f.key === selected) : undefined;
  const liveId = flight?.liveId ?? (selected && !selected.startsWith("dir:") ? selected : null);
  const dir = flight?.dir ?? (selected?.startsWith("dir:") ? selected.slice(4) : null);
  const view = useMemo(
    () => (selected ? flightView(selected, flight, liveId ? live[liveId] : undefined, dir ? history[dir] : undefined) : null),
    [selected, flight, liveId, live, dir, history],
  );
  const active = view?.active ?? false;
  const hasView = !!view;
  const events = selected ? allEvents[selected] : undefined;
  const replay = useMemo(() => {
    const r = digest(events ?? []);
    // A run that stopped without a final event (a killed process) ends at its last event.
    if (r.endedAt === null && !active && events && events.length > 0 && hasView) {
      r.endedAt = Date.parse(events[events.length - 1].timestamp);
      r.outcome = "fail";
    }
    return r;
  }, [events, active, hasView]);
  const docEntry = selected ? docs[selected] : undefined;
  const runDoc = docEntry && typeof docEntry === "object" ? (docEntry as StudioDoc) : null;

  const recordName = view?.name ?? designName;
  const telemetry = useTelemetry((s) => s.byName[recordName]);
  useEffect(() => {
    if (recordName) void loadTelemetry(recordName);
  }, [recordName, flights.length]);
  const recordDoc = runDoc ?? (recordName === designName ? design : null);

  useEffect(() => {
    startClock(() => (replay.startedAt ? { start: replay.startedAt, end: replay.endedAt ?? Date.now() } : null));
    return stopClock;
  }, [replay]);

  useEffect(() => {
    canvasActions.answer = (questionId, value, text) => {
      if (!liveId) return;
      api
        .answer(liveId, questionId, value, text)
        .then(() => {
          toast("Answer sent.", "success", undefined, 1500);
          if (selected) loadDetail(selected);
        })
        .catch((e) => toast((e as Error).message, "error"));
    };
  }, [liveId, selected]);

  // Quantize "now" for the overlay so cards re-render a few times a second, not on every tick.
  const coarseNow = Math.floor(now / 500) * 500;
  const flightOverlay = useMemo(() => {
    if (!runDoc) return null;
    const overlay = overlayAt(replay, { at, now: coarseNow, pending: view?.pending, allNodes: runDoc.nodes.map((n) => n.id) });
    // A re-flown flight starts from a snapshot: stages completed before it were restored, not flown again.
    const restored = view?.forkedFrom && dir ? history[dir]?.checkpoint?.completed_nodes : undefined;
    if (restored) {
      const flown = new Set(replay.executions.map((x) => x.node));
      for (const node of restored) if (!flown.has(node) && overlay.nodes[node]) overlay.nodes[node] = { state: "restored", badge: "restored" };
    }
    return overlay;
  }, [runDoc, replay, at, coarseNow, view?.pending, view?.forkedFrom, dir, history]);
  const recordOverlay = useMemo(() => (recordDoc && telemetry ? telemetryOverlay(recordDoc, telemetry) : null), [recordDoc, telemetry]);
  const showRecord = lens === "record";
  const canvasDoc = showRecord ? recordDoc : runDoc;

  const pendingTotal = flights.reduce((n, f) => n + (f.pending ?? 0), 0);
  const groups = useMemo(() => {
    const out: { day: string; flights: Flight[] }[] = [];
    for (const f of visible) {
      const day = dayLabel(f.startedAt ?? f.createdAt);
      if (out.length === 0 || out[out.length - 1].day !== day) out.push({ day, flights: [] });
      out[out.length - 1].flights.push(f);
    }
    return out;
  }, [visible]);

  return (
    <div className="rs-flights">
      <aside className="rs-flights__list">
        <header className="rs-panel-head">
          <span className="label">Flights</span>
          {pendingTotal > 0 ? <span className="rs-attention">{pendingTotal} need you</span> : null}
        </header>
        <div className="rs-flights__filter">
          <Segmented
            size="sm"
            value={filter}
            onChange={setFilter}
            options={[
              { value: "pipeline", label: designName, title: "Flights of the pipeline open in Design" },
              { value: "all", label: "All" },
            ]}
          />
        </div>
        <div className="rs-panel-scroll">
          {!loaded ? (
            <div className="rs-flights__center">
              <Spinner />
            </div>
          ) : visible.length === 0 ? (
            <Empty title={filter === "pipeline" && flights.length > 0 ? `No flights of ${designName} yet.` : "No flights yet."} icon="flights">
              Press <span className="mono">Run</span> to launch the open pipeline.
            </Empty>
          ) : null}
          {groups.map((g) => (
            <div key={g.day} className="rs-flights__day">
              <div className="rs-flights__dayhead label">{g.day}</div>
              {g.flights.map((f) => (
                <FlightRow key={f.key} flight={f} selected={f.key === selected} />
              ))}
            </div>
          ))}
        </div>
      </aside>

      <section className="rs-flights__main">
        <header className="rs-flights__bar">
          {showRecord ? (
            <>
              <Glyph name="gauge" size={14} />
              <span className="rs-flights__title">{recordName}</span>
              <span className="faint">flight record{telemetry ? ` · ${telemetry.runs} finished flight${telemetry.runs === 1 ? "" : "s"}` : ""}</span>
            </>
          ) : view ? (
            <>
              <StatusPill status={view.status} budget={!!budgetOf(view, replay)} />
              <span className="rs-flights__title">{view.name}</span>
              {view.currentNode && view.active ? <span className="rs-flights__current">▸ {view.currentNode}</span> : null}
            </>
          ) : (
            <span className="faint">No flight selected</span>
          )}
          <span className="rs-flights__spacer" />
          {!showRecord && view && view.pending.length > 0 ? (
            <span className="rs-attention">
              <Glyph name="human" size={12} /> {view.pending.length} question{view.pending.length > 1 ? "s" : ""} waiting on the canvas
            </span>
          ) : null}
          {telemetrySupported() ? (
            <Segmented
              size="sm"
              value={lens}
              onChange={(v) => {
                setLens(v);
                selectStage(null);
              }}
              options={[
                { value: "flight", label: "This flight", title: "Replay the selected flight" },
                { value: "record", label: <>Record{telemetry ? <span className="faint num"> · {telemetry.runs}</span> : null}</>, title: "How this pipeline flies: every finished flight, aggregated" },
              ]}
            />
          ) : null}
        </header>
        <div className="rs-flights__canvas">
          {canvasDoc ? (
            <Canvas doc={canvasDoc} overlay={showRecord ? recordOverlay : flightOverlay} selectedNode={stage} onSelectNode={selectStage} />
          ) : docEntry === "loading" ? (
            <div className="rs-flights__center">
              <Spinner size={18} />
            </div>
          ) : selected ? (
            <Empty title="This run's graph isn't available here." icon="flights">
              Its directory has no <span className="mono">graph.dot</span>. The flight log and stages are still on the right.
            </Empty>
          ) : null}
        </div>
        {!showRecord && selected && replay.startedAt ? (
          <Timeline
            replay={replay}
            doc={runDoc}
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
        {showRecord ? (
          <RecordPanel
            name={recordName}
            doc={recordDoc}
            stage={stage}
            onSelectStage={selectStage}
            onSelectFlight={(id) => {
              const target = flights.find((f) => f.dir === id);
              if (!target) return;
              setLens("flight");
              selectRun(target.key);
            }}
          />
        ) : (
          <RunPanel view={view} replay={replay} doc={runDoc} stage={stage} onSelectStage={selectStage} />
        )}
      </aside>
    </div>
  );
}
