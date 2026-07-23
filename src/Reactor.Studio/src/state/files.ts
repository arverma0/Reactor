import { api, ApiError } from "../api/client";
import type { LayoutSidecar } from "../model/types";
import { hasBodyComments, writeDot } from "../model/dot";
import { docFromParse } from "../model/importer";
import { autoLayout, placeMissing } from "../model/layout";
import type { StudioDoc } from "../model/types";
import { loadDoc, markSaved, toast, useStudio, signatureOf } from "./store";

const get = useStudio.getState;
const RECENT_KEY = "reactor.recent";

export const STARTER = `digraph untitled {
    graph [goal="Describe what this factory should deliver"]

    start [shape=Mdiamond]
    work  [label="Do the work", prompt="Work toward: $goal"]
    exit  [shape=Msquare]

    start -> work -> exit
}
`;

export function recentFiles(): string[] {
  try {
    return JSON.parse(localStorage.getItem(RECENT_KEY) ?? "[]");
  } catch {
    return [];
  }
}

function remember(path: string) {
  const list = [path, ...recentFiles().filter((p) => p !== path)].slice(0, 8);
  localStorage.setItem(RECENT_KEY, JSON.stringify(list));
}

/** Parses DOT through the server and returns a document (with positions for every node). */
export async function docFromDot(dot: string, layout?: Partial<LayoutSidecar> | null): Promise<StudioDoc> {
  const result = await api.parse(dot);
  if (!result.ok) throw new ApiError(`${result.message}`, 400, result);
  const doc = docFromParse(result.graph, dot, layout);
  const missing = placeMissing(doc);
  return missing ? { ...doc, layout: { ...doc.layout, nodes: { ...doc.layout.nodes, ...missing } } } : doc;
}

export async function openFile(path: string) {
  try {
    const file = await api.readFile(path);
    const doc = await docFromDot(file.dot, file.layout);
    loadDoc(doc, { path: file.path, modified: file.modified, hadBodyComments: hasBodyComments(file.dot) });
    remember(file.path);
    localStorage.setItem("reactor.lastFile", file.path);
    useStudio.setState({ mode: "design" });
  } catch (error) {
    toast(`Could not open ${path}: ${(error as Error).message}`, "error");
  }
}

export async function newDocument(dot = STARTER) {
  try {
    const doc = await docFromDot(dot);
    loadDoc(doc, {});
    localStorage.removeItem("reactor.lastFile");
  } catch (error) {
    toast(`Could not create a pipeline: ${(error as Error).message}`, "error");
  }
}

/** Saves to the current path, or to `path` (save as). Returns the saved path, or null. */
export async function saveDocument(path?: string): Promise<string | null> {
  const { doc, file } = get();
  const target = path ?? file.path;
  if (!target) return null;
  try {
    const sameFile = target === file.path;
    const saved = await api.saveFile(target, writeDot(doc), doc.layout, sameFile ? file.modified : null);
    markSaved(saved.path, saved.modified);
    remember(saved.path);
    localStorage.setItem("reactor.lastFile", saved.path);
    toast(`Saved ${saved.path}`, "success", undefined, 2200);
    return saved.path;
  } catch (error) {
    if ((error as ApiError).status === 409) {
      toast(`${target} changed on disk since you opened it.`, "error", { label: "Overwrite", run: () => void forceSave(target) }, 9000);
    } else {
      toast(`Save failed: ${(error as Error).message}`, "error");
    }
    return null;
  }
}

async function forceSave(path: string) {
  const { doc } = get();
  const saved = await api.saveFile(path, writeDot(doc), doc.layout, null);
  markSaved(saved.path, saved.modified);
  toast(`Saved ${saved.path}`, "success");
}

/** Re-lays out the whole document (undoable). */
export function layoutAll(sizes: Record<string, { width: number; height: number }> = {}) {
  const { doc } = get();
  const positions = autoLayout(doc, sizes);
  return positions;
}

export const currentSignature = () => signatureOf(get().doc);
