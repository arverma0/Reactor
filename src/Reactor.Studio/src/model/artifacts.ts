import { findNode, kindOf, nodeAttr } from "./doc";
import { KINDS } from "./handlers";
import { lineColor } from "./palette";
import type { DocNode, StudioDoc } from "./types";

export interface ArtifactOutput {
  name: string;
  /** File path for file artifacts; undefined for text artifacts (the stage's response / tool stdout). */
  path?: string;
}

const NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

export const isArtifactName = (name: string) => NAME.test(name);

/** `design:docs/DESIGN.md, verdict` → [{name: design, path: docs/DESIGN.md}, {name: verdict}] */
export function parseOutputs(text: string | undefined): ArtifactOutput[] {
  if (!text) return [];
  return text
    .split(",")
    .map((entry) => entry.trim())
    .filter(Boolean)
    .map((entry) => {
      const colon = entry.indexOf(":");
      if (colon < 0) return { name: entry };
      const path = entry.slice(colon + 1).trim();
      return path ? { name: entry.slice(0, colon).trim(), path } : { name: entry.slice(0, colon).trim() };
    });
}

export function formatOutputs(outputs: ArtifactOutput[]): string | undefined {
  const text = outputs.map((o) => (o.path ? `${o.name}:${o.path}` : o.name)).join(", ");
  return text || undefined;
}

export function parseInputs(text: string | undefined): string[] {
  if (!text) return [];
  return [...new Set(text.split(",").map((s) => s.trim()).filter(Boolean))];
}

export function formatInputs(inputs: string[]): string | undefined {
  const text = [...new Set(inputs)].join(", ");
  return text || undefined;
}

export const nodeOutputs = (doc: StudioDoc, node: DocNode) => (KINDS[kindOf(doc, node)].pins ? parseOutputs(nodeAttr(doc, node, "outputs")) : []);
export const nodeInputs = (doc: StudioDoc, node: DocNode) => (KINDS[kindOf(doc, node)].pins ? parseInputs(nodeAttr(doc, node, "inputs")) : []);

export interface DataLink {
  id: string;
  name: string;
  from: string;
  to: string;
  color: string;
}

export interface ArtifactInfo {
  name: string;
  color: string;
  producers: { node: string; path?: string }[];
  consumers: string[];
}

/** Every artifact in the document with its producers and consumers. */
export function artifactIndex(doc: StudioDoc): Map<string, ArtifactInfo> {
  const index = new Map<string, ArtifactInfo>();
  const entry = (name: string) => {
    let info = index.get(name);
    if (!info) {
      info = { name, color: lineColor(name, doc.layout.artifacts?.[name]?.color), producers: [], consumers: [] };
      index.set(name, info);
    }
    return info;
  };
  for (const node of doc.nodes) {
    for (const output of nodeOutputs(doc, node)) entry(output.name).producers.push({ node: node.id, path: output.path });
    for (const input of nodeInputs(doc, node)) entry(input).consumers.push(node.id);
  }
  return index;
}

/** Data wires: every producer of an artifact to every consumer of it. */
export function dataLinks(doc: StudioDoc): DataLink[] {
  const links: DataLink[] = [];
  for (const info of artifactIndex(doc).values()) {
    for (const producer of info.producers) {
      for (const consumer of info.consumers) {
        if (producer.node === consumer) continue;
        links.push({ id: `data:${info.name}:${producer.node}->${consumer}`, name: info.name, from: producer.node, to: consumer, color: info.color });
      }
    }
  }
  return links;
}

/** Adds an input to a node (no-op when present). */
export function withInput(doc: StudioDoc, nodeId: string, name: string): string | undefined {
  const node = findNode(doc, nodeId);
  if (!node) return undefined;
  const inputs = parseInputs(node.attrs.inputs);
  return inputs.includes(name) ? node.attrs.inputs : formatInputs([...inputs, name]);
}
