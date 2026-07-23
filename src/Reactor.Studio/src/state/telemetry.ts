import { create } from "zustand";
import { api } from "../api/client";
import type { Telemetry, TelemetryNode } from "../api/types";
import { edgeKey, type CanvasOverlay, type EdgeOverlay, type NodeOverlay } from "../canvas/build";
import { formatDuration } from "../canvas/Elapsed";
import type { StudioDoc } from "../model/types";
import { useStudio } from "./store";

/**
 * The flight record: aggregates over a pipeline's finished, non-simulated runs (`/studio/api/telemetry`), keyed by graph
 * name. Shared by the Flights record lens, the Design record lens, and the wind tunnel's calibration.
 */
interface TelemetryState {
  byName: Record<string, Telemetry | null>;
  fetchedAt: Record<string, number>;
  /** Design mode shows the record on the editable canvas. */
  designLens: boolean;
}

export const useTelemetry = create<TelemetryState>()(() => ({ byName: {}, fetchedAt: {}, designLens: false }));

const inflight = new Map<string, Promise<Telemetry | null>>();

export function telemetrySupported() {
  return useStudio.getState().info?.features.includes("telemetry") ?? false;
}

/** Loads (or refreshes, when older than `maxAgeMs`) the record of pipeline `name`; null when it has no finished flights. */
export async function loadTelemetry(name: string, maxAgeMs = 15000): Promise<Telemetry | null> {
  if (!name || !telemetrySupported()) return null;
  const state = useTelemetry.getState();
  if (name in state.byName && Date.now() - (state.fetchedAt[name] ?? 0) < maxAgeMs) return state.byName[name];
  const pending = inflight.get(name);
  if (pending) return pending;
  const request = api
    .telemetry(name)
    .then((t) => (t.runs > 0 ? t : null))
    .catch(() => null)
    .then((t) => {
      inflight.delete(name);
      const s = useTelemetry.getState();
      useTelemetry.setState({ byName: { ...s.byName, [name]: t }, fetchedAt: { ...s.fetchedAt, [name]: Date.now() } });
      return t;
    });
  inflight.set(name, request);
  return request;
}

export const setDesignLens = (on: boolean) => useTelemetry.setState({ designLens: on });

export const pct = (n: number, digits = 0) => `${(n * 100).toFixed(digits)}%`;

const sum = (values: number[]) => values.reduce((a, b) => a + b, 0);

export function completedVisits(node: TelemetryNode) {
  return sum(Object.values(node.outcomes));
}

export function failRate(node: TelemetryNode) {
  if (node.fail_rate !== undefined) return node.fail_rate;
  const done = completedVisits(node);
  return done ? (node.outcomes.fail ?? 0) / done : 0;
}

export function successRate(t: Telemetry) {
  const finished = sum(Object.values(t.outcomes));
  return finished ? ((t.outcomes.success ?? 0) + (t.outcomes.partial_success ?? 0)) / finished : 0;
}

/** Stages ranked by how much they hurt: failure share weighted by how often flights reach them. */
export function trouble(t: Telemetry): { node: string; stats: TelemetryNode; rate: number; score: number }[] {
  return Object.entries(t.nodes)
    .map(([node, stats]) => ({ node, stats, rate: failRate(stats), score: failRate(stats) * (stats.runs / Math.max(1, t.runs)) + stats.retries * 0.01 }))
    .filter((x) => x.rate > 0 || x.stats.retries > 0)
    .sort((a, b) => b.score - a.score);
}

/**
 * Canvas overlay for a flight record: every stage carries its reach, success share, typical duration, and cost; the heat
 * strip is its failure share; cable thickness is the share of flights that took it.
 */
export function telemetryOverlay(doc: StudioDoc, t: Telemetry, options: { dimUnvisited?: boolean } = {}): CanvasOverlay {
  const nodes: Record<string, NodeOverlay> = {};
  for (const node of doc.nodes) {
    const stats = t.nodes[node.id];
    if (!stats || stats.visits === 0) {
      if (options.dimUnvisited !== false) nodes[node.id] = { state: "dim" };
      continue;
    }
    const rate = failRate(stats);
    const done = completedVisits(stats);
    const readouts: NonNullable<NodeOverlay["readouts"]> = [];
    if (stats.runs < t.runs) readouts.push({ label: "reached", value: pct(stats.runs / t.runs) });
    if (done > 0) readouts.push({ label: "ok", value: pct(1 - rate), tone: rate >= 0.34 ? "fail" : rate > 0 ? "warn" : "ok" });
    if (stats.duration_ms) readouts.push({ label: "p50", value: formatDuration(stats.duration_ms.p50) });
    if (stats.cost?.ai_units !== undefined) readouts.push({ label: "AIU", value: stats.cost.ai_units.toFixed(stats.cost.ai_units < 10 ? 1 : 0) });
    else if (stats.cost?.cost_usd !== undefined) readouts.push({ label: "cost", value: `$${stats.cost.cost_usd.toFixed(2)}` });
    if (stats.retries > 0) readouts.push({ label: "retries", value: String(stats.retries), tone: "warn" });
    nodes[node.id] = {
      state: rate >= 0.34 ? "fail" : rate > 0 ? "partial_success" : "visited",
      badge: stats.mean_visits >= 1.05 ? `×${stats.mean_visits.toFixed(1)}` : undefined,
      heat: rate,
      heatLabel: `${pct(rate)} of ${stats.visits} visits failed, across ${stats.runs} of ${t.runs} flights`,
      readouts,
    };
  }
  const edges: Record<string, EdgeOverlay> = {};
  for (const edge of t.edges) {
    const share = edge.runs / Math.max(1, t.runs);
    const perRun = edge.count / Math.max(1, edge.runs);
    edges[edgeKey(edge.from, edge.to)] = { state: "taken", flow: share, text: `${pct(share)}${perRun >= 1.1 ? ` ·×${perRun.toFixed(1)}` : ""}` };
  }
  return { kind: "heat", nodes, edges };
}
