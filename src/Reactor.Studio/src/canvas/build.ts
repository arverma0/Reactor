import type { Edge, Node } from "@xyflow/react";
import type { PendingQuestion } from "../api/types";
import { artifactIndex, dataLinks, nodeInputs, nodeOutputs } from "../model/artifacts";
import { describeCondition } from "../model/conditions";
import { edgeAttr, groupChain, handlerOf, kindOf, nodeAttr, nodeClasses, nodeLabel } from "../model/doc";
import { KINDS, type NodeKind } from "../model/handlers";
import { lineColor } from "../model/palette";
import { ghostRoutes, routeKeyOf, routesOf, type Route } from "../model/routes";
import { effectiveModel, parseStylesheet } from "../model/stylesheet";
import type { Diagnostic, DocNode, Effective, StudioDoc } from "../model/types";

export type NodeState = "idle" | "running" | "success" | "partial_success" | "fail" | "retry" | "skipped" | "waiting" | "dim" | "visited" | "restored";

export interface NodeOverlay {
  state?: NodeState;
  /** Small readout in the header (e.g. "×3", "62%", "1.2s"). */
  badge?: string;
  /** 0..1 intensity for heat views. */
  heat?: number;
  heatLabel?: string;
  /** Live log lines (agent activity). */
  lines?: { text: string; level?: string }[];
  /** A pending human question shown on the card. */
  question?: PendingQuestion;
  /** ISO time the current execution started (ticking timer). */
  startedAt?: string;
  /** Static readouts, e.g. duration and cost. */
  readouts?: { label: string; value: string; tone?: "ok" | "fail" | "warn" }[];
  attempt?: number;
  /** Artifact names produced in this run. */
  produced?: string[];
}

export interface EdgeOverlay {
  state?: "taken" | "active" | "dim";
  /** 0..1 flow share (traffic thickness). */
  flow?: number;
  /** Text shown on the edge (e.g. "62%", "×4"). */
  text?: string;
  /** Changes when a new traversal should animate a pulse. */
  pulse?: number;
}

export interface CanvasOverlay {
  kind: "live" | "replay" | "heat" | "sim";
  nodes: Record<string, NodeOverlay>;
  /** Keyed by "from->to". */
  edges: Record<string, EdgeOverlay>;
}

export const edgeKey = (from: string, to: string) => `${from}->${to}`;

export interface PinView {
  name: string;
  color: string;
  path?: string;
  /** Input with no producer anywhere. */
  dangling?: boolean;
}

export interface StageData extends Record<string, unknown> {
  id: string;
  kind: NodeKind;
  handler: string;
  label: string;
  prompt: string;
  command?: string;
  model?: Effective[keyof Effective];
  effort?: Effective[keyof Effective];
  backend?: string;
  fidelity?: string;
  thread?: { id: string; color: string };
  goalGate: boolean;
  retries?: string;
  timeout?: string;
  classes: string[];
  routes: Route[];
  ghosts: Route[];
  inputs: PinView[];
  outputs: PinView[];
  diagnostics: Diagnostic[];
  overlay?: NodeOverlay;
  overlayKind?: CanvasOverlay["kind"];
  details: { label: string; value: string }[];
  gateCount?: number;
}

export interface FrameData extends Record<string, unknown> {
  uid: string;
  label: string;
  className?: string;
  model?: string;
  depth: number;
  members: number;
}

export interface NoteData extends Record<string, unknown> {
  id: string;
  text: string;
  color?: string;
}

export interface CableData extends Record<string, unknown> {
  uid: string;
  routeKind: Route["kind"];
  label?: string;
  condition?: string;
  conditionText?: string;
  loopRestart: boolean;
  weight?: string;
  diagnostics: Diagnostic[];
  overlay?: EdgeOverlay;
  overlayKind?: CanvasOverlay["kind"];
  /** Lane index among back edges, for separation. */
  lane: number;
}

export interface DataEdgeData extends Record<string, unknown> {
  name: string;
  color: string;
  active?: boolean;
}

function threadOf(doc: StudioDoc, node: DocNode): string | undefined {
  const fidelity = nodeAttr(doc, node, "fidelity") || doc.graphAttrs.default_fidelity;
  if (fidelity !== "full") return undefined;
  return nodeAttr(doc, node, "thread_id") || doc.graphAttrs.default_thread_id || nodeClasses(doc, node)[0] || undefined;
}

/** Card data for every stage node. */
export function buildStageData(
  doc: StudioDoc,
  diagnostics: Diagnostic[],
  serverEffective: Record<string, Effective>,
  overlay: CanvasOverlay | null,
): Map<string, StageData> {
  const rules = parseStylesheet(doc.graphAttrs.model_stylesheet);
  const artifacts = artifactIndex(doc);
  const goalGates = doc.nodes.filter((n) => (nodeAttr(doc, n, "goal_gate") ?? "").toLowerCase() === "true").length;
  const result = new Map<string, StageData>();
  for (const node of doc.nodes) {
    const kind = kindOf(doc, node);
    const handler = handlerOf(doc, node);
    const local = effectiveModel(doc, node, rules);
    const server = serverEffective[node.id];
    const model = local.llm_model ?? server?.llm_model;
    const effort = local.reasoning_effort ?? server?.reasoning_effort;
    const routes = routesOf(doc, node);
    const threadId = threadOf(doc, node);
    const details: { label: string; value: string }[] = [];
    const add = (label: string, key: string) => {
      const value = nodeAttr(doc, node, key);
      if (value) details.push({ label, value });
    };
    if (kind === "human") {
      add("timeout", "human.timeout");
      add("default", "human.default_choice");
      if (nodeAttr(doc, node, "human.input") === "freeform") details.push({ label: "input", value: "free text" });
    } else if (kind === "fanout") {
      details.push({ label: "join", value: nodeAttr(doc, node, "join_policy") || "wait_all" });
      add("max", "max_parallel");
    } else if (kind === "join") {
      if (nodeAttr(doc, node, "prompt")) details.push({ label: "picks", value: "LLM judge" });
      else details.push({ label: "picks", value: "by status" });
    } else if (kind === "supervisor") {
      const child = nodeAttr(doc, node, "stack.child_dotfile") || doc.graphAttrs["stack.child_dotfile"];
      if (child) details.push({ label: "child", value: child });
      add("poll", "manager.poll_interval");
      add("actions", "manager.actions");
    } else if (kind === "custom") {
      add("type", "type");
    }

    const nodeDiagnostics = diagnostics.filter((d) => d.node_id === node.id);
    const inputs: PinView[] = KINDS[kind].pins
      ? nodeInputs(doc, node).map((name) => {
          const info = artifacts.get(name);
          return { name, color: info?.color ?? lineColor(name), dangling: !info || info.producers.filter((p) => p.node !== node.id).length === 0 };
        })
      : [];
    const outputs: PinView[] = KINDS[kind].pins
      ? nodeOutputs(doc, node).map((o) => ({ name: o.name, path: o.path, color: artifacts.get(o.name)?.color ?? lineColor(o.name) }))
      : [];
    const retries = nodeAttr(doc, node, "max_retries");
    result.set(node.id, {
      id: node.id,
      kind,
      handler,
      label: nodeLabel(doc, node),
      prompt: nodeAttr(doc, node, "prompt") ?? "",
      command: kind === "tool" ? nodeAttr(doc, node, "tool_command") ?? "" : undefined,
      model: KINDS[kind].llm || (kind === "join" && nodeAttr(doc, node, "prompt")) ? model : undefined,
      effort: KINDS[kind].llm ? effort : undefined,
      backend: nodeAttr(doc, node, "backend"),
      fidelity: nodeAttr(doc, node, "fidelity"),
      thread: threadId ? { id: threadId, color: lineColor(threadId, doc.layout.threads?.[threadId]?.color) } : undefined,
      goalGate: (nodeAttr(doc, node, "goal_gate") ?? "").toLowerCase() === "true",
      retries: retries && retries !== "0" ? retries : undefined,
      timeout: kind === "human" ? undefined : nodeAttr(doc, node, "timeout"),
      classes: nodeClasses(doc, node),
      routes,
      ghosts: ghostRoutes(doc, node, routes),
      inputs,
      outputs,
      diagnostics: nodeDiagnostics,
      overlay: overlay?.nodes[node.id],
      overlayKind: overlay?.kind,
      details,
      gateCount: kind === "exit" ? goalGates : undefined,
    });
  }
  return result;
}

export function buildStageNode(data: StageData, position: { x: number; y: number }, previous?: Node): Node<StageData> {
  return {
    id: data.id,
    type: "stage",
    position: previous?.dragging ? previous.position : position,
    data,
    selected: previous?.selected,
    measured: previous?.measured,
    dragging: previous?.dragging,
    zIndex: 1,
  };
}

/** Control edges plus data (artifact) wires. */
export function buildEdges(doc: StudioDoc, diagnostics: Diagnostic[], overlay: CanvasOverlay | null, showData: boolean, positions: Record<string, { x: number }>): Edge[] {
  const edges: Edge[] = [];
  let lane = 0;
  for (const edge of doc.edges) {
    const source = doc.nodes.find((n) => n.id === edge.from);
    const target = doc.nodes.find((n) => n.id === edge.to);
    if (!source || !target) continue;
    const handler = handlerOf(doc, source);
    const key = routeKeyOf(doc, edge, handler);
    const label = edgeAttr(doc, edge, "label");
    const condition = edgeAttr(doc, edge, "condition");
    const back = (positions[edge.to]?.x ?? 0) <= (positions[edge.from]?.x ?? 0) + 40;
    const route = routesOf(doc, source).find((r) => r.key === key);
    edges.push({
      id: edge.uid,
      type: "cable",
      source: edge.from,
      target: edge.to,
      sourceHandle: `out:${key}`,
      targetHandle: "in",
      zIndex: 0,
      data: {
        uid: edge.uid,
        routeKind: route?.kind ?? "next",
        label: label || undefined,
        condition: condition || undefined,
        conditionText: condition ? describeCondition(condition) : undefined,
        loopRestart: (edgeAttr(doc, edge, "loop_restart") ?? "").toLowerCase() === "true",
        weight: edgeAttr(doc, edge, "weight"),
        diagnostics: diagnostics.filter((d) => d.edge === `${edge.from} -> ${edge.to}`),
        overlay: overlay?.edges[edgeKey(edge.from, edge.to)],
        overlayKind: overlay?.kind,
        lane: back ? lane++ : 0,
      } satisfies CableData,
    });
  }
  if (showData) {
    for (const link of dataLinks(doc)) {
      const produced = overlay?.nodes[link.from]?.produced?.includes(link.name);
      edges.push({
        id: link.id,
        type: "data",
        source: link.from,
        target: link.to,
        sourceHandle: `aout:${link.name}`,
        targetHandle: `ain:${link.name}`,
        selectable: true,
        zIndex: 0,
        data: { name: link.name, color: link.color, active: produced } satisfies DataEdgeData,
      });
    }
  }
  return edges;
}

export interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
}

const FRAME_PAD = 22;
const FRAME_TOP = 40;

/** Frames fit their members (and nested frames). Nodes being dragged are left out so a node can be dragged out. */
export function buildFrames(doc: StudioDoc, nodes: Node[], frozen: Record<string, Box>): { frames: Node<FrameData>[]; boxes: Record<string, Box> } {
  const boxes: Record<string, Box> = {};
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const depthOf = (uid: string) => groupChain(doc, uid).length - 1;
  const ordered = [...doc.groups].sort((a, b) => depthOf(b.uid) - depthOf(a.uid));
  const rules = parseStylesheet(doc.graphAttrs.model_stylesheet);
  for (const group of ordered) {
    const members = doc.nodes.filter((n) => n.group === group.uid).map((n) => byId.get(n.id)).filter((n): n is Node => !!n && !n.dragging);
    const children = doc.groups.filter((g) => g.parent === group.uid).map((g) => boxes[g.uid]).filter(Boolean);
    const rects: Box[] = [
      ...members.map((n) => ({ x: n.position.x, y: n.position.y, w: n.measured?.width ?? 260, h: n.measured?.height ?? 140 })),
      ...children.map((b) => ({ x: b.x, y: b.y, w: b.w, h: b.h })),
    ];
    if (rects.length === 0) {
      if (frozen[group.uid]) boxes[group.uid] = frozen[group.uid];
      continue;
    }
    const minX = Math.min(...rects.map((r) => r.x)) - FRAME_PAD;
    const minY = Math.min(...rects.map((r) => r.y)) - FRAME_TOP;
    const maxX = Math.max(...rects.map((r) => r.x + r.w)) + FRAME_PAD;
    const maxY = Math.max(...rects.map((r) => r.y + r.h)) + FRAME_PAD;
    boxes[group.uid] = { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
  }
  const frames: Node<FrameData>[] = [];
  for (const group of doc.groups) {
    const box = boxes[group.uid];
    if (!box) continue;
    const label = group.attrs.label ?? group.name ?? "Group";
    const className = group.attrs.label ? label.trim().toLowerCase().replace(/\s+/g, "-").replace(/[^a-z0-9-]/g, "") : undefined;
    const classRule = className ? [...rules].reverse().find((r) => r.kind === "class" && r.value === className && r.declarations.llm_model) : undefined;
    frames.push({
      id: `frame:${group.uid}`,
      type: "frame",
      position: { x: box.x, y: box.y },
      style: { width: box.w, height: box.h },
      width: box.w,
      height: box.h,
      data: {
        uid: group.uid,
        label,
        className,
        model: classRule?.declarations.llm_model,
        depth: depthOf(group.uid),
        members: doc.nodes.filter((n) => n.group === group.uid).length,
      },
      zIndex: -1 + depthOf(group.uid) * 0,
      selectable: true,
      draggable: true,
      focusable: false,
    });
  }
  return { frames, boxes };
}

/** The innermost frame containing a point. */
export function frameAt(doc: StudioDoc, boxes: Record<string, Box>, x: number, y: number): string | null {
  let best: string | null = null;
  let bestDepth = -1;
  for (const group of doc.groups) {
    const box = boxes[group.uid];
    if (!box) continue;
    if (x >= box.x && x <= box.x + box.w && y >= box.y && y <= box.y + box.h) {
      const depth = groupChain(doc, group.uid).length;
      if (depth > bestDepth) {
        best = group.uid;
        bestDepth = depth;
      }
    }
  }
  return best;
}
