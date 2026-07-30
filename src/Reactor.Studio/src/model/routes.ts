import { normalizeCondition } from "./conditions";
import { edgeAttr, handlerOf, nodeLabel, outgoing } from "./doc";
import type { Attrs, DocEdge, DocNode, HandlerType, StudioDoc } from "./types";

export { normalizeCondition };

export type RouteKind = "next" | "success" | "partial" | "fail" | "label" | "cond" | "choice" | "branch";

export interface Route {
  /** Handle id suffix: `out:<key>`. */
  key: string;
  kind: RouteKind;
  /** Port caption. */
  text: string;
  /** Secondary text (condition expression, accelerator key, ...). */
  detail?: string;
  edges: DocEdge[];
  /** A suggested port with no edges yet (shown on hover/selection). */
  ghost?: boolean;
}

/** Parses an accelerator from a label: `[A] Approve`, `A) Approve`, `A - Approve`. */
export function acceleratorOf(label: string): { key: string; text: string } | null {
  const bracket = /^\s*\[([A-Za-z0-9])\]\s*(.*)$/.exec(label);
  if (bracket) return { key: bracket[1].toUpperCase(), text: bracket[2] };
  const paren = /^\s*([A-Za-z0-9])\)\s+(.*)$/.exec(label);
  if (paren) return { key: paren[1].toUpperCase(), text: paren[2] };
  const dash = /^\s*([A-Za-z0-9])\s+-\s+(.*)$/.exec(label);
  if (dash) return { key: dash[1].toUpperCase(), text: dash[2] };
  return null;
}

export function routeKeyOf(doc: StudioDoc, edge: DocEdge, handler: HandlerType): string {
  const label = edgeAttr(doc, edge, "label") ?? "";
  const condition = normalizeCondition(edgeAttr(doc, edge, "condition") ?? "");
  if (handler === "parallel") return "branch";
  if (handler === "wait.human") return "choice:" + (label || edge.to);
  if (condition) {
    if (condition === "outcome=success") return "success";
    if (condition === "outcome=fail") return "fail";
    if (condition === "outcome=partial_success") return "partial";
    return "cond:" + condition;
  }
  if (label) return "label:" + label;
  return "next";
}

const ORDER: Record<RouteKind, number> = { next: 0, success: 1, partial: 2, fail: 3, label: 4, cond: 5, choice: 6, branch: 7 };

function describe(key: string, doc: StudioDoc, edges: DocEdge[]): Pick<Route, "kind" | "text" | "detail"> {
  if (key === "next") return { kind: "next", text: "next" };
  if (key === "success") return { kind: "success", text: "on success" };
  if (key === "fail") return { kind: "fail", text: "on fail" };
  if (key === "partial") return { kind: "partial", text: "on partial" };
  if (key === "branch") return { kind: "branch", text: edges.length === 1 ? "1 branch" : `${edges.length} branches` };
  if (key.startsWith("choice:")) {
    const label = key.slice(7);
    const accel = acceleratorOf(label);
    const target = doc.nodes.find((n) => n.id === edges[0]?.to);
    const text = accel ? accel.text || label : label === edges[0]?.to && target ? nodeLabel(doc, target) : label;
    return { kind: "choice", text, detail: accel?.key };
  }
  if (key.startsWith("label:")) return { kind: "label", text: key.slice(6) };
  return { kind: "cond", text: key.slice(5), detail: "if" };
}

/** Output routes of a node, grouped by route key, in display order. */
export function routesOf(doc: StudioDoc, node: DocNode): Route[] {
  const handler = handlerOf(doc, node);
  if (handler === "exit") return [];
  const groups = new Map<string, DocEdge[]>();
  for (const edge of outgoing(doc, node.id)) {
    const key = routeKeyOf(doc, edge, handler);
    groups.set(key, [...(groups.get(key) ?? []), edge]);
  }
  const routes: Route[] = [...groups.entries()].map(([key, edges]) => ({ key, edges, ...describe(key, doc, edges) }));
  // Gate options keep edge order (it is the option order); everything else uses a stable semantic order.
  if (handler !== "wait.human") routes.sort((a, b) => ORDER[a.kind] - ORDER[b.kind]);
  return routes;
}

/** Ports suggested for a node beyond its existing routes (shown when the node is hovered or selected). */
export function ghostRoutes(doc: StudioDoc, node: DocNode, existing: Route[]): Route[] {
  const handler = handlerOf(doc, node);
  const has = (key: string) => existing.some((r) => r.key === key);
  const ghost = (key: string, kind: RouteKind, text: string, detail?: string): Route => ({ key, kind, text, detail, edges: [], ghost: true });
  switch (handler) {
    case "exit":
      return [];
    case "parallel":
      return has("branch") ? [] : [ghost("branch", "branch", "branches")];
    case "wait.human":
      return [ghost("choice:+", "choice", "add option", "+")];
    case "conditional":
      return [ghost("cond:+", "cond", "add condition", "if"), ...(has("next") ? [] : [ghost("next", "next", "otherwise")])];
    case "start":
      return has("next") ? [] : [ghost("next", "next", "next")];
    default: {
      const list: Route[] = [];
      if (!has("next")) list.push(ghost("next", "next", "next"));
      if (!has("success")) list.push(ghost("success", "success", "on success"));
      if (!has("fail")) list.push(ghost("fail", "fail", "on fail"));
      return list;
    }
  }
}

/** Default edge attributes when connecting from a route port. */
export function attrsForRoute(doc: StudioDoc, source: DocNode, routeKey: string, target: DocNode | undefined, existing: DocEdge[]): Attrs {
  if (routeKey === "next" || routeKey === "branch") return {};
  if (routeKey === "success") return { condition: "outcome=success" };
  if (routeKey === "fail") return { condition: "outcome=fail" };
  if (routeKey === "partial") return { condition: "outcome=partial_success" };
  if (routeKey.startsWith("choice:")) {
    if (routeKey !== "choice:+") return { label: routeKey.slice(7) };
    const text = target ? nodeLabel(doc, target) : "Option";
    const used = new Set(outgoing(doc, source.id).map((e) => acceleratorOf(e.attrs.label ?? "")?.key).filter(Boolean));
    const candidates = (text.replace(/[^A-Za-z0-9]/g, "") + "ABCDEFGHIJKLMNOPQRSTUVWXYZ").toUpperCase();
    const key = [...candidates].find((c) => !used.has(c)) ?? "X";
    return { label: `[${key}] ${text}` };
  }
  if (routeKey.startsWith("label:")) return { label: routeKey.slice(6) };
  if (routeKey === "cond:+") {
    return { condition: existing.some((e) => normalizeCondition(e.attrs.condition ?? "") === "outcome=success") ? "outcome=fail" : "outcome=success" };
  }
  if (routeKey.startsWith("cond:")) return { condition: routeKey.slice(5) };
  return {};
}

/** A one-line description of how an edge routes (for tooltips and the inspector). */
export function describeEdge(doc: StudioDoc, edge: DocEdge): string {
  const source = doc.nodes.find((n) => n.id === edge.from);
  const handler = source ? handlerOf(doc, source) : "codergen";
  const key = routeKeyOf(doc, edge, handler);
  const label = edgeAttr(doc, edge, "label");
  if (key === "branch") return "parallel branch";
  if (key.startsWith("choice:")) return `when a person picks “${label || edge.to}”`;
  if (key === "success") return "when the stage succeeds";
  if (key === "fail") return "when the stage fails";
  if (key === "partial") return "on partial success";
  if (key.startsWith("cond:")) return `when ${key.slice(5)}`;
  if (key.startsWith("label:")) return `when the stage prefers “${label}”`;
  return "always (unconditional)";
}
