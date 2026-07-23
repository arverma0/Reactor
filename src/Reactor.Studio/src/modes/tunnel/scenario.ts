import { handlerOf, nodeAttr, outgoing } from "../../model/doc";
import { routeKeyOf } from "../../model/routes";
import type { DocNode, StudioDoc } from "../../model/types";

/** One stage's simulated behavior (wire format of the server's scenario). */
export interface Behavior {
  outcomes?: Record<string, number>;
  script?: string[];
  labels?: Record<string, number>;
  choices?: Record<string, number>;
  context_updates?: Record<string, Record<string, unknown>>;
  duration_ms?: number;
}

export interface Scenario {
  seed?: number;
  step_delay_ms?: number;
  defaults?: Record<string, Behavior>;
  nodes?: Record<string, Behavior>;
}

export type Preset = "happy" | "realistic" | "stress" | "chaos" | "calibrated" | "custom";

export interface TunnelSettings {
  preset: Preset;
  /** Share of agent executions that succeed. */
  agentSuccess: number;
  /** Share of agent executions that ask for a retry (the rest fail). */
  agentRetry: number;
  /** Share of tool commands that pass. */
  toolSuccess: number;
  nodes: Record<string, Behavior>;
  runs: number;
  maxSteps: number;
  seed: number | null;
}

export const PRESETS: Record<Exclude<Preset, "custom" | "calibrated">, { label: string; blurb: string; agentSuccess: number; agentRetry: number; toolSuccess: number }> = {
  happy: { label: "Happy path", blurb: "Every stage succeeds. Checks the main route reaches the exit.", agentSuccess: 1, agentRetry: 0, toolSuccess: 1 },
  realistic: { label: "Realistic", blurb: "Agents succeed 9 times in 10; a quarter of tool runs fail.", agentSuccess: 0.9, agentRetry: 0.03, toolSuccess: 0.75 },
  stress: { label: "Stress", blurb: "A bad day: agents miss one time in four, tests fail half the time.", agentSuccess: 0.75, agentRetry: 0.05, toolSuccess: 0.5 },
  chaos: { label: "Chaos", blurb: "Hostile conditions. Finds loops that never converge.", agentSuccess: 0.55, agentRetry: 0.15, toolSuccess: 0.35 },
};

export const defaultSettings = (): TunnelSettings => ({ preset: "realistic", ...pick(PRESETS.realistic), nodes: {}, runs: 500, maxSteps: 200, seed: null });

function pick(p: { agentSuccess: number; agentRetry: number; toolSuccess: number }) {
  return { agentSuccess: p.agentSuccess, agentRetry: p.agentRetry, toolSuccess: p.toolSuccess };
}

const round = (n: number) => Math.round(n * 1000) / 1000;

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
  return {
    seed: settings.seed ?? undefined,
    defaults: {
      codergen: { outcomes: { success: round(settings.agentSuccess), retry: round(settings.agentRetry), fail: round(fail) } },
      tool: { outcomes: { success: round(settings.toolSuccess), fail: round(1 - settings.toolSuccess) } },
      "stack.manager_loop": { outcomes: { success: round(settings.agentSuccess), fail: round(1 - settings.agentSuccess) } },
    },
    nodes,
    ...extra,
  };
}

// ---------- report (server wire format) ----------

export interface SampleRef {
  seed: number;
  run_index: number;
  status?: string;
  kind?: string;
  failure_reason?: string;
  steps?: number;
  path?: string[];
}

export interface SimReport {
  runs: number;
  seed: number;
  duration_ms: number;
  max_steps: number;
  outcomes: Record<string, number>;
  success_rate: number;
  success_ci: [number, number];
  steps: { min: number; mean: number; max: number; p50?: number; p90?: number; p95?: number; bucket_size: number; histogram: { steps: number; count: number }[] };
  simulated_duration_ms?: { mean: number; p50: number; p90: number; max: number };
  nodes: Record<string, { visits: number; runs: number; mean_visits: number; max_visits: number; outcomes: Record<string, number>; retries: number }>;
  edges: { from: string; to: string; count: number; runs: number; kind: string; label?: string }[];
  failures: { kind: string; reason: string; count: number; sample: SampleRef; last_node?: string }[];
  samples: { first_failure?: SampleRef | null; longest: SampleRef; typical: SampleRef };
  warnings: string[];
}

export const OUTCOME_KINDS: { key: string; label: string; tone: string }[] = [
  { key: "success", label: "landed", tone: "var(--ok)" },
  { key: "fail", label: "failed", tone: "var(--fail)" },
  { key: "step_limit", label: "never converged", tone: "var(--signal)" },
  { key: "goal_gate", label: "goal gate unmet", tone: "var(--warn)" },
  { key: "dead_end", label: "dead end", tone: "var(--kind-fanout)" },
  { key: "error", label: "error", tone: "var(--ink-3)" },
];
