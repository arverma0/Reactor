import { useEffect } from "react";
import type { Telemetry, TelemetryNode } from "../../api/types";
import { formatDuration } from "../../canvas/Elapsed";
import { Button, Empty, Spinner, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { findNode, kindOf, nodeLabel } from "../../model/doc";
import type { StudioDoc } from "../../model/types";
import { setMode, toast, useStudio } from "../../state/store";
import { completedVisits, failRate, loadTelemetry, pct, successRate, trouble, useTelemetry } from "../../state/telemetry";
import { applyCalibration } from "../tunnel/store";

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
              <title>{`${r.started_at ? new Date(r.started_at).toLocaleString() : r.id}\n${r.status}${r.duration_ms !== undefined ? ` · ${formatDuration(r.duration_ms)}` : ""}${r.ai_units !== undefined ? ` · ${r.ai_units.toFixed(1)} AIU` : ""}`}</title>
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
            <dd className="num">
              {Object.entries(stats.cost)
                .map(([k, v]) => (k === "ai_units" ? `${v.toFixed(2)} AIU` : k === "cost_usd" ? `$${v.toFixed(3)}` : `${Math.round(v).toLocaleString()} ${k.replace(/_/g, " ")}`))
                .join(" · ")}
            </dd>
          </>
        ) : null}
        {stats.retries > 0 ? (
          <>
            <dt>retries</dt>
            <dd className="num">{stats.retries}</dd>
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
      {stats.top_failures && stats.top_failures.length > 0 ? (
        <div className="rs-record__reasons">
          <div className="label">Why it fails</div>
          {stats.top_failures.map((f) => (
            <div key={f.reason} className="rs-record__reason">
              <span className="num">×{f.count}</span>
              <span>{f.reason}</span>
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
  const success = successRate(t);
  const worst = trouble(t).slice(0, 6);
  const label = (id: string) => {
    const node = doc ? findNode(doc, id) : undefined;
    return node && doc ? nodeLabel(doc, node) : id;
  };
  const selected = stage ? t.nodes[stage] : undefined;
  const cost = t.cost?.ai_units;
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
        <Big value={pct(success)} label="landed" tone={success >= 0.9 ? "var(--ok)" : success >= 0.6 ? "var(--warn)" : "var(--fail)"} />
        <Big value={t.duration_ms ? formatDuration(t.duration_ms.p50) : "—"} label={t.duration_ms ? `p50 · p95 ${formatDuration(t.duration_ms.p95)}` : "time"} />
        <Big value={cost ? cost.mean.toFixed(cost.mean < 10 ? 1 : 0) : t.cost?.cost_usd ? `$${t.cost.cost_usd.mean.toFixed(2)}` : "—"} label={cost ? "AIU per flight" : t.cost?.cost_usd ? "per flight" : "cost not reported"} />
      </div>
      <OutcomeBar outcomes={t.outcomes} />
      <Series t={t} onSelect={onSelectFlight} />

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
              </span>
              {w.stats.top_failures?.[0] ? <span className="rs-trouble__why">“{w.stats.top_failures[0].reason}”</span> : null}
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
