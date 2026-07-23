import { addEdge, addGroup, addNode, findNode, newNodeOfKind, newNoteId, removeEdges, removeGroup, removeNodes, uniqueNodeId } from "../model/doc";
import type { NodeKind } from "../model/handlers";
import type { DocEdge, DocNode } from "../model/types";
import { applyDoc, select, toast, undo, useStudio } from "./store";

const get = useStudio.getState;

/** Deletes the selected stages, connections, note, or group (one undo step). Deleting a group keeps its stages. */
export function deleteSelection() {
  const { selection } = get();
  if (selection.nodes.length === 0 && selection.edges.length === 0 && !selection.note && !selection.group) return;
  applyDoc((d) => {
    let next = removeEdges(d, selection.edges);
    next = removeNodes(next, selection.nodes);
    if (selection.note) next = { ...next, layout: { ...next.layout, notes: next.layout.notes.filter((n) => n.id !== selection.note) } };
    if (selection.group && selection.nodes.length === 0) next = removeGroup(next, selection.group);
    return next;
  });
  const count = selection.nodes.length + selection.edges.length;
  if (count > 1) toast(`Deleted ${count} items.`, "info", { label: "Undo", run: undo }, 3500);
}

interface Clipboard {
  nodes: DocNode[];
  edges: DocEdge[];
  positions: Record<string, { x: number; y: number }>;
}

let clipboard: Clipboard | null = null;

export function copySelection() {
  const { selection, doc } = get();
  if (selection.nodes.length === 0) return false;
  const ids = new Set(selection.nodes);
  clipboard = {
    nodes: doc.nodes.filter((n) => ids.has(n.id)).map((n) => ({ ...n, attrs: { ...n.attrs } })),
    edges: doc.edges.filter((e) => ids.has(e.from) && ids.has(e.to)).map((e) => ({ ...e, attrs: { ...e.attrs } })),
    positions: Object.fromEntries(selection.nodes.map((id) => [id, doc.layout.nodes[id] ?? { x: 0, y: 0 }])),
  };
  try {
    void navigator.clipboard?.writeText(JSON.stringify({ reactor: "nodes", ...clipboard }));
  } catch {
    // clipboard access is optional
  }
  return true;
}

export function pasteClipboard(offset = { x: 48, y: 48 }) {
  if (!clipboard) return;
  const source = clipboard;
  const created: string[] = [];
  applyDoc((d) => {
    const taken = new Set(d.nodes.map((n) => n.id));
    const rename = new Map<string, string>();
    let next = d;
    for (const node of source.nodes) {
      const id = uniqueNodeId(next, node.id, taken);
      taken.add(id);
      rename.set(node.id, id);
      const p = source.positions[node.id];
      const groupExists = node.group && next.groups.some((g) => g.uid === node.group);
      next = addNode(next, { id, attrs: { ...node.attrs }, group: groupExists ? node.group : null }, { x: p.x + offset.x, y: p.y + offset.y });
      created.push(id);
    }
    for (const edge of source.edges) next = addEdge(next, rename.get(edge.from)!, rename.get(edge.to)!, { ...edge.attrs }).doc;
    return next;
  });
  select({ nodes: created });
}

export function duplicateSelection() {
  if (copySelection()) pasteClipboard();
}

/** Adds a stage of `kind` near the middle of the visible canvas (or next to the selected stage). */
export function addNodeOfKind(kind: NodeKind, at?: { x: number; y: number }) {
  const { doc, selection } = get();
  let position = at;
  if (!position) {
    const anchor = selection.nodes[0] ? doc.layout.nodes[selection.nodes[0]] : undefined;
    position = anchor ? { x: anchor.x + 340, y: anchor.y } : centerOfView();
  }
  let created = "";
  const anchorId = selection.nodes.length === 1 && !at ? selection.nodes[0] : null;
  applyDoc((d) => {
    const node = newNodeOfKind(d, kind);
    created = node.id;
    let next = addNode(d, node, { x: Math.round(position!.x), y: Math.round(position!.y) });
    const anchorNode = anchorId ? findNode(next, anchorId) : undefined;
    if (anchorNode && kind !== "start") next = addEdge(next, anchorNode.id, node.id).doc;
    return next;
  });
  select({ nodes: [created] });
}

export function addNote() {
  const id = newNoteId();
  const p = centerOfView();
  applyDoc((d) => ({ ...d, layout: { ...d.layout, notes: [...d.layout.notes, { id, x: Math.round(p.x), y: Math.round(p.y), w: 240, h: 120, text: "" }] } }));
  select({ note: id });
}

export function groupSelection(label = "New group") {
  const { selection } = get();
  if (selection.nodes.length === 0) {
    toast("Select the stages to group first.", "info");
    return;
  }
  let uid = "";
  applyDoc((d) => {
    const result = addGroup(d, label, selection.nodes);
    uid = result.group.uid;
    return result.doc;
  });
  select({ group: uid });
}

export function selectAll() {
  const { doc } = get();
  select({ nodes: doc.nodes.map((n) => n.id) });
}

/** Viewport center in flow coordinates (set by the canvas). */
export let centerOfView = () => ({ x: 120, y: 120 });
export const setCenterOfView = (fn: () => { x: number; y: number }) => (centerOfView = fn);
