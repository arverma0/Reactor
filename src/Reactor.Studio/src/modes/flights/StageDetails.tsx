import { useEffect, useState } from "react";
import { api } from "../../api/client";
import type { StageVisits } from "../../api/types";
import { cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { formatUsd } from "../../model/cost";
import { outcomeText, visitCostUsd, type AttemptRow, type StageMetricTable } from "./attempts";
import { metricText } from "./replay";

/** Loads the visits (with retried attempts) of a stage in a run directory; refreshes when `version` changes. */
export function useStageVisits(directory: string | null, node: string, version: string | number): StageVisits | null {
  const [visits, setVisits] = useState<StageVisits | null>(null);
  useEffect(() => {
    if (!directory) {
      setVisits(null);
      return;
    }
    let cancelled = false;
    api
      .stageVisits(directory, node)
      .then((v) => !cancelled && setVisits(v))
      .catch(() => !cancelled && setVisits(null));
    return () => {
      cancelled = true;
    };
  }, [directory, node, version]);
  return visits;
}

const TONE: Record<string, string> = { timeout: "fail", error: "fail", cancelled: "fail", fail: "fail", retry: "retry", partial_success: "partial_success", success: "success" };

/**
 * The attempts of the visit on show: each retried attempt (timed out, errored, failed, asked for a retry) with what it
 * cost, then the visit's last attempt. Picking one shows its files — a stopped attempt's last words included.
 */
export function AttemptStrip({ rows, shown, onPick }: { rows: AttemptRow[]; shown: number | null; onPick: (attempt: number | null) => void }) {
  if (rows.length < 2) return null;
  const total = visitCostUsd(rows);
  return (
    <div className="rs-attempts">
      <div className="rs-attempts__head">
        <span className="label">attempts</span>
        {total !== undefined ? <span className="faint num">{formatUsd(total)} for this visit</span> : null}
      </div>
      <div className="rs-attempts__list">
        {rows.map((r) => (
          <button
            key={r.number}
            type="button"
            className={cx("rs-attempt", shown === r.attempt && "is-on", r.final && "is-final")}
            title={[`Attempt ${r.number}${r.final ? " (the visit's last)" : ""}: ${outcomeText(r.outcome) || "running"}`, r.error, r.model ? `model ${r.model}` : null, r.lastWords ? "Has the agent's last words (response.partial.md)" : null].filter(Boolean).join("\n")}
            onClick={() => onPick(r.attempt)}
          >
            <span className={cx("rs-led", `is-${TONE[r.outcome ?? ""] ?? (r.final && !r.outcome ? "running" : "idle")}`)} />
            <span className="num">#{r.number}</span>
            <span className="rs-attempt__outcome">{outcomeText(r.outcome) || (r.final ? "last" : "")}</span>
            {r.costUsd !== undefined ? <span className="num faint">{formatUsd(r.costUsd)}</span> : null}
            {r.lastWords ? <Glyph name="note" size={10} /> : null}
          </button>
        ))}
      </div>
    </div>
  );
}

/** A tiny line of a metric over visits (values only; missing visits leave a gap). */
function Spark({ values }: { values: (number | undefined)[] }) {
  const known = values.filter((v): v is number => v !== undefined);
  if (known.length < 2) return null;
  const min = Math.min(...known);
  const max = Math.max(...known);
  const span = max - min || 1;
  const w = 56;
  const h = 16;
  const x = (i: number) => (values.length === 1 ? w / 2 : (i / (values.length - 1)) * (w - 4) + 2);
  const y = (v: number) => h - 2 - ((v - min) / span) * (h - 4);
  const points = values.map((v, i) => (v === undefined ? null : `${x(i)},${y(v)}`)).filter(Boolean);
  return (
    <svg className="rs-spark" width={w} height={h} viewBox={`0 0 ${w} ${h}`}>
      <polyline points={points.join(" ")} />
      {values.map((v, i) => (v === undefined ? null : <circle key={i} cx={x(i)} cy={y(v)} r={1.6} />))}
    </svg>
  );
}

/** A stage's outcome metrics per visit — the quality trajectory through a polish loop. */
export function StageMetrics({ table }: { table: StageMetricTable }) {
  if (table.names.length === 0) return null;
  return (
    <div className="rs-stagemetrics">
      <div className="label">metrics</div>
      <table>
        <thead>
          <tr>
            <th />
            {table.visits.map((v) => (
              <th key={v} className="num">
                v{v}
              </th>
            ))}
            {table.visits.length > 1 ? <th /> : null}
          </tr>
        </thead>
        <tbody>
          {table.names.map((name) => (
            <tr key={name}>
              <td className="mono">{name}</td>
              {table.values[name].map((v, i) => (
                <td key={i} className="num">
                  {v === undefined ? <span className="faint">—</span> : metricText(v)}
                </td>
              ))}
              {table.visits.length > 1 ? (
                <td>
                  <Spark values={table.values[name]} />
                </td>
              ) : null}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
