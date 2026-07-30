import { useEffect, useMemo } from "react";
import type { MetricStats, Telemetry, TelemetryNode } from "../../api/types";
import { formatDuration } from "../../canvas/Elapsed";
import { Button, Empty, Spinner, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { stripAnsi } from "../../model/ansi";
import { formatAiUnits, formatUsd, perVisitCost } from "../../model/cost";
import { findNode, kindOf, nodeLabel } from "../../model/doc";
import type { StudioDoc } from "../../model/types";
import { setMode, toast, useStudio } from "../../state/store";
import { completedVisits, failRate, loadTelemetry, pct, trouble, useTelemetry } from "../../state/telemetry";
import { applyCalibration } from "../tunnel/store";
import { cleanLandingRate, dirtyLandingRate, latestChange, trajectory, type TrajectoryRow } from "./record";
import { metricText } from "./replay";

const STATUS_TONE: Record<string, string> = {
  success: "var(--ok)",
  partial_success: "var(--warn)",
  fail: "var(--fail)",
  cancelled: "var(--ink-3)",
};

function OutcomeBar({ outcomes }: { outcomes: Record<string, number> }) {
  const total = Object.values(outcomes).reduce((a, b) => a + b, 0) || 1;
  return (
    <div className="rs-outbar" title={Object.entries(outcomes).map(([k, v]) => `${k}: ${v}`).join(" · ")}>
      {Object.entries(outcomes)
        .filter(([, v]) => v > 0)
        .map(([k, v]) => (
          <span key={k} style={{ width: `${(v / total) * 100}%`, background: STATUS_TONE[k] ?? "var(--ink-3)" }} />
        ))}
    </div>
  );
}

/** One bar per flight, oldest → newest: height = duration, color = outcome. */
function Series({ t, onSelect }: { t: Telemetry; onSelect?: (dir: string) => void }) {
  const runs = t.series;
  if (runs.length === 0) return null;
  const max = Math.max(1, ...runs.map((r) => r.duration_ms ?? 0));
  const slots = Math.max(runs.length, 12);
  const w = 100 / slots;
  // Newest flights sit at the right edge, under the "newest" label.
  const offset = slots - runs.length;
  return (
    <div className="rs-series">
      <svg viewBox="0 0 100 32" preserveAspectRatio="none">
        {runs.map((r, i) => {
          const h = 3 + Math.sqrt((r.duration_ms ?? 0) / max) * 27;
          return (
            <rect
              key={r.id}
              x={(offset + i) * w + w * 0.18}
              y={32 - h}
              width={w * 0.64}
              height={h}
              rx={0.6}
              fill={STATUS_TONE[r.status] ?? "var(--ink-3)"}
              opacity={0.85}
              className="rs-series__bar"
              onClick={() => onSelect?.(r.id)}
            >
              <title>{`${r.started_at ? new Date(r.started_at).toLocaleString() : r.id}\n${r.status}${r.duration_ms !== undefined ? ` · ${formatDuration(r.duration_ms)}` : ""}${r.cost_usd !== undefined ? ` · ${formatUsd(r.cost_usd)}` : r.ai_units !== undefined ? ` · ${formatAiUnits(r.ai_units)}` : ""}`}</title>
            </rect>
          );
        })}
      </svg>
      <div className="rs-series__axis faint">
        <span>oldest</span>
        <span>newest</span>
      </div>
    </div>
  );
}

function Big({ value, label, tone }: { value: string; label: string; tone?: string }) {
  return (
    <div className="rs-big">
      <span className="rs-big__value num" style={tone ? { color: tone } : undefined}>
        {value}
      </span>
      <span className="rs-big__label">{label}</span>
    </div>
  );
}

const SLOTS_MIN = 12;

/** One line of the trajectory: a value per flight (gaps where a flight has none), dots colored by outcome. */
function TrajectoryLine({ row, series, onSelect }: { row: TrajectoryRow; series: Telemetry["series"]; onSelect?: (dir: string) => void }) {
  const known = row.values.filter((v): v is number => v !== undefined);
  if (known.length === 0) return null;
  const min = Math.min(...known);
  const max = Math.max(...known);
  const span = max - min || 1;
  const slots = Math.max(series.length, SLOTS_MIN);
  const offset = slots - series.length;
  const x = (i: number) => ((offset + i + 0.5) / slots) * 100;
  const h = 26;
  const y = (v: number) => (max === min ? h / 2 : h - 4 - ((v - min) / span) * (h - 8));
  const segments: string[][] = [[]];
  row.values.forEach((v, i) => {
    if (v === undefined) segments.push([]);
    else segments[segments.length - 1].push(`${x(i)},${y(v)}`);
  });
  const { last, delta } = latestChange(row.values);
  return (
    <div className={cx("rs-trajectory__row", `is-${row.kind}`)}>
      <span className="rs-trajectory__label mono" title={row.kind === "metric" ? `metrics.${row.label}: its final value in each flight` : undefined}>
        {row.label}
      </span>
      <span className="rs-trajectory__plot">
        <svg viewBox={`0 0 100 ${h}`} preserveAspectRatio="none">
          {segments
            .filter((s) => s.length > 1)
            .map((s, i) => (
              <polyline key={i} points={s.join(" ")} vectorEffect="non-scaling-stroke" />
            ))}
        </svg>
        {row.values.map((v, i) =>
          v === undefined ? null : (
            <button
              key={series[i].id}
              type="button"
              className="rs-trajectory__dot"
              style={{ left: `${x(i)}%`, top: `${y(v)}px`, background: STATUS_TONE[series[i].status] ?? "var(--ink-3)" }}
              title={`${series[i].started_at ? new Date(series[i].started_at!).toLocaleString() : series[i].id}\n${row.label}: ${row.format(v)} · ${series[i].status}`}
              onClick={() => onSelect?.(series[i].id)}
            />
          ),
        )}
      </span>
      <span className="rs-trajectory__last num">
        {last !== undefined ? row.format(last) : ""}
        {delta ? <span className={cx("rs-trajectory__delta", delta > 0 ? "is-up" : "is-down")}>{delta > 0 ? "↑" : "↓"}</span> : null}
      </span>
    </div>
  );
}

/**
 * The quality trajectory: each outcome metric's final value flight by flight, next to the time and cost each flight
 * took — does the factory ship better products than it used to, and at what price?
 */
function Trajectory({ t, onSelect }: { t: Telemetry; onSelect?: (dir: string) => void }) {
  const rows = useMemo(() => trajectory(t), [t]);
  if (!rows.some((r) => r.kind === "metric")) return null;
  return (
    <div className="rs-trajectory">
      <div className="rs-trajectory__head">
        <span className="label">Quality trajectory</span>
        <span className="faint">final metrics per flight, with time and cost</span>
      </div>
      {rows.map((row) => (
        <TrajectoryLine key={row.key} row={row} series={t.series} onSelect={onSelect} />
      ))}
      <div className="rs-series__axis faint">
        <span>oldest</span>
        <span>newest</span>
      </div>
    </div>
  );
}

/** Statistics of each metric a stage reported over all its visits in these flights. */
function MetricStatsTable({ metrics }: { metrics: Record<string, MetricStats> }) {
  const names = Object.keys(metrics);
  if (names.length === 0) return null;
  return (
    <table className="rs-record__metrics">
      <thead>
        <tr>
          <th />
          <th>mean</th>
          <th>min</th>
          <th>max</th>
          <th>last</th>
          <th>n</th>
        </tr>
      </thead>
      <tbody>
        {names.map((name) => {
          const m = metrics[name];
          return (
            <tr key={name}>
              <td className="mono">{name}</td>
              <td className="num">{metricText(m.mean)}</td>
              <td className="num">{metricText(m.min)}</td>
              <td className="num">{metricText(m.max)}</td>
              <td className="num">{metricText(m.last)}</td>
              <td className="num faint">{m.count ?? m.runs ?? ""}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

function StageRecord({ doc, id, stats, flights }: { doc: StudioDoc | null; id: string; stats: TelemetryNode; flights: number }) {
  const node = doc ? findNode(doc, id) : undefined;
  const rate = failRate(stats);
  const models = Object.entries(stats.models ?? {}).sort((a, b) => b[1] - a[1]);
  return (
    <div className="rs-record__stage">
      <div className="rs-record__stage-head">
        {node && doc ? <Glyph name={kindOf(doc, node)} size={14} /> : null}
        <span className="rs-record__stage-name">{node && doc ? nodeLabel(doc, node) : id}</span>
        <span className="faint mono">{id}</span>
      </div>
      <div className="rs-record__bigs">
        <Big value={pct(1 - rate)} label="visits succeed" tone={rate >= 0.34 ? "var(--fail)" : rate > 0 ? "var(--warn)" : "var(--ok)"} />
        <Big value={stats.mean_visits.toFixed(stats.mean_visits < 10 ? 1 : 0)} label="visits per flight" />
        <Big value={pct(stats.runs / Math.max(1, flights))} label="of flights reach it" />
      </div>
      <OutcomeBar outcomes={stats.outcomes} />
      <dl className="rs-record__facts">
        {stats.duration_ms ? (
          <>
            <dt>time</dt>
            <dd className="num">
              p50 {formatDuration(stats.duration_ms.p50)} · p95 {formatDuration(stats.duration_ms.p95)}
              {stats.duration_ms.max !== undefined ? ` · max ${formatDuration(stats.duration_ms.max)}` : ""}
            </dd>
          </>
        ) : null}
        {stats.cost && Object.keys(stats.cost).length > 0 ? (
          <>
            <dt>per visit</dt>
            <dd className="num">{perVisitCost(stats.cost)}</dd>
          </>
        ) : null}
        {stats.retries > 0 ? (
          <>
            <dt>retries</dt>
            <dd className="num">{stats.retries}</dd>
          </>
        ) : null}
        {stats.failures_continued ? (
          <>
            <dt>flowed on</dt>
            <dd className="num" title="Failures that nothing routed: the flight went on along an unconditional edge as if the stage had succeeded">
              {stats.failures_continued} failure{stats.failures_continued === 1 ? "" : "s"}
              {stats.failed_stage_landings ? ` · ${stats.failed_stage_landings} landing${stats.failed_stage_landings === 1 ? "" : "s"} with it failed` : ""}
            </dd>
          </>
        ) : null}
        {stats.visit_limits ? (
          <>
            <dt>visit limit</dt>
            <dd className="num">reached {stats.visit_limits}×</dd>
          </>
        ) : null}
        {models.length > 0 ? (
          <>
            <dt>models</dt>
            <dd>
              {models.map(([m, n]) => (
                <span key={m} className="rs-chip">
                  {m} <span className="faint">×{n}</span>
                </span>
              ))}
            </dd>
          </>
        ) : null}
      </dl>
      {stats.metrics && Object.keys(stats.metrics).length > 0 ? (
        <div className="rs-record__stagemetrics">
          <div className="label">Metrics it reported</div>
          <MetricStatsTable metrics={stats.metrics} />
        </div>
      ) : null}
      {stats.top_failures && stats.top_failures.length > 0 ? (
        <div className="rs-record__reasons">
          <div className="label">Why it fails</div>
          {stats.top_failures.map((f) => (
            <div key={f.reason} className="rs-record__reason">
              <span className="num">×{f.count}</span>
              <span>{stripAnsi(f.reason)}</span>
            </div>
          ))}
        </div>
      ) : null}
    </div>
  );
}

/**
 * The flight record of a pipeline: how its real flights have gone (success, time, cost, trend), which stages break and
 * why, and a one-click calibration of the wind tunnel to these numbers.
 */
export function RecordPanel({
  name,
  doc,
  stage,
  onSelectStage,
  onSelectFlight,
}: {
  name: string;
  doc: StudioDoc | null;
  stage: string | null;
  onSelectStage: (id: string | null) => void;
  onSelectFlight?: (dir: string) => void;
}) {
  const t = useTelemetry((s) => s.byName[name]);
  const known = useTelemetry((s) => name in s.byName);
  const designName = useStudio((s) => s.doc.name);

  useEffect(() => {
    void loadTelemetry(name);
    const timer = setInterval(() => void loadTelemetry(name), 20000);
    return () => clearInterval(timer);
  }, [name]);

  if (!known) {
    return (
      <div className="rs-record__empty">
        <Spinner size={16} />
      </div>
    );
  }
  if (!t) {
    return (
      <Empty title="No finished flights yet." icon="gauge">
        The record fills in as real (non-simulated) flights of <span className="mono">{name}</span> land or fail.
      </Empty>
    );
  }
  const success = cleanLandingRate(t);
  const dirty = dirtyLandingRate(t);
  const worst = trouble(t).slice(0, 6);
  const label = (id: string) => {
    const node = doc ? findNode(doc, id) : undefined;
    return node && doc ? nodeLabel(doc, node) : id;
  };
  const selected = stage ? t.nodes[stage] : undefined;
  const cost = t.cost?.ai_units;
  const usd = t.cost?.cost_usd;
  return (
    <div className="rs-record">
      <header className="rs-record__head">
        <div className="label">
          <Glyph name="gauge" size={12} /> Flight record
        </div>
        <h2 className="rs-record__title">{name}</h2>
        <div className="faint">
          last {t.runs} finished flight{t.runs === 1 ? "" : "s"}
          {t.window ? ` · ${new Date(t.window.from).toLocaleDateString()} – ${new Date(t.window.to).toLocaleDateString()}` : ""}
        </div>
      </header>

      <div className="rs-record__bigs">
        <Big value={pct(success)} label={dirty > 0 ? "landed clean" : "landed"} tone={success >= 0.9 ? "var(--ok)" : success >= 0.6 ? "var(--warn)" : "var(--fail)"} />
        <Big value={t.duration_ms ? formatDuration(t.duration_ms.p50) : "—"} label={t.duration_ms ? `p50 · p95 ${formatDuration(t.duration_ms.p95)}` : "time"} />
        <Big
          value={usd ? formatUsd(usd.mean) : cost ? cost.mean.toFixed(cost.mean < 10 ? 1 : 0) : "—"}
          label={usd ? `per flight${cost ? ` · ${formatAiUnits(cost.mean)}` : ""}` : cost ? "AIU per flight" : "cost not reported"}
        />
      </div>
      <OutcomeBar outcomes={t.outcomes} />
      {dirty > 0 ? (
        <p className="rs-record__note is-warn" title="A stage failed, nothing routed the failure, and the flight went on along an unconditional edge as if it had succeeded (no later visit made it good)">
          <Glyph name="flag" size={11} /> {pct(dirty)} of flights landed with failed stages behind them.
        </p>
      ) : null}
      {t.budget_stops ? (
        <p className="rs-record__note">
          <Glyph name="stop" size={11} /> {t.budget_stops} flight{t.budget_stops === 1 ? "" : "s"} stopped at {t.budget_stops === 1 ? "its" : "their"} cost budget.
        </p>
      ) : null}
      <Series t={t} onSelect={onSelectFlight} />
      <Trajectory t={t} onSelect={onSelectFlight} />

      {selected && stage ? (
        <>
          <button type="button" className="rs-linkbtn rs-record__back" onClick={() => onSelectStage(null)}>
            ← whole pipeline
          </button>
          <StageRecord doc={doc} id={stage} stats={selected} flights={t.runs} />
        </>
      ) : (
        <div className="rs-record__trouble">
          <div className="label">Where it breaks</div>
          {worst.length === 0 ? <p className="faint">No stage has failed or retried in these flights.</p> : null}
          {worst.map((w) => (
            <button key={w.node} type="button" className="rs-trouble" onClick={() => onSelectStage(w.node)}>
              <span className="rs-trouble__heat" style={{ ["--heat" as string]: w.rate }} />
              <span className="rs-trouble__name">{label(w.node)}</span>
              <span className="rs-trouble__rate num">{pct(w.rate)} fail</span>
              <span className="rs-trouble__meta faint">
                {completedVisits(w.stats)} visits{w.stats.retries ? ` · ${w.stats.retries} retries` : ""}
                {w.stats.failures_continued ? ` · ${w.stats.failures_continued} flowed on` : ""}
              </span>
              {w.stats.top_failures?.[0] ? <span className="rs-trouble__why">“{stripAnsi(w.stats.top_failures[0].reason)}”</span> : null}
            </button>
          ))}
        </div>
      )}

      <div className={cx("rs-record__calibrate", designName !== name && "is-disabled")}>
        <p>
          Fit the wind tunnel to these flights, then try design changes there first: it predicts their success rate
          {cost ? " and cost" : ""} before you spend a token.
        </p>
        <Button
          icon="tunnel"
          disabled={designName !== name}
          title={designName !== name ? `Open ${name} in Design to calibrate its wind tunnel` : undefined}
          onClick={() => {
            applyCalibration(t);
            setMode("tunnel");
            toast(`Wind tunnel calibrated to ${t.runs} real flight${t.runs === 1 ? "" : "s"}.`, "success");
          }}
        >
          Calibrate wind tunnel
        </Button>
      </div>
    </div>
  );
}
