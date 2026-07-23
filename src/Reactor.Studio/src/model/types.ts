/** Attribute map as written in DOT (string values, insertion order preserved). */
export type Attrs = Record<string, string>;

/** Handler types understood by the engine (plus any custom type string). */
export type HandlerType =
  | "start"
  | "exit"
  | "codergen"
  | "wait.human"
  | "conditional"
  | "parallel"
  | "parallel.fan_in"
  | "tool"
  | "stack.manager_loop"
  | (string & {});

export interface DocNode {
  id: string;
  /** Attributes written on the node statement (explicit keys only; defaults live on the graph/group). */
  attrs: Attrs;
  /** Owning group (subgraph) uid, or null at the root. */
  group: string | null;
}

export interface DocEdge {
  /** Stable UI id (DOT edges have no identity). */
  uid: string;
  from: string;
  to: string;
  attrs: Attrs;
}

export interface DocGroup {
  /** Stable UI id: `g:<name>` for named subgraphs, `g~<n>` for anonymous ones. */
  uid: string;
  /** Subgraph name in DOT (`cluster_design`), or null for an anonymous block. */
  name: string | null;
  parent: string | null;
  /** Subgraph attributes (label, ...). The label derives a stylesheet class. */
  attrs: Attrs;
  nodeDefaults: Attrs;
  edgeDefaults: Attrs;
}

export interface StickyNote {
  id: string;
  x: number;
  y: number;
  w: number;
  h: number;
  text: string;
  color?: string;
}

export interface Point {
  x: number;
  y: number;
}

/** The `<name>.layout.json` sidecar: everything visual that the engine does not need. */
export interface LayoutSidecar {
  version: 1;
  nodes: Record<string, Point>;
  notes: StickyNote[];
  viewport?: { x: number; y: number; zoom: number };
  /** Per-artifact presentation (color, description). */
  artifacts?: Record<string, { color?: string; description?: string }>;
  /** Per-thread presentation (color). */
  threads?: Record<string, { color?: string }>;
}

export interface StudioDoc {
  name: string;
  /** Leading comment block of the source file (kept verbatim). */
  header: string;
  graphAttrs: Attrs;
  nodeDefaults: Attrs;
  edgeDefaults: Attrs;
  nodes: DocNode[];
  edges: DocEdge[];
  groups: DocGroup[];
  layout: LayoutSidecar;
}

export interface Diagnostic {
  rule: string;
  severity: "error" | "warning" | "info";
  message: string;
  node_id?: string | null;
  edge?: string | null;
  fix?: string | null;
}

export interface EffectiveValue {
  value: string;
  source: "node" | "stylesheet" | "default";
  selector?: string;
}

export type Effective = Partial<Record<"llm_model" | "llm_provider" | "reasoning_effort", EffectiveValue>>;

export const emptyLayout = (): LayoutSidecar => ({ version: 1, nodes: {}, notes: [] });

export const emptyDoc = (name = "pipeline"): StudioDoc => ({
  name,
  header: "",
  graphAttrs: {},
  nodeDefaults: {},
  edgeDefaults: {},
  nodes: [],
  edges: [],
  groups: [],
  layout: emptyLayout(),
});
