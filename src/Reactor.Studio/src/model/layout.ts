import dagre from "@dagrejs/dagre";
import { kindOf } from "./doc";
import type { DocNode, Point, StudioDoc } from "./types";

export interface Size {
  width: number;
  height: number;
}

/** Rough card sizes before the DOM has measured them. */
export function estimateSize(doc: StudioDoc, node: DocNode): Size {
  switch (kindOf(doc, node)) {
    case "start":
    case "exit":
      return { width: 150, height: 64 };
    case "router":
    case "fanout":
    case "join":
      return { width: 230, height: 120 };
    case "human":
      return { width: 260, height: 150 };
    default:
      return { width: 280, height: 170 };
  }
}

/**
 * Layered left-to-right layout (dagre, with groups as compound clusters). Returns top-left positions.
 * `sizes` holds measured card sizes when available.
 */
export function autoLayout(doc: StudioDoc, sizes: Record<string, Size> = {}): Record<string, Point> {
  const g = new dagre.graphlib.Graph({ compound: true, multigraph: true });
  g.setGraph({ rankdir: "LR", nodesep: 40, ranksep: 84, edgesep: 24, marginx: 40, marginy: 40, ranker: "network-simplex" });
  g.setDefaultEdgeLabel(() => ({}));
  for (const group of doc.groups) g.setNode(group.uid, { label: group.uid, paddingTop: 36 });
  for (const group of doc.groups) if (group.parent && doc.groups.some((x) => x.uid === group.parent)) g.setParent(group.uid, group.parent);
  for (const node of doc.nodes) {
    const size = sizes[node.id] ?? estimateSize(doc, node);
    g.setNode(node.id, { width: size.width, height: size.height });
    if (node.group && doc.groups.some((x) => x.uid === node.group)) g.setParent(node.id, node.group);
  }
  for (const edge of doc.edges) {
    if (g.hasNode(edge.from) && g.hasNode(edge.to) && edge.from !== edge.to) {
      g.setEdge(edge.from, edge.to, { minlen: 1, weight: edge.attrs.condition?.includes("fail") ? 1 : 2 }, edge.uid);
    }
  }
  dagre.layout(g);
  const positions: Record<string, Point> = {};
  for (const node of doc.nodes) {
    const laid = g.node(node.id) as { x: number; y: number; width: number; height: number } | undefined;
    if (laid) positions[node.id] = { x: Math.round(laid.x - laid.width / 2), y: Math.round(laid.y - laid.height / 2) };
  }
  return positions;
}

/**
 * Positions for nodes that have none: a full layout when most nodes are unplaced, otherwise each new node goes next to a
 * placed neighbour without overlapping existing cards.
 */
export function placeMissing(doc: StudioDoc, sizes: Record<string, Size> = {}): Record<string, Point> | null {
  const missing = doc.nodes.filter((n) => !doc.layout.nodes[n.id]);
  if (missing.length === 0) return null;
  if (missing.length >= Math.max(2, doc.nodes.length * 0.5)) return autoLayout(doc, sizes);

  const placed: Record<string, Point> = { ...doc.layout.nodes };
  const boxes = () =>
    Object.entries(placed).map(([id, p]) => {
      const node = doc.nodes.find((n) => n.id === id);
      const size = sizes[id] ?? (node ? estimateSize(doc, node) : { width: 280, height: 170 });
      return { x: p.x, y: p.y, w: size.width, h: size.height };
    });
  const overlaps = (x: number, y: number, w: number, h: number) => boxes().some((b) => x < b.x + b.w + 30 && x + w + 30 > b.x && y < b.y + b.h + 24 && y + h + 24 > b.y);

  const result: Record<string, Point> = {};
  for (const node of missing) {
    const size = sizes[node.id] ?? estimateSize(doc, node);
    const pred = doc.edges.find((e) => e.to === node.id && placed[e.from]);
    const succ = doc.edges.find((e) => e.from === node.id && placed[e.to]);
    let anchor: Point;
    if (pred) anchor = { x: placed[pred.from].x + 340, y: placed[pred.from].y };
    else if (succ) anchor = { x: placed[succ.to].x - 340, y: placed[succ.to].y };
    else {
      const all = Object.values(placed);
      anchor = all.length ? { x: Math.max(...all.map((p) => p.x)) + 340, y: Math.min(...all.map((p) => p.y)) } : { x: 80, y: 80 };
    }
    let y = anchor.y;
    for (let tries = 0; tries < 40 && overlaps(anchor.x, y, size.width, size.height); tries++) y += size.height + 30;
    placed[node.id] = { x: anchor.x, y };
    result[node.id] = placed[node.id];
  }
  return result;
}
