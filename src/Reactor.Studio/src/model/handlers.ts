import type { Attrs, DocNode, HandlerType } from "./types";

export type NodeKind = "start" | "exit" | "agent" | "tool" | "human" | "router" | "fanout" | "join" | "supervisor" | "custom";

export interface KindMeta {
  kind: NodeKind;
  handler: HandlerType;
  /** Short display name. */
  name: string;
  /** Three-letter part code shown on cards (instrument-panel style). */
  code: string;
  /** DOT shape that selects the handler. */
  shape: string;
  /** One-line description for the library. */
  blurb: string;
  /** Default attributes for a new node of this kind. */
  defaults: Attrs;
  /** Prefix for generated ids. */
  idPrefix: string;
  /** Library keyboard shortcut. */
  hotkey?: string;
  /** Whether stages of this kind run a prompt through a backend. */
  llm: boolean;
  /** Whether artifact pins (inputs/outputs) apply. */
  pins: boolean;
}

export const SHAPE_TO_HANDLER: Record<string, HandlerType> = {
  Mdiamond: "start",
  Msquare: "exit",
  box: "codergen",
  hexagon: "wait.human",
  diamond: "conditional",
  component: "parallel",
  tripleoctagon: "parallel.fan_in",
  parallelogram: "tool",
  house: "stack.manager_loop",
};

export const KINDS: Record<NodeKind, KindMeta> = {
  start: {
    kind: "start", handler: "start", name: "Start", code: "BEG", shape: "Mdiamond", idPrefix: "start",
    blurb: "Entry point. Exactly one per pipeline.", defaults: { shape: "Mdiamond" }, llm: false, pins: false,
  },
  exit: {
    kind: "exit", handler: "exit", name: "Exit", code: "END", shape: "Msquare", idPrefix: "exit",
    blurb: "Terminal. The run succeeds here once every goal gate has succeeded.", defaults: { shape: "Msquare" }, llm: false, pins: false,
  },
  agent: {
    kind: "agent", handler: "codergen", name: "Agent", code: "AGT", shape: "box", idPrefix: "stage", hotkey: "A",
    blurb: "A coding agent works on a prompt with tools, then reports an outcome.", defaults: { label: "New stage", prompt: "" }, llm: true, pins: true,
  },
  tool: {
    kind: "tool", handler: "tool", name: "Tool", code: "TOL", shape: "parallelogram", idPrefix: "check", hotkey: "T",
    blurb: "Runs a shell command. Exit code 0 is success; output lands in context.", defaults: { shape: "parallelogram", label: "Run tests", tool_command: "" }, llm: false, pins: true,
  },
  human: {
    kind: "human", handler: "wait.human", name: "Human gate", code: "HMN", shape: "hexagon", idPrefix: "gate", hotkey: "H",
    blurb: "Pauses for a person to pick one of the outgoing routes.", defaults: { shape: "hexagon", label: "Approve?" }, llm: false, pins: false,
  },
  router: {
    kind: "router", handler: "conditional", name: "Router", code: "RTE", shape: "diamond", idPrefix: "route", hotkey: "R",
    blurb: "Routes on the previous outcome or context values. No LLM call.", defaults: { shape: "diamond", label: "Route" }, llm: false, pins: false,
  },
  fanout: {
    kind: "fanout", handler: "parallel", name: "Fan-out", code: "FAN", shape: "component", idPrefix: "parallel", hotkey: "P",
    blurb: "Runs every outgoing branch concurrently on isolated context.", defaults: { shape: "component", label: "In parallel" }, llm: false, pins: false,
  },
  join: {
    kind: "join", handler: "parallel.fan_in", name: "Join", code: "JON", shape: "tripleoctagon", idPrefix: "join", hotkey: "J",
    blurb: "Gathers parallel branches; optionally an LLM picks the best one.", defaults: { shape: "tripleoctagon", label: "Gather" }, llm: false, pins: false,
  },
  supervisor: {
    kind: "supervisor", handler: "stack.manager_loop", name: "Supervisor", code: "SUP", shape: "house", idPrefix: "supervise", hotkey: "S",
    blurb: "Starts a child pipeline, observes it, and steers it when it drifts.", defaults: { shape: "house", label: "Supervise" }, llm: false, pins: false,
  },
  custom: {
    kind: "custom", handler: "custom", name: "Custom", code: "CUS", shape: "box", idPrefix: "custom",
    blurb: "A handler registered by type name in your Reactor host.", defaults: { type: "custom" }, llm: false, pins: false,
  },
};

export const LIBRARY_KINDS: NodeKind[] = ["agent", "tool", "human", "router", "fanout", "join", "supervisor", "start", "exit"];

const isStartId = (id: string) => id === "start" || id === "Start" || id === "START";
const isExitId = (id: string) => id.toLowerCase() === "exit" || id.toLowerCase() === "end";

/** Effective attribute lookup: node attributes over group defaults over graph defaults. */
export type AttrLookup = (node: DocNode, key: string) => string | undefined;

/** Mirrors HandlerTypes.Resolve: explicit type, then shape mapping, then start/exit ids (only without a shape), else codergen. */
export function resolveHandler(node: DocNode, lookup: AttrLookup): HandlerType {
  const type = lookup(node, "type");
  if (type && type.trim()) return type;
  const shape = lookup(node, "shape");
  if (shape !== undefined && SHAPE_TO_HANDLER[shape]) return SHAPE_TO_HANDLER[shape];
  if (shape === undefined) {
    if (isStartId(node.id)) return "start";
    if (isExitId(node.id)) return "exit";
  }
  return "codergen";
}

export function kindOfHandler(handler: HandlerType): NodeKind {
  switch (handler) {
    case "start": return "start";
    case "exit": return "exit";
    case "codergen": return "agent";
    case "tool": return "tool";
    case "wait.human": return "human";
    case "conditional": return "router";
    case "parallel": return "fanout";
    case "parallel.fan_in": return "join";
    case "stack.manager_loop": return "supervisor";
    default: return "custom";
  }
}

/** Attributes that switch a node to another kind (shape for built-ins, type for custom). */
export function kindAttrs(kind: NodeKind): Attrs {
  const meta = KINDS[kind];
  if (kind === "custom") return { type: "custom" };
  return kind === "agent" ? {} : { shape: meta.shape };
}

/** Accent color per kind (CSS custom property names resolve the actual color). */
export const kindColorVar = (kind: NodeKind) => `var(--kind-${kind})`;
