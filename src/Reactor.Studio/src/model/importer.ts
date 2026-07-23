import type { ParsedGraph } from "../api/types";
import { extractHeader } from "./dot";
import type { Attrs, DocEdge, DocGroup, DocNode, LayoutSidecar, StudioDoc } from "./types";
import { emptyLayout } from "./types";

const groupUid = (graph: ParsedGraph, index: number): string => {
  const sub = graph.subgraphs[index];
  return sub?.name ? `g:${sub.name}` : `g~${index}`;
};

/** Normalizes a possibly partial/foreign layout object. */
export function normalizeLayout(layout: Partial<LayoutSidecar> | null | undefined): LayoutSidecar {
  const base = emptyLayout();
  if (!layout || typeof layout !== "object") return base;
  const nodes: LayoutSidecar["nodes"] = {};
  for (const [id, p] of Object.entries(layout.nodes ?? {})) {
    if (p && Number.isFinite(p.x) && Number.isFinite(p.y)) nodes[id] = { x: p.x, y: p.y };
  }
  return {
    ...base,
    nodes,
    notes: Array.isArray(layout.notes) ? layout.notes.filter((n) => n && typeof n.id === "string") : [],
    viewport: layout.viewport,
    artifacts: layout.artifacts,
    threads: layout.threads,
  };
}

/**
 * Builds a Studio document from the server's parse result. Node attributes keep only explicit keys, except inherited values
 * that the hoisted default blocks would not reproduce (defaults declared mid-file), which become explicit so a rewrite
 * keeps the same effective attributes.
 */
export function docFromParse(graph: ParsedGraph, source: string, layout?: Partial<LayoutSidecar> | null): StudioDoc {
  const groups: DocGroup[] = graph.subgraphs.map((sub) => ({
    uid: groupUid(graph, sub.index),
    name: sub.name,
    parent: sub.parent >= 0 ? groupUid(graph, sub.parent) : null,
    attrs: { ...sub.attributes },
    nodeDefaults: { ...sub.node_defaults },
    edgeDefaults: { ...sub.edge_defaults },
  }));

  const defaultsFor = (subgraph: number | null): Attrs => {
    const chain: number[] = [];
    let current = subgraph;
    const seen = new Set<number>();
    while (current !== null && current >= 0 && !seen.has(current)) {
      seen.add(current);
      chain.unshift(current);
      current = graph.subgraphs[current]?.parent ?? -1;
    }
    const result: Attrs = { ...graph.node_defaults };
    for (const index of chain) Object.assign(result, graph.subgraphs[index]?.node_defaults ?? {});
    return result;
  };

  const nodes: DocNode[] = graph.nodes.map((n) => {
    const explicit = new Set(n.explicit);
    const attrs: Attrs = {};
    const inherited = defaultsFor(n.subgraph);
    for (const [key, value] of Object.entries(n.attributes)) {
      if (explicit.has(key) || inherited[key] !== value) attrs[key] = value;
    }
    return { id: n.id, attrs, group: n.subgraph !== null && n.subgraph >= 0 ? groupUid(graph, n.subgraph) : null };
  });

  const edges: DocEdge[] = graph.edges.map((e, i) => {
    const attrs: Attrs = {};
    for (const [key, value] of Object.entries(e.attributes)) {
      if (graph.edge_defaults[key] !== value) attrs[key] = value;
    }
    return { uid: `e${i}`, from: e.from, to: e.to, attrs };
  });

  const normalized = normalizeLayout(layout);
  // Drop positions of nodes that no longer exist.
  const ids = new Set(nodes.map((n) => n.id));
  for (const id of Object.keys(normalized.nodes)) if (!ids.has(id)) delete normalized.nodes[id];

  return {
    name: graph.name,
    header: extractHeader(source),
    graphAttrs: { ...graph.attributes },
    nodeDefaults: { ...graph.node_defaults },
    edgeDefaults: { ...graph.edge_defaults },
    nodes,
    edges,
    groups,
    layout: normalized,
  };
}
