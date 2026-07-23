import { create } from "zustand";
import type { ModelDescriptor, ServerInfo } from "../api/types";
import { writeDot } from "../model/dot";
import type { Diagnostic, Effective, StudioDoc } from "../model/types";
import { emptyDoc } from "../model/types";

export type Mode = "design" | "tunnel" | "flights";
export type LeftPanel = "library" | "files" | "outline" | "models" | null;
export type BottomPanel = "code" | "problems" | "prompt" | null;

export interface Selection {
  nodes: string[];
  edges: string[];
  group: string | null;
  note: string | null;
}

export const emptySelection: Selection = { nodes: [], edges: [], group: null, note: null };

export interface Analysis {
  status: "idle" | "pending" | "ok" | "offline";
  diagnostics: Diagnostic[];
  valid: boolean;
  parseError: { message: string; line: number; column: number } | null;
  effective: Record<string, Effective>;
  /** DOT text the analysis describes. */
  dot: string;
}

export interface FileState {
  path: string | null;
  modified: string | null;
  /** Signature of the last saved/opened state, for the dirty flag. */
  savedSignature: string;
  /** The source had comments inside the graph body (lost on a visual edit). */
  hadBodyComments: boolean;
}

export interface Toast {
  id: number;
  text: string;
  kind: "info" | "success" | "error";
  action?: { label: string; run: () => void };
}

interface StudioState {
  info: ServerInfo | null;
  models: ModelDescriptor[];
  modelsError: string | null;
  connected: boolean;
  needsToken: boolean;

  doc: StudioDoc;
  file: FileState;
  dirty: boolean;
  past: StudioDoc[];
  future: StudioDoc[];
  coalesce: { key: string; at: number } | null;
  analysis: Analysis;

  selection: Selection;
  mode: Mode;
  leftPanel: LeftPanel;
  bottomPanel: BottomPanel;
  inspectorOpen: boolean;
  layers: { data: boolean; notes: boolean; minimap: boolean };
  paletteOpen: boolean;
  architectOpen: boolean;
  /** Questions waiting on a person across live runs. */
  waiting: number;
  saveAsOpen: boolean;
  runOpen: boolean;
  toasts: Toast[];
  /** Incremented to ask the canvas to fit the view (smart: keeps cards legible). */
  fitRequest: number;
  /** Incremented to fit the entire graph regardless of zoom. */
  fitAllRequest: number;
  /** Incremented to ask the canvas to run auto-layout. */
  layoutRequest: number;
  /** Node to focus (center) on the canvas. */
  focusRequest: { id: string; at: number } | null;
}

export const signatureOf = (doc: StudioDoc) => writeDot(doc) + "\u0000" + JSON.stringify(doc.layout);

const MAX_HISTORY = 200;
const COALESCE_MS = 900;

export const useStudio = create<StudioState>()(() => ({
  info: null,
  models: [],
  modelsError: null,
  connected: false,
  needsToken: false,
  doc: emptyDoc("untitled"),
  file: { path: null, modified: null, savedSignature: signatureOf(emptyDoc("untitled")), hadBodyComments: false },
  dirty: false,
  past: [],
  future: [],
  coalesce: null,
  analysis: { status: "idle", diagnostics: [], valid: true, parseError: null, effective: {}, dot: "" },
  selection: emptySelection,
  mode: "design",
  leftPanel: "library",
  bottomPanel: null,
  inspectorOpen: true,
  layers: { data: true, notes: true, minimap: true },
  paletteOpen: false,
  architectOpen: false,
  waiting: 0,
  saveAsOpen: false,
  runOpen: false,
  toasts: [],
  fitRequest: 0,
  fitAllRequest: 0,
  layoutRequest: 0,
  focusRequest: null,
}));

const set = useStudio.setState;
const get = useStudio.getState;

/**
 * Applies a document change as one undoable step. Changes sharing a `coalesce` key within a short window (typing in a
 * field, dragging) merge into a single step.
 */
export function applyDoc(update: (doc: StudioDoc) => StudioDoc, options: { coalesce?: string; select?: Partial<Selection> } = {}) {
  const state = get();
  const next = update(state.doc);
  if (next === state.doc && !options.select) return;
  const now = Date.now();
  const merge = options.coalesce !== undefined && state.coalesce?.key === options.coalesce && now - state.coalesce.at < COALESCE_MS;
  const past = next === state.doc ? state.past : merge ? state.past : [...state.past.slice(-(MAX_HISTORY - 1)), state.doc];
  set({
    doc: next,
    past,
    future: next === state.doc ? state.future : [],
    coalesce: options.coalesce !== undefined ? { key: options.coalesce, at: now } : null,
    dirty: signatureOf(next) !== state.file.savedSignature,
    selection: options.select ? { ...emptySelection, ...options.select } : pruneSelection(state.selection, next),
  });
}

function pruneSelection(selection: Selection, doc: StudioDoc): Selection {
  const nodes = selection.nodes.filter((id) => doc.nodes.some((n) => n.id === id));
  const edges = selection.edges.filter((uid) => doc.edges.some((e) => e.uid === uid));
  const group = selection.group && doc.groups.some((g) => g.uid === selection.group) ? selection.group : null;
  const note = selection.note && doc.layout.notes.some((n) => n.id === selection.note) ? selection.note : null;
  if (nodes.length === selection.nodes.length && edges.length === selection.edges.length && group === selection.group && note === selection.note) return selection;
  return { nodes, edges, group, note };
}

export function undo() {
  const { past, doc, future, file, selection } = get();
  if (past.length === 0) return;
  const previous = past[past.length - 1];
  set({ doc: previous, past: past.slice(0, -1), future: [doc, ...future], coalesce: null, dirty: signatureOf(previous) !== file.savedSignature, selection: pruneSelection(selection, previous) });
}

export function redo() {
  const { past, doc, future, file, selection } = get();
  if (future.length === 0) return;
  const next = future[0];
  set({ doc: next, past: [...past, doc], future: future.slice(1), coalesce: null, dirty: signatureOf(next) !== file.savedSignature, selection: pruneSelection(selection, next) });
}

/** Replaces the document (open/new/import); history resets. */
export function loadDoc(doc: StudioDoc, file: Partial<FileState> = {}) {
  const signature = signatureOf(doc);
  set({
    doc,
    past: [],
    future: [],
    coalesce: null,
    file: { path: file.path ?? null, modified: file.modified ?? null, savedSignature: file.savedSignature ?? signature, hadBodyComments: file.hadBodyComments ?? false },
    dirty: file.savedSignature !== undefined ? file.savedSignature !== signature : false,
    selection: emptySelection,
    analysis: { status: "idle", diagnostics: [], valid: true, parseError: null, effective: {}, dot: "" },
    fitRequest: get().fitRequest + 1,
  });
}

export function markSaved(path: string, modified: string) {
  const { doc, file } = get();
  set({ file: { ...file, path, modified, savedSignature: signatureOf(doc), hadBodyComments: false }, dirty: false });
}

export function select(selection: Partial<Selection>) {
  const next = { ...emptySelection, ...selection };
  const current = get().selection;
  if (
    current.group === next.group &&
    current.note === next.note &&
    current.nodes.length === next.nodes.length &&
    current.edges.length === next.edges.length &&
    current.nodes.every((id, i) => id === next.nodes[i]) &&
    current.edges.every((id, i) => id === next.edges[i])
  ) {
    return;
  }
  set({ selection: next });
}

let toastId = 0;
export function toast(text: string, kind: Toast["kind"] = "info", action?: Toast["action"], ttl = 4200) {
  const id = ++toastId;
  set({ toasts: [...get().toasts.slice(-3), { id, text, kind, action }] });
  setTimeout(() => set({ toasts: get().toasts.filter((t) => t.id !== id) }), ttl);
}

export const dismissToast = (id: number) => set({ toasts: get().toasts.filter((t) => t.id !== id) });

export const setMode = (mode: Mode) => set({ mode });
export const setLeftPanel = (panel: LeftPanel) => set({ leftPanel: get().leftPanel === panel ? null : panel });
export const setBottomPanel = (panel: BottomPanel) => set({ bottomPanel: get().bottomPanel === panel ? null : panel });
export const requestFit = () => set({ fitAllRequest: get().fitAllRequest + 1 });
export const requestLayout = () => set({ layoutRequest: get().layoutRequest + 1 });
export const focusNode = (id: string) => set({ focusRequest: { id, at: Date.now() }, selection: { ...emptySelection, nodes: [id] } });
