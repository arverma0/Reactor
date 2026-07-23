import type { Attrs, StudioDoc } from "./types";

export interface AttrChange {
  key: string;
  before?: string;
  after?: string;
}

export interface DocDiff {
  added: string[];
  removed: string[];
  changed: { id: string; changes: AttrChange[] }[];
  edgesAdded: { from: string; to: string; attrs: Attrs }[];
  edgesRemoved: { from: string; to: string; attrs: Attrs }[];
  edgesChanged: { from: string; to: string; changes: AttrChange[] }[];
  graph: AttrChange[];
  /** Number of individual differences (nodes, edges, and graph attributes). */
  total: number;
}

function attrChanges(a: Attrs, b: Attrs): AttrChange[] {
  const changes: AttrChange[] = [];
  for (const key of new Set([...Object.keys(a), ...Object.keys(b)])) {
    if (a[key] !== b[key]) changes.push({ key, before: a[key], after: b[key] });
  }
  return changes;
}

const groupLabel = (doc: StudioDoc, uid: string | null) => {
  const group = uid ? doc.groups.find((g) => g.uid === uid) : undefined;
  return group ? (group.attrs.label ?? group.name ?? "") : "";
};

/** What changed from `a` to `b`, as the engine sees it (attributes, group membership, and edges; not positions). */
export function diffDocs(a: StudioDoc, b: StudioDoc): DocDiff {
  const before = new Map(a.nodes.map((n) => [n.id, n]));
  const after = new Map(b.nodes.map((n) => [n.id, n]));
  const added = b.nodes.filter((n) => !before.has(n.id)).map((n) => n.id);
  const removed = a.nodes.filter((n) => !after.has(n.id)).map((n) => n.id);
  const changed: DocDiff["changed"] = [];
  for (const node of b.nodes) {
    const old = before.get(node.id);
    if (!old) continue;
    const changes = attrChanges(old.attrs, node.attrs);
    const ga = groupLabel(a, old.group);
    const gb = groupLabel(b, node.group);
    if (ga !== gb) changes.push({ key: "group", before: ga || undefined, after: gb || undefined });
    if (changes.length > 0) changed.push({ id: node.id, changes });
  }

  // Edges have no identity: pair them by endpoints, in declaration order.
  const pool = new Map<string, Attrs[]>();
  for (const e of a.edges) pool.set(`${e.from}\u0000${e.to}`, [...(pool.get(`${e.from}\u0000${e.to}`) ?? []), e.attrs]);
  const edgesAdded: DocDiff["edgesAdded"] = [];
  const edgesChanged: DocDiff["edgesChanged"] = [];
  for (const e of b.edges) {
    const key = `${e.from}\u0000${e.to}`;
    const candidates = pool.get(key);
    if (!candidates || candidates.length === 0) {
      edgesAdded.push({ from: e.from, to: e.to, attrs: e.attrs });
      continue;
    }
    const exact = candidates.findIndex((attrs) => attrChanges(attrs, e.attrs).length === 0);
    const [match] = candidates.splice(exact >= 0 ? exact : 0, 1);
    const changes = attrChanges(match, e.attrs);
    if (changes.length > 0) edgesChanged.push({ from: e.from, to: e.to, changes });
  }
  const edgesRemoved: DocDiff["edgesRemoved"] = [];
  for (const [key, rest] of pool) {
    const [from, to] = key.split("\u0000");
    for (const attrs of rest) edgesRemoved.push({ from, to, attrs });
  }

  const graph = attrChanges(a.graphAttrs, b.graphAttrs);
  const defaults = attrChanges(a.nodeDefaults, b.nodeDefaults).map((c) => ({ ...c, key: `node.${c.key}` }));
  const edgeDefaults = attrChanges(a.edgeDefaults, b.edgeDefaults).map((c) => ({ ...c, key: `edge.${c.key}` }));
  graph.push(...defaults, ...edgeDefaults);

  return {
    added,
    removed,
    changed,
    edgesAdded,
    edgesRemoved,
    edgesChanged,
    graph,
    total: added.length + removed.length + changed.length + edgesAdded.length + edgesRemoved.length + edgesChanged.length + graph.length,
  };
}

/** One-line summary, e.g. "2 stages changed (implement: prompt, llm_model), 1 added". */
export function describeDiff(diff: DocDiff, limit = 3): string {
  if (diff.total === 0) return "identical";
  const parts: string[] = [];
  if (diff.changed.length > 0) {
    const shown = diff.changed
      .slice(0, limit)
      .map((c) => `${c.id}: ${c.changes.map((x) => x.key).join(", ")}`)
      .join("; ");
    parts.push(`${diff.changed.length} stage${diff.changed.length > 1 ? "s" : ""} changed (${shown}${diff.changed.length > limit ? "; …" : ""})`);
  }
  if (diff.added.length > 0) parts.push(`${diff.added.length} added (${diff.added.slice(0, limit).join(", ")}${diff.added.length > limit ? ", …" : ""})`);
  if (diff.removed.length > 0) parts.push(`${diff.removed.length} removed (${diff.removed.slice(0, limit).join(", ")}${diff.removed.length > limit ? ", …" : ""})`);
  const edges = diff.edgesAdded.length + diff.edgesRemoved.length + diff.edgesChanged.length;
  if (edges > 0) parts.push(`${edges} route${edges > 1 ? "s" : ""} changed`);
  if (diff.graph.length > 0) parts.push(`graph: ${diff.graph.map((g) => g.key).slice(0, limit).join(", ")}`);
  return parts.join(" · ");
}
