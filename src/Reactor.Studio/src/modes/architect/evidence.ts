import type { Telemetry } from "../../api/types";
import { edgeKey, type CanvasOverlay, type NodeOverlay } from "../../canvas/build";
import { formatDuration } from "../../canvas/Elapsed";
import { stripAnsi } from "../../model/ansi";
import type { DocDiff } from "../../model/diff";
import { findNode, nodeLabel } from "../../model/doc";
import type { StudioDoc } from "../../model/types";
import { completedVisits, failRate, pct, successRate } from "../../state/telemetry";
import { OUTCOME_KINDS, failedStageLandings, formatUsd, landing, type SimReport } from "../tunnel/scenario";

const label = (doc: StudioDoc, id: string) => {
  const node = findNode(doc, id);
  return node ? `\`${id}\` (${nodeLabel(doc, node)})` : `\`${id}\``;
};

/**
 * Evidence for the Architect, in Markdown: the pipeline's flight record (real flights) and the latest wind tunnel report.
 * Numbers only — the Architect is asked to tie every finding to them.
 */
export function buildEvidence(doc: StudioDoc, record: Telemetry | null, report: SimReport | null, scenario: string | null): string {
  const parts: string[] = [];
  if (record) {
    const finished = Object.values(record.outcomes).reduce((a, b) => a + b, 0);
    const lines = [`### Flight record: the last ${record.runs} real flights${record.window ? ` (${record.window.from.slice(0, 10)} to ${record.window.to.slice(0, 10)})` : ""}`];
    lines.push(
      `- Landed ${pct(successRate(record))} (${Object.entries(record.outcomes)
        .filter(([, n]) => n > 0)
        .map(([k, n]) => `${k} ${n}`)
        .join(", ")} of ${finished}).` +
        (record.duration_ms ? ` Duration p50 ${formatDuration(record.duration_ms.p50)}, p95 ${formatDuration(record.duration_ms.p95)}.` : "") +
        (record.cost?.ai_units ? ` Cost ${record.cost.ai_units.mean.toFixed(1)} AI units per flight.` : record.cost?.cost_usd ? ` Cost $${record.cost.cost_usd.mean.toFixed(2)} per flight.` : ""),
    );
    lines.push("- Stages (in pipeline order):");
    for (const node of doc.nodes) {
      const s = record.nodes[node.id];
      if (!s || s.visits === 0) continue;
      const bits = [`reached in ${pct(s.runs / Math.max(1, record.runs))} of flights`];
      if (s.mean_visits >= 1.05) bits.push(`×${s.mean_visits.toFixed(1)} visits per flight`);
      if (completedVisits(s) > 0) bits.push(`${pct(1 - failRate(s))} of ${completedVisits(s)} visits succeed`);
      if (s.retries > 0) bits.push(`${s.retries} retries`);
      if (s.duration_ms) bits.push(`p50 ${formatDuration(s.duration_ms.p50)}`);
      if (s.cost?.ai_units !== undefined) bits.push(`${s.cost.ai_units.toFixed(2)} AIU per visit`);
      const models = Object.keys(s.models ?? {});
      if (models.length > 0) bits.push(`model ${models.join("/")}`);
      let line = `  - ${label(doc, node.id)}: ${bits.join(", ")}.`;
      if (s.top_failures?.length) line += ` Fails with: ${s.top_failures.slice(0, 3).map((f) => `"${stripAnsi(f.reason)}" ×${f.count}`).join("; ")}.`;
      lines.push(line);
    }
    const loops = record.edges.filter((e) => e.count > e.runs).sort((a, b) => b.count / b.runs - a.count / a.runs);
    if (loops.length > 0) lines.push(`- Repeated routes: ${loops.slice(0, 5).map((e) => `${e.from} → ${e.to} ×${(e.count / e.runs).toFixed(1)} per flight that took it`).join("; ")}.`);
    parts.push(lines.join("\n"));
  }
  if (report) {
    const lines = [`### Wind tunnel: ${report.runs} simulated flights${scenario ? ` (${scenario})` : ""}`];
    const ready = landing(report);
    const dirtyStages = failedStageLandings(report);
    lines.push(
      `- Landed clean ${pct(ready.clean, 1)} (95% CI ${pct(ready.ci[0], 1)}–${pct(ready.ci[1], 1)}). ` +
        (ready.dirty > 0
          ? `Another ${pct(ready.dirtyShare, 1)} landed with failed stages behind them (a failure flowed on along an unconditional edge: ${dirtyStages
              .slice(0, 4)
              .map((s) => `\`${s.node}\` in ${pct(s.flights / report.runs, 1)}`)
              .join(", ")}). `
          : "") +
        OUTCOME_KINDS.filter((k) => (report.outcomes[k.key] ?? 0) > 0 && k.key !== "success")
          .map((k) => `${k.label}: ${pct((report.outcomes[k.key] ?? 0) / report.runs, 1)}`)
          .join(", ") +
        `${OUTCOME_KINDS.some((k) => k.key !== "success" && (report.outcomes[k.key] ?? 0) > 0) ? ". " : ""}Stage executions per flight: mean ${report.steps.mean.toFixed(1)}, max ${report.steps.max}.`,
    );
    const time = report.simulated_duration_ms;
    const cost = report.simulated_cost_usd;
    if ((time && time.max > 0) || (cost && cost.max > 0)) {
      const bits: string[] = [];
      if (time && time.max > 0) bits.push(`time mean ${formatDuration(time.mean)}, p90 ${formatDuration(time.p90)}, max ${formatDuration(time.max)}`);
      if (cost && cost.max > 0) bits.push(`cost ~${formatUsd(cost.mean)} mean, ${formatUsd(cost.p90)} p90`);
      lines.push(`- Simulated per flight: ${bits.join("; ")}.`);
    }
    for (const f of report.failures.slice(0, 4)) {
      const nodes = Object.entries(f.nodes ?? {});
      const where =
        nodes.length > 1 ? ` (last stages ${nodes.slice(0, 3).map(([id, n]) => `${id} ×${n}`).join(", ")})` : f.last_node && !f.reason.includes(f.last_node) ? ` (at ${f.last_node})` : "";
      lines.push(`- ${f.kind.replace("_", " ")} ${pct(f.count / report.runs, 1)}: ${f.reason}${where}`);
    }
    const hot = Object.entries(report.nodes)
      .filter(([, n]) => n.mean_visits >= 1.3)
      .sort((a, b) => b[1].mean_visits - a[1].mean_visits)
      .slice(0, 4);
    if (hot.length > 0) lines.push(`- Hot spots: ${hot.map(([id, n]) => `${id} ×${n.mean_visits.toFixed(1)}`).join(", ")}.`);
    const metrics = Object.entries(report.landed_metrics ?? {});
    if (metrics.length > 0) {
      lines.push(
        `- Metrics at landing: ${metrics
          .slice(0, 5)
          .map(([name, m]) => `${name} mean ${Math.round(m.mean * 100) / 100} (p10 ${m.p10}, p90 ${m.p90})`)
          .join("; ")}.`,
      );
    }
    parts.push(lines.join("\n"));
  }
  return parts.join("\n\n");
}

/** Canvas overlay for a proposal: new stages, changed stages (with what changed), and new routes. */
export function proposalOverlay(diff: DocDiff | null): CanvasOverlay {
  const nodes: Record<string, NodeOverlay> = {};
  const edges: CanvasOverlay["edges"] = {};
  if (!diff) return { kind: "replay", nodes, edges };
  for (const id of diff.added) nodes[id] = { state: "success", badge: "new" };
  for (const change of diff.changed) {
    nodes[change.id] = {
      state: "partial_success",
      badge: "changed",
      readouts: change.changes.slice(0, 4).map((c) => ({ label: c.key.replace(/_/g, " "), value: c.after === undefined ? "removed" : c.before === undefined ? "added" : "edited", tone: "warn" as const })),
    };
  }
  for (const e of diff.edgesAdded) edges[edgeKey(e.from, e.to)] = { state: "taken", text: "new" };
  for (const e of diff.edgesChanged) edges[edgeKey(e.from, e.to)] = { state: "taken", text: e.changes.map((c) => c.key).join(", ") };
  return { kind: "replay", nodes, edges };
}
