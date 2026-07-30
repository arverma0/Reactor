import type { Telemetry } from "../../api/types";
import { handlerOf, nodeAttr, outgoing } from "../../model/doc";
import { routeKeyOf } from "../../model/routes";
import type { DocNode, StudioDoc } from "../../model/types";
import presetCatalog from "./presets.json";

/** A metric's distribution: a constant, a uniform range (whole numbers when both bounds are whole, unless `integer: false`), or weighted values. */
export type MetricDistribution = number | { min: number; max: number; integer?: boolean } | { values: Record<string, number> | number[] };

/** A metric's distribution, or one per resulting status (`success`, `fail`, …, `*` for every other status). */
export type MetricSpec = MetricDistribution | Record<string, MetricDistribution>;

/** One stage's simulated behavior (wire format of the server's scenario). */
export interface Behavior {
  outcomes?: Record<string, number>;
  script?: string[];
  labels?: Record<string, number>;
  choices?: Record<string, number>;
  context_updates?: Record<string, Record<string, unknown>>;
  duration_ms?: number;
  /** Outcome metrics drawn per execution (recorded as `metrics.<name>`, routable with conditions). */
  metrics?: Record<string, MetricSpec>;
  /** Cost of every execution, in USD. */
  cost?: number;
}

export interface Scenario {
  seed?: number;
  step_delay_ms?: number;
  /** Typical duration per handler type for stages without `duration_ms` (never longer than a stage's timeout). */
  duration_priors?: Record<string, number>;
  defaults?: Record<string, Behavior>;
  nodes?: Record<string, Behavior>;
}

export type Preset = "happy" | "realistic" | "stress" | "chaos" | "calibrated" | "custom";

export interface Calibration {
  /** Finished real flights the scenario was fitted to. */
  runs: number;
  at: string;
  window?: { from: string; to: string };
  /** Real success share over those flights (to compare with the tunnel's prediction). */
  successRate: number;
  /** Mean cost per visit, per stage (AI units), for cost predictions. */
  costPerVisit: Record<string, number>;
  /** Real mean AI units per flight. */
  meanCost?: number;
  /** Real mean USD per flight (when the record has `cost_usd`). */
  meanCostUsd?: number;
  /** Real mean flight duration (ms). */
  meanDurationMs?: number;
}

export interface TunnelSettings {
  preset: Preset;
  /** Share of agent executions that succeed. */
  agentSuccess: number;
  /** Share of agent executions that ask for a retry (the rest fail). */
  agentRetry: number;
  /** Share of tool commands that pass. */
  toolSuccess: number;
  /** Typical duration per handler type (ms), for time forecasts of stages without their own duration. */
  durationPriors?: Record<string, number>;
  nodes: Record<string, Behavior>;
  runs: number;
  maxSteps: number;
  seed: number | null;
  calibration?: Calibration | null;
}

type PresetName = Exclude<Preset, "custom" | "calibrated">;

interface PresetCatalog {
  duration_priors: Record<string, number>;
  presets: { name: string; label: string; blurb: string; agent_success: number; agent_retry: number; tool_success: number; scenario: Scenario }[];
}

/** The server's preset catalog (`SimulationPreset.CatalogJson()`; a server test keeps this bundled copy in sync). */
export const PRESET_CATALOG = presetCatalog as PresetCatalog;

export const PRESETS = Object.fromEntries(
  PRESET_CATALOG.presets.map((p) => [p.name, { label: p.label, blurb: p.blurb, agentSuccess: p.agent_success, agentRetry: p.agent_retry, toolSuccess: p.tool_success }]),
) as Record<PresetName, { label: string; blurb: string; agentSuccess: number; agentRetry: number; toolSuccess: number }>;

/** Rough typical stage durations by handler type (ms): what a forecast assumes until calibrated to real flights. */
export const DURATION_PRIORS: Readonly<Record<string, number>> = PRESET_CATALOG.duration_priors;

export const defaultSettings = (): TunnelSettings => ({ preset: "realistic", ...pick(PRESETS.realistic), durationPriors: { ...DURATION_PRIORS }, nodes: {}, runs: 500, maxSteps: 200, seed: null });

function pick(p: { agentSuccess: number; agentRetry: number; toolSuccess: number }) {
  return { agentSuccess: p.agentSuccess, agentRetry: p.agentRetry, toolSuccess: p.toolSuccess };
}

const round = (n: number) => Math.round(n * 1000) / 1000;

/** Largest `duration_ms` the server accepts (24 hours; a stage's own timeout still applies). */
export const MAX_DURATION_MS = 86_400_000;

/** Largest `cost` per execution the server accepts (USD). */
export const MAX_COST_USD = 10_000;

/** Handler types that always run for real in a simulation (scenario entries for them have no effect). */
const REAL_HANDLERS = new Set(["start", "exit", "conditional", "parallel", "parallel.fan_in"]);

/** The scenario without stage durations or priors, for managed simulated runs, which wait durations out in real time. */
export function withoutDurations(scenario: Scenario): Scenario {
  const strip = (behaviors: Record<string, Behavior> | undefined) => {
    if (!behaviors) return behaviors;
    const out: Record<string, Behavior> = {};
    for (const [key, behavior] of Object.entries(behaviors)) {
      const copy = { ...behavior };
      delete copy.duration_ms;
      if (Object.keys(copy).length > 0) out[key] = copy;
    }
    return out;
  };
  const copy: Scenario = { ...scenario, defaults: strip(scenario.defaults), nodes: strip(scenario.nodes) };
  delete copy.duration_priors;
  return copy;
}

/** Labels a stage can report as its preferred route (label-only edges out of an agent stage). */
export function routeLabels(doc: StudioDoc, node: DocNode): string[] {
  const handler = handlerOf(doc, node);
  if (handler !== "codergen" && handler !== "tool") return [];
  return [...new Set(outgoing(doc, node.id).filter((e) => routeKeyOf(doc, e, handler).startsWith("label:")).map((e) => e.attrs.label!))];
}

/** Gate options (edge labels or targets) of a human gate. */
export function gateChoices(doc: StudioDoc, node: DocNode): string[] {
  if (handlerOf(doc, node) !== "wait.human") return [];
  return outgoing(doc, node.id).map((e) => e.attrs.label || e.to);
}

/** Builds the wire scenario. Agent stages with labeled routes default to choosing among them evenly. */
export function buildScenario(doc: StudioDoc, settings: TunnelSettings, extra: Partial<Scenario> = {}): Scenario {
  const fail = Math.max(0, 1 - settings.agentSuccess - settings.agentRetry);
  const nodes: Record<string, Behavior> = {};
  for (const node of doc.nodes) {
    const override = settings.nodes[node.id];
    const labels = routeLabels(doc, node);
    const behavior: Behavior = { ...(override ?? {}) };
    if (labels.length > 0 && !behavior.labels) behavior.labels = Object.fromEntries(labels.map((l) => [l, round(1 / labels.length)]));
    if (Object.keys(behavior).length > 0) nodes[node.id] = behavior;
    void nodeAttr;
  }
  const priors = settings.durationPriors ?? DURATION_PRIORS;
  return {
    seed: settings.seed ?? undefined,
    ...(Object.keys(priors).length > 0 ? { duration_priors: { ...priors } } : {}),
    defaults: {
      codergen: { outcomes: { success: round(settings.agentSuccess), retry: round(settings.agentRetry), fail: round(fail) } },
      tool: { outcomes: { success: round(settings.toolSuccess), fail: round(1 - settings.toolSuccess) } },
      "stack.manager_loop": { outcomes: { success: round(settings.agentSuccess), fail: round(1 - settings.agentSuccess) } },
    },
    nodes,
    ...extra,
  };
}

// ---------- outcome metrics (scenario editor syntax) ----------

const STATUS_KEYS = new Set(["success", "partial_success", "retry", "fail", "skipped", "*"]);

const isNumber = (text: string) => /^[-+]?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d+)?$/.test(text.trim());
const hasFraction = (text: string) => /[.eE]/.test(text.trim());

function parseDistribution(text: string): MetricDistribution | null {
  const t = text.trim();
  if (isNumber(t)) return Number(t);
  const range = /^(\S+)\s*\.\.\s*(\S+)$/.exec(t);
  if (range && isNumber(range[1]) && isNumber(range[2])) {
    const min = Number(range[1]);
    const max = Number(range[2]);
    if (min > max) return null;
    const whole = Number.isInteger(min) && Number.isInteger(max);
    // A fraction anywhere ("0.0..1.0") asks for a continuous range even over whole bounds.
    return whole && (hasFraction(range[1]) || hasFraction(range[2])) ? { min, max, integer: false } : { min, max };
  }
  const parts = t.split(",").map((p) => p.trim()).filter(Boolean);
  if (parts.length === 0) return null;
  if (parts.every(isNumber)) return { values: parts.map(Number) };
  const values: Record<string, number> = {};
  for (const part of parts) {
    const m = /^(\S+)\s*:\s*(\S+)$/.exec(part);
    if (!m || !isNumber(m[1]) || !isNumber(m[2]) || Number(m[2]) < 0) return null;
    const key = String(Number(m[1]));
    values[key] = (values[key] ?? 0) + Number(m[2]);
  }
  return Object.values(values).some((w) => w > 0) ? { values } : null;
}

/**
 * Parses the scenario editor's metric syntax: `4` (always), `1..5` (uniform whole numbers; `0.0..1.0` for a continuous
 * range), `3, 4, 5` (equally likely), `3:0.2, 4:0.5, 5:0.3` (weighted), or per status `success: 0; fail: 1..5`
 * (`*` for every other status). Null when the text is not valid.
 */
export function parseMetricSpec(text: string): MetricSpec | null {
  const t = text.trim();
  if (!t) return null;
  const statusPart = /^([A-Za-z_*]+)\s*:\s*(.+)$/;
  const segments = t.split(";").map((s) => s.trim()).filter(Boolean);
  const perStatus = segments.length > 1 || (segments.length === 1 && statusPart.test(segments[0]) && STATUS_KEYS.has(statusPart.exec(segments[0])![1].toLowerCase()));
  if (!perStatus) return parseDistribution(t);
  const out: Record<string, MetricDistribution> = {};
  for (const segment of segments) {
    const m = statusPart.exec(segment);
    if (!m || !STATUS_KEYS.has(m[1].toLowerCase())) return null;
    const spec = parseDistribution(m[2]);
    if (spec === null) return null;
    out[m[1].toLowerCase()] = spec;
  }
  return out;
}

function formatDistribution(spec: MetricDistribution): string {
  if (typeof spec === "number") return String(spec);
  if ("values" in spec) {
    return Array.isArray(spec.values) ? spec.values.join(", ") : Object.entries(spec.values).map(([v, w]) => `${v}:${w}`).join(", ");
  }
  const continuous = spec.integer === false && Number.isInteger(spec.min) && Number.isInteger(spec.max);
  return continuous ? `${spec.min.toFixed(1)}..${spec.max.toFixed(1)}` : `${spec.min}..${spec.max}`;
}

const isDistribution = (spec: MetricSpec): spec is MetricDistribution => typeof spec === "number" || "values" in spec || ("min" in spec && "max" in spec);

/** The editor text of a metric spec (inverse of {@link parseMetricSpec}). */
export function formatMetricSpec(spec: MetricSpec): string {
  if (isDistribution(spec)) return formatDistribution(spec);
  return Object.entries(spec)
    .map(([status, d]) => `${status}: ${formatDistribution(d)}`)
    .join("; ");
}

/** Rounds a calibrated USD cost per execution (to a hundredth of a cent). */
const roundCost = (usd: number) => Math.round(usd * 10000) / 10000;

// ---------- calibration from real flights ----------

/**
 * Fits a scenario to a pipeline's flight record: each stage that has flown gets its observed outcome mix, retry share,
 * route choices, and typical duration; the global dials become the observed averages per handler (used by stages that
 * have not flown yet, such as ones just added in Design).
 */
export function calibrate(doc: StudioDoc, t: Telemetry, base: TunnelSettings): TunnelSettings {
  const nodes: Record<string, Behavior> = {};
  const totals = { agent: { ok: 0, retry: 0, all: 0 }, tool: { ok: 0, all: 0 } };
  const costPerVisit: Record<string, number> = {};
  for (const node of doc.nodes) {
    const stats = t.nodes[node.id];
    if (!stats || stats.visits === 0) continue;
    const handler = handlerOf(doc, node);
    if (stats.cost?.ai_units !== undefined) costPerVisit[node.id] = stats.cost.ai_units;
    if (REAL_HANDLERS.has(handler)) continue;
    const behavior: Behavior = {};
    const done = Object.values(stats.outcomes).reduce((a, b) => a + b, 0);
    if (done > 0 && handler !== "wait.human") {
      const attempts = done + stats.retries;
      const retry = attempts > 0 ? stats.retries / attempts : 0;
      const outcomes: Record<string, number> = {};
      for (const [status, count] of Object.entries(stats.outcomes)) {
        if (status === "success" || status === "fail" || status === "partial_success") outcomes[status] = round((count / done) * (1 - retry));
      }
      if (retry > 0) outcomes.retry = round(retry);
      if (Object.keys(outcomes).length > 0) behavior.outcomes = outcomes;
      const ok = (stats.outcomes.success ?? 0) + (stats.outcomes.partial_success ?? 0);
      if (handler === "tool") {
        totals.tool.ok += ok;
        totals.tool.all += done;
      } else if (handler === "codergen" || handler === "stack.manager_loop") {
        totals.agent.ok += ok;
        totals.agent.all += attempts;
        totals.agent.retry += stats.retries;
      }
    }
    // Route choices: how often each outgoing edge was taken from this stage.
    const out = outgoing(doc, node.id);
    const taken = (to: string) => t.edges.filter((e) => e.from === node.id && e.to === to).reduce((a, e) => a + e.count, 0);
    if (handler === "wait.human") {
      const choices: Record<string, number> = {};
      for (const e of out) choices[e.attrs.label || e.to] = (choices[e.attrs.label || e.to] ?? 0) + taken(e.to);
      if (Object.values(choices).some((c) => c > 0)) behavior.choices = normalize(choices);
    } else {
      const labels = routeLabels(doc, node);
      if (labels.length > 0) {
        const weights: Record<string, number> = {};
        for (const e of out) if (e.attrs.label && labels.includes(e.attrs.label)) weights[e.attrs.label] = (weights[e.attrs.label] ?? 0) + taken(e.to);
        if (Object.values(weights).some((c) => c > 0)) behavior.labels = normalize(weights);
      }
    }
    // Typical time (a person's, at a gate), capped at what the simulator accepts.
    if (stats.duration_ms) behavior.duration_ms = Math.min(MAX_DURATION_MS, Math.round(stats.duration_ms.p50));
    // Real cost per recorded execution, in USD (backends that report it), so the tunnel forecasts cost per flight.
    const usd = stats.cost?.cost_usd;
    if (usd !== undefined && Number.isFinite(usd) && usd > 0) behavior.cost = Math.min(MAX_COST_USD, roundCost(usd));
    if (Object.keys(behavior).length > 0) nodes[node.id] = behavior;
  }
  const finished = Object.values(t.outcomes).reduce((a, b) => a + b, 0);
  const agentSuccess = totals.agent.all > 0 ? totals.agent.ok / totals.agent.all : base.agentSuccess;
  const agentRetry = totals.agent.all > 0 ? totals.agent.retry / totals.agent.all : base.agentRetry;
  return {
    ...base,
    preset: "calibrated",
    agentSuccess: round(Math.min(agentSuccess, 1 - agentRetry)),
    agentRetry: round(agentRetry),
    toolSuccess: totals.tool.all > 0 ? round(totals.tool.ok / totals.tool.all) : base.toolSuccess,
    nodes,
    calibration: {
      runs: t.runs,
      at: new Date().toISOString(),
      window: t.window,
      successRate: finished > 0 ? ((t.outcomes.success ?? 0) + (t.outcomes.partial_success ?? 0)) / finished : 0,
      costPerVisit,
      meanCost: t.cost?.ai_units?.mean,
      meanCostUsd: t.cost?.cost_usd?.mean,
      meanDurationMs: t.duration_ms?.mean,
    },
  };
}

function normalize(weights: Record<string, number>): Record<string, number> {
  const total = Object.values(weights).reduce((a, b) => a + b, 0) || 1;
  return Object.fromEntries(Object.entries(weights).map(([k, v]) => [k, round(v / total)]));
}

/** Predicted AI units per flight: simulated visits per flight × the real cost per visit of each stage. */
export function predictedCost(report: SimReport, calibration: Calibration): { mean: number; covered: number } | null {
  const entries = Object.entries(calibration.costPerVisit);
  if (entries.length === 0) return null;
  let mean = 0;
  let covered = 0;
  for (const [node, cost] of entries) {
    const stats = report.nodes[node];
    if (!stats) continue;
    mean += (stats.visits / Math.max(1, report.runs)) * cost;
    covered++;
  }
  return { mean, covered };
}

// ---------- report (server wire format) ----------

export interface SampleRef {
  seed: number;
  run_index: number;
  status?: string;
  kind?: string;
  failure_reason?: string;
  steps?: number;
  /** Stages whose failure flowed on along an unconditional edge and was never made good. */
  failed_stages?: string[];
  simulated_duration_ms?: number;
  simulated_cost_usd?: number;
  path?: string[];
}

export interface SimNodeStats {
  visits: number;
  runs: number;
  mean_visits: number;
  max_visits: number;
  outcomes: Record<string, number>;
  retries: number;
  /** Failed visits whose failure flowed on along an unconditional edge. */
  failures_continued?: number;
  /** Flights that landed with this stage's failure behind them. */
  failed_stage_landings?: number;
  mean_duration_ms?: number;
  mean_cost_usd?: number;
  metrics?: Record<string, { count: number; mean: number; min: number; max: number }>;
}

export interface LandedMetric {
  flights: number;
  mean: number;
  min: number;
  p10: number;
  p50: number;
  p90: number;
  max: number;
  /** Flights per final value (when there are few distinct values). */
  values?: Record<string, number>;
}

export interface SimReport {
  runs: number;
  seed: number;
  duration_ms: number;
  max_steps: number;
  outcomes: Record<string, number>;
  /** Share of flights that reached the exit, including those with failed stages behind them. */
  success_rate: number;
  success_ci: [number, number];
  /** Share of flights that landed clean: the readiness number. */
  clean_success_rate?: number;
  clean_success_ci?: [number, number];
  /** Flights that landed with failed stages behind them. */
  failed_stage_landings?: number;
  steps: { min: number; mean: number; max: number; p50?: number; p90?: number; p95?: number; bucket_size: number; histogram: { steps: number; count: number }[] };
  simulated_duration_ms?: { mean: number; p50: number; p90: number; max: number };
  simulated_cost_usd?: { mean: number; p50: number; p90: number; max: number };
  nodes: Record<string, SimNodeStats>;
  edges: { from: string; to: string; count: number; runs: number; kind: string; label?: string }[];
  failures: { kind: string; reason: string; count: number; sample: SampleRef; last_node?: string | null; nodes?: Record<string, number> }[];
  landed_metrics?: Record<string, LandedMetric>;
  samples: { first_failure?: SampleRef | null; first_failed_stage_landing?: SampleRef | null; longest: SampleRef; typical: SampleRef };
  warnings: string[];
}

/** The readiness numbers of a report: clean landings (falling back to all landings for servers that predate them). */
export function landing(report: SimReport): { clean: number; ci: [number, number]; landed: number; dirty: number; dirtyShare: number; down: number } {
  const landed = report.outcomes.success ?? 0;
  const dirty = report.failed_stage_landings ?? 0;
  return {
    clean: report.clean_success_rate ?? report.success_rate,
    ci: report.clean_success_ci ?? report.success_ci,
    landed,
    dirty,
    dirtyShare: report.runs > 0 ? dirty / report.runs : 0,
    down: report.runs - landed,
  };
}

/** Stages whose failures flowed on into landed flights, most flights first. */
export function failedStageLandings(report: SimReport): { node: string; flights: number }[] {
  return Object.entries(report.nodes)
    .map(([node, n]) => ({ node, flights: n.failed_stage_landings ?? 0 }))
    .filter((e) => e.flights > 0)
    .sort((a, b) => b.flights - a.flights || a.node.localeCompare(b.node));
}

/** Formats USD: `$1,234`, `$12.34`, `$0.042`. */
export function formatUsd(usd: number): string {
  if (usd >= 1000) return `$${Math.round(usd).toLocaleString("en-US")}`;
  if (usd >= 1) return `$${usd.toFixed(2)}`;
  if (usd > 0) return `$${Number(usd.toPrecision(2))}`;
  return "$0";
}

export const OUTCOME_KINDS: { key: string; label: string; tone: string }[] = [
  { key: "success", label: "landed", tone: "var(--ok)" },
  { key: "fail", label: "failed", tone: "var(--fail)" },
  { key: "step_limit", label: "never converged", tone: "var(--signal)" },
  { key: "goal_gate", label: "goal gate unmet", tone: "var(--warn)" },
  { key: "dead_end", label: "dead end", tone: "var(--kind-fanout)" },
  { key: "error", label: "error", tone: "var(--ink-3)" },
];
