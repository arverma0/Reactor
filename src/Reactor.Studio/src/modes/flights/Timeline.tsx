import { useMemo, useRef } from "react";
import { Button, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import type { StudioDoc } from "../../model/types";
import { findNode, kindOf, nodeLabel } from "../../model/doc";
import { formatDuration } from "../../canvas/Elapsed";
import { timelineRows, type RunReplay } from "./replay";

function niceStep(span: number) {
  const steps = [1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000, 600000, 900000, 1800000, 3600000, 7200000];
  return steps.find((s) => span / s <= 8) ?? 14400000;
}

const clock = (ms: number) => {
  const s = Math.max(0, Math.round(ms / 1000));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  return h ? `${h}:${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}` : `${m}:${String(sec).padStart(2, "0")}`;
};

/**
 * The flight recorder: one row per stage, one bar per execution, a playhead you can drag to replay the run on the canvas.
 */
export function Timeline({
  replay,
  doc,
  now,
  at,
  playing,
  speed,
  selectedStage,
  onScrub,
  onPlay,
  onSpeed,
  onSelectStage,
}: {
  replay: RunReplay;
  doc: StudioDoc | null;
  now: number;
  at: number | null;
  playing: boolean;
  speed: number;
  selectedStage: string | null;
  onScrub: (at: number | null) => void;
  onPlay: (playing: boolean) => void;
  onSpeed: (speed: number) => void;
  onSelectStage: (id: string) => void;
}) {
  const track = useRef<HTMLDivElement>(null);
  const rows = useMemo(() => timelineRows(replay), [replay]);
  const start = replay.startedAt ?? now;
  const end = Math.max(replay.endedAt ?? now, start + 1000);
  const span = end - start;
  const step = niceStep(span);
  const ticks: number[] = [];
  for (let t = 0; t <= span; t += step) ticks.push(t);
  const pct = (t: number) => `${((t - start) / span) * 100}%`;
  const head = at ?? (replay.endedAt ?? now);

  const scrubFrom = (clientX: number) => {
    const rect = track.current?.getBoundingClientRect();
    if (!rect) return;
    const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    const t = start + ratio * span;
    onScrub(ratio >= 0.999 && replay.endedAt === null ? null : t);
  };

  const onDown = (e: React.MouseEvent) => {
    scrubFrom(e.clientX);
    const move = (ev: MouseEvent) => scrubFrom(ev.clientX);
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  };

  return (
    <section className="rs-timeline">
      <header className="rs-timeline__bar">
        <Button
          size="sm"
          variant="ghost"
          icon={playing ? "pause" : "play"}
          title={playing ? "Pause" : "Replay from the playhead"}
          onClick={() => {
            if (playing) onPlay(false);
            else {
              if (at === null || at >= end - 50) onScrub(start);
              onPlay(true);
            }
          }}
        />
        <div className="rs-timeline__speeds">
          {[1, 8, 30, 120].map((s) => (
            <button key={s} type="button" className={cx("rs-timeline__speed", speed === s && "is-on")} onClick={() => onSpeed(s)}>
              {s}×
            </button>
          ))}
        </div>
        <span className="rs-timeline__clock num">
          {clock(head - start)} <span className="faint">/ {clock(span)}</span>
        </span>
        <span className="rs-timeline__spacer" />
        {at !== null ? (
          <button type="button" className="rs-timeline__live" onClick={() => onScrub(null)}>
            <span className="rs-led is-running" /> {replay.endedAt === null ? "Back to live" : "Show final state"}
          </button>
        ) : replay.endedAt === null ? (
          <span className="rs-timeline__livebadge">
            <span className="rs-led is-running" /> LIVE
          </span>
        ) : (
          <span className="rs-timeline__done faint">recorded · {replay.executions.length} stage executions</span>
        )}
      </header>
      <div className="rs-timeline__body">
        <div className="rs-timeline__labels">
          <div className="rs-timeline__axis-spacer" />
          {rows.map((row) => {
            const node = doc ? findNode(doc, row.node) : undefined;
            return (
              <button key={row.node} type="button" className={cx("rs-timeline__label", selectedStage === row.node && "is-selected")} onClick={() => onSelectStage(row.node)} title={row.node}>
                {node && doc ? <Glyph name={kindOf(doc, node)} size={11} /> : null}
                <span>{node && doc ? nodeLabel(doc, node) : row.node}</span>
              </button>
            );
          })}
        </div>
        <div className="rs-timeline__track" ref={track} onMouseDown={onDown}>
          <div className="rs-timeline__axis">
            {ticks.map((t) => (
              <span key={t} className="rs-timeline__tick num" style={{ left: `${(t / span) * 100}%` }}>
                {clock(t)}
              </span>
            ))}
          </div>
          {ticks.map((t) => (
            <span key={`g${t}`} className="rs-timeline__grid" style={{ left: `${(t / span) * 100}%` }} />
          ))}
          {rows.map((row) => (
            <div key={row.node} className={cx("rs-timeline__row", selectedStage === row.node && "is-selected")}>
              {row.executions.map((exec, i) => {
                const stop = exec.end ?? (replay.endedAt ?? now);
                const width = Math.max(0.25, ((stop - exec.start) / span) * 100);
                const future = exec.start > head;
                const waiting = exec.end === undefined && replay.questions.some((q) => q.node === row.node && q.asked >= exec.start && q.answered === undefined);
                const state = exec.end === undefined ? (replay.endedAt === null ? (waiting ? "waiting" : "running") : "fail") : exec.status ?? "success";
                return (
                  <span
                    key={i}
                    className={cx("rs-timeline__exec", `is-${state}`, future && "is-future")}
                    style={{ left: pct(exec.start), width: `${width}%` }}
                    title={`${row.node} · attempt ${exec.attempt} · ${exec.status ?? "running"} · ${formatDuration(stop - exec.start)}${exec.error ? `\n${exec.error}` : ""}`}
                  />
                );
              })}
              {replay.questions
                .filter((q) => q.node === row.node)
                .map((q) => (
                  <span key={q.id} className="rs-timeline__wait" style={{ left: pct(q.asked), width: `${Math.max(0.25, (((q.answered ?? now) - q.asked) / span) * 100)}%` }} title={`waiting for a person · ${q.answer ?? "pending"}`} />
                ))}
            </div>
          ))}
          {replay.traversals.length > 0 ? (
            <div className="rs-timeline__marks">
              {replay.traversals.map((tr, i) => (
                <span key={i} className="rs-timeline__mark" style={{ left: pct(tr.at) }} />
              ))}
            </div>
          ) : null}
          <span className={cx("rs-timeline__head", at === null && replay.endedAt === null && "is-live")} style={{ left: pct(head) }} />
        </div>
      </div>
    </section>
  );
}
