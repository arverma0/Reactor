import { KINDS, kindOfHandler, resolveHandler, type NodeKind } from "./handlers";
import type { Attrs, DocEdge, DocGroup, DocNode, HandlerType, StudioDoc } from "./types";

/** Mirrors DotParser.DeriveClassName: lowercase, whitespace → '-', other non-alphanumerics (except '-') removed. */
export function deriveClassName(label: string): string {
  let out = "";
  for (const c of label.trim().toLowerCase()) {
    if ((c >= "a" && c <= "z") || (c >= "0" && c <= "9") || c === "-") out += c;
    else if (/\s/.test(c)) out += "-";
  }
  return out;
}

export const IDENT = /^[A-Za-z_][A-Za-z0-9_]*$/;

export function groupById(doc: StudioDoc, uid: string | null | undefined): DocGroup | undefined {
  return uid ? doc.groups.find((g) => g.uid === uid) : undefined;
}

/** Group chain from innermost to outermost. */
export function groupChain(doc: StudioDoc, uid: string | null | undefined): DocGroup[] {
  const chain: DocGroup[] = [];
  let current = groupById(doc, uid);
  const seen = new Set<string>();
  while (current && !seen.has(current.uid)) {
    seen.add(current.uid);
    chain.push(current);
    current = groupById(doc, current.parent);
  }
  return chain;
}

/** Effective node attribute: explicit, then innermost-to-outermost group defaults, then graph defaults. */
export function nodeAttr(doc: StudioDoc, node: DocNode, key: string): string | undefined {
  if (key in node.attrs) return node.attrs[key];
  for (const group of groupChain(doc, node.group)) {
    if (key in group.nodeDefaults) return group.nodeDefaults[key];
  }
  return key in doc.nodeDefaults ? doc.nodeDefaults[key] : undefined;
}

/** All effective attributes of a node. */
export function nodeAttrs(doc: StudioDoc, node: DocNode): Attrs {
  const result: Attrs = { ...doc.nodeDefaults };
  for (const group of [...groupChain(doc, node.group)].reverse()) Object.assign(result, group.nodeDefaults);
  return Object.assign(result, node.attrs);
}

export function edgeAttr(doc: StudioDoc, edge: DocEdge, key: string): string | undefined {
  return key in edge.attrs ? edge.attrs[key] : doc.edgeDefaults[key];
}

export function handlerOf(doc: StudioDoc, node: DocNode): HandlerType {
  return resolveHandler(node, (n, k) => nodeAttr(doc, n, k));
}

export function kindOf(doc: StudioDoc, node: DocNode): NodeKind {
  return kindOfHandler(handlerOf(doc, node));
}

export function nodeLabel(doc: StudioDoc, node: DocNode): string {
  const label = nodeAttr(doc, node, "label");
  return label && label.length > 0 ? label : node.id;
}

/** Explicit `class` entries plus classes derived from enclosing group labels (outermost first), de-duplicated. */
export function nodeClasses(doc: StudioDoc, node: DocNode): string[] {
  const classes: string[] = [];
  const explicit = nodeAttr(doc, node, "class");
  if (explicit) {
    for (const c of explicit.split(/[,\s]+/)) if (c && !classes.includes(c)) classes.push(c);
  }
  for (const group of [...groupChain(doc, node.group)].reverse()) {
    const label = group.attrs.label;
    const derived = label ? deriveClassName(label) : "";
    if (derived && !classes.includes(derived)) classes.push(derived);
  }
  return classes;
}

export const findNode = (doc: StudioDoc, id: string) => doc.nodes.find((n) => n.id === id);
export const outgoing = (doc: StudioDoc, id: string) => doc.edges.filter((e) => e.from === id);
export const incoming = (doc: StudioDoc, id: string) => doc.edges.filter((e) => e.to === id);

export function startNode(doc: StudioDoc): DocNode | undefined {
  return doc.nodes.find((n) => handlerOf(doc, n) === "start");
}

/** A valid, unused node id derived from `base`. */
export function uniqueNodeId(doc: StudioDoc, base: string, taken?: Set<string>): string {
  let clean = base.trim().toLowerCase().replace(/[^a-z0-9_]+/g, "_").replace(/^_+|_+$/g, "");
  if (!clean) clean = "stage";
  if (!/^[a-z_]/.test(clean)) clean = "n_" + clean;
  if (clean.length > 40) clean = clean.slice(0, 40).replace(/_+$/, "");
  const used = taken ?? new Set(doc.nodes.map((n) => n.id));
  if (!used.has(clean)) return clean;
  for (let i = 2; ; i++) {
    const candidate = `${clean}_${i}`;
    if (!used.has(candidate)) return candidate;
  }
}

let edgeCounter = 0;
export const newEdgeUid = () => `e${Date.now().toString(36)}${(edgeCounter++).toString(36)}`;

let noteCounter = 0;
export const newNoteId = () => `note${Date.now().toString(36)}${(noteCounter++).toString(36)}`;

// ---------- immutable operations ----------

function setAttr(attrs: Attrs, key: string, value: string | undefined): Attrs {
  if (value === undefined) {
    if (!(key in attrs)) return attrs;
    const next = { ...attrs };
    delete next[key];
    return next;
  }
  if (attrs[key] === value) return attrs;
  return { ...attrs, [key]: value };
}

export function setNodeAttr(doc: StudioDoc, id: string, key: string, value: string | undefined): StudioDoc {
  return {
    ...doc,
    nodes: doc.nodes.map((n) => (n.id === id ? { ...n, attrs: setAttr(n.attrs, key, value) } : n)),
  };
}

export function setNodeAttrs(doc: StudioDoc, id: string, patch: Record<string, string | undefined>): StudioDoc {
  return {
    ...doc,
    nodes: doc.nodes.map((n) => {
      if (n.id !== id) return n;
      let attrs = n.attrs;
      for (const [k, v] of Object.entries(patch)) attrs = setAttr(attrs, k, v);
      return { ...n, attrs };
    }),
  };
}

export function setEdgeAttr(doc: StudioDoc, uid: string, key: string, value: string | undefined): StudioDoc {
  return { ...doc, edges: doc.edges.map((e) => (e.uid === uid ? { ...e, attrs: setAttr(e.attrs, key, value) } : e)) };
}

export function setEdgeAttrs(doc: StudioDoc, uid: string, patch: Record<string, string | undefined>): StudioDoc {
  return {
    ...doc,
    edges: doc.edges.map((e) => {
      if (e.uid !== uid) return e;
      let attrs = e.attrs;
      for (const [k, v] of Object.entries(patch)) attrs = setAttr(attrs, k, v);
      return { ...e, attrs };
    }),
  };
}

export function setGraphAttr(doc: StudioDoc, key: string, value: string | undefined): StudioDoc {
  return { ...doc, graphAttrs: setAttr(doc.graphAttrs, key, value) };
}

export function addNode(doc: StudioDoc, node: DocNode, position?: { x: number; y: number }): StudioDoc {
  return {
    ...doc,
    nodes: [...doc.nodes, node],
    layout: position ? { ...doc.layout, nodes: { ...doc.layout.nodes, [node.id]: position } } : doc.layout,
  };
}

export function newNodeOfKind(doc: StudioDoc, kind: NodeKind, label?: string): DocNode {
  const meta = KINDS[kind];
  const attrs: Attrs = { ...meta.defaults };
  if (label !== undefined) attrs.label = label;
  const base = kind === "start" ? "start" : kind === "exit" ? "exit" : attrs.label ?? meta.idPrefix;
  return { id: uniqueNodeId(doc, base), attrs, group: null };
}

export function removeNodes(doc: StudioDoc, ids: Iterable<string>): StudioDoc {
  const gone = new Set(ids);
  if (gone.size === 0) return doc;
  const nodes = { ...doc.layout.nodes };
  for (const id of gone) delete nodes[id];
  return {
    ...doc,
    nodes: doc.nodes.filter((n) => !gone.has(n.id)),
    edges: doc.edges.filter((e) => !gone.has(e.from) && !gone.has(e.to)),
    layout: { ...doc.layout, nodes },
  };
}

export function addEdge(doc: StudioDoc, from: string, to: string, attrs: Attrs = {}): { doc: StudioDoc; edge: DocEdge } {
  const edge: DocEdge = { uid: newEdgeUid(), from, to, attrs };
  return { doc: { ...doc, edges: [...doc.edges, edge] }, edge };
}

export function removeEdges(doc: StudioDoc, uids: Iterable<string>): StudioDoc {
  const gone = new Set(uids);
  if (gone.size === 0) return doc;
  return { ...doc, edges: doc.edges.filter((e) => !gone.has(e.uid)) };
}

/** Moves an edge within the outgoing list of its source (order matters for human gate options). */
export function moveEdge(doc: StudioDoc, uid: string, direction: -1 | 1): StudioDoc {
  const edge = doc.edges.find((e) => e.uid === uid);
  if (!edge) return doc;
  const siblings = doc.edges.filter((e) => e.from === edge.from);
  const index = siblings.indexOf(edge);
  const swapWith = siblings[index + direction];
  if (!swapWith) return doc;
  const edges = [...doc.edges];
  const a = edges.indexOf(edge);
  const b = edges.indexOf(swapWith);
  [edges[a], edges[b]] = [edges[b], edges[a]];
  return { ...doc, edges };
}

const ID_REFERENCE_KEYS = ["retry_target", "fallback_retry_target", "human.default_choice"];

/**
 * Renames a node and every reference to it: edges, layout, retry targets, human default choices, and `#id`
 * selectors in the model stylesheet.
 */
export function renameNode(doc: StudioDoc, oldId: string, newId: string): StudioDoc {
  if (oldId === newId || !findNode(doc, oldId) || findNode(doc, newId)) return doc;
  const remap = (attrs: Attrs): Attrs => {
    let next = attrs;
    for (const key of ID_REFERENCE_KEYS) if (next[key] === oldId) next = { ...next, [key]: newId };
    return next;
  };
  const nodes = doc.nodes.map((n) => ({ ...n, id: n.id === oldId ? newId : n.id, attrs: remap(n.attrs) }));
  const edges = doc.edges.map((e) => (e.from === oldId || e.to === oldId ? { ...e, from: e.from === oldId ? newId : e.from, to: e.to === oldId ? newId : e.to } : e));
  let graphAttrs = remap(doc.graphAttrs);
  const sheet = graphAttrs.model_stylesheet;
  if (sheet && sheet.includes("#" + oldId)) {
    graphAttrs = { ...graphAttrs, model_stylesheet: sheet.replace(new RegExp(`#${oldId}(?![A-Za-z0-9_])`, "g"), "#" + newId) };
  }
  const layoutNodes = { ...doc.layout.nodes };
  if (layoutNodes[oldId]) {
    layoutNodes[newId] = layoutNodes[oldId];
    delete layoutNodes[oldId];
  }
  return { ...doc, nodes, edges, graphAttrs, layout: { ...doc.layout, nodes: layoutNodes } };
}

export function setNodeGroup(doc: StudioDoc, ids: Iterable<string>, group: string | null): StudioDoc {
  const set = new Set(ids);
  return { ...doc, nodes: doc.nodes.map((n) => (set.has(n.id) ? { ...n, group } : n)) };
}

/** A unique subgraph name derived from a label (`Deep thinking` → `cluster_deep_thinking`). */
export function uniqueGroupName(doc: StudioDoc, label: string): string {
  const base = "cluster_" + (label.trim().toLowerCase().replace(/[^a-z0-9_]+/g, "_").replace(/^_+|_+$/g, "") || "group");
  const used = new Set(doc.groups.map((g) => g.name));
  if (!used.has(base)) return base;
  for (let i = 2; ; i++) if (!used.has(`${base}_${i}`)) return `${base}_${i}`;
}

export function addGroup(doc: StudioDoc, label: string, memberIds: string[], parent: string | null = null): { doc: StudioDoc; group: DocGroup } {
  const name = uniqueGroupName(doc, label);
  const group: DocGroup = { uid: "g:" + name, name, parent, attrs: { label }, nodeDefaults: {}, edgeDefaults: {} };
  const withGroup = { ...doc, groups: [...doc.groups, group] };
  return { doc: setNodeGroup(withGroup, memberIds, group.uid), group };
}

/** Removes a group; its members and child groups move to the group's parent. */
export function removeGroup(doc: StudioDoc, uid: string): StudioDoc {
  const group = groupById(doc, uid);
  if (!group) return doc;
  return {
    ...doc,
    groups: doc.groups.filter((g) => g.uid !== uid).map((g) => (g.parent === uid ? { ...g, parent: group.parent } : g)),
    nodes: doc.nodes.map((n) => (n.group === uid ? { ...n, group: group.parent } : n)),
  };
}

export function updateGroup(doc: StudioDoc, uid: string, patch: Partial<Pick<DocGroup, "attrs" | "nodeDefaults" | "name">>): StudioDoc {
  return { ...doc, groups: doc.groups.map((g) => (g.uid === uid ? { ...g, ...patch } : g)) };
}

export function moveNodes(doc: StudioDoc, positions: Record<string, { x: number; y: number }>): StudioDoc {
  return { ...doc, layout: { ...doc.layout, nodes: { ...doc.layout.nodes, ...positions } } };
}
