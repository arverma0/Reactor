import { IDENT } from "./doc";
import type { Attrs, DocGroup, DocNode, StudioDoc } from "./types";

const KEYWORDS = new Set(["graph", "digraph", "node", "edge", "subgraph", "strict"]);
const UNQUOTED = /^(?:-?\d+|-?\d*\.\d+|\d+(?:ms|s|m|h|d)|[A-Za-z_][A-Za-z0-9_.:-]*)$/;
const KEY = /^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$/;
const INDENT = "    ";
const MAX_INLINE = 110;

/** Escapes a value for a DOT quoted string (newlines become \n). */
export function quote(value: string): string {
  let out = '"';
  for (const c of value) {
    if (c === '"') out += '\\"';
    else if (c === "\\") out += "\\\\";
    else if (c === "\n") out += "\\n";
    else if (c === "\t") out += "\\t";
    else if (c !== "\r") out += c;
  }
  return out + '"';
}

export const isKeyword = (text: string) => KEYWORDS.has(text.toLowerCase());

/** A value as DOT: bare when the parser accepts it unquoted, else quoted. */
export function formatValue(value: string): string {
  return UNQUOTED.test(value) && !isKeyword(value) ? value : quote(value);
}

/** Multi-line values are written as `"line 1\n" + "line 2"` so the file stays readable and round-trips exactly. */
function formatValueAt(value: string, continuationIndent: string): string {
  const normalized = value.replace(/\r\n?/g, "\n");
  if (!normalized.includes("\n") || normalized.length < 2) return formatValue(normalized);
  const lines = normalized.split("\n");
  const pieces = lines.map((line, i) => (i < lines.length - 1 ? line + "\n" : line)).filter((piece) => piece.length > 0);
  return pieces.map((piece, i) => (i === 0 ? quote(piece) : `${continuationIndent}+ ${quote(piece)}`)).join("\n");
}

export function formatId(id: string): string {
  return IDENT.test(id) && !isKeyword(id) ? id : quote(id);
}

function formatKey(key: string): string {
  return KEY.test(key) ? key : quote(key);
}

/** `[a=1, b="x"]` on one line when short; otherwise one attribute per line. */
function attrBlock(attrs: Attrs, indent: string, forceMultiline = false): string {
  const entries = Object.entries(attrs);
  if (entries.length === 0) return "";
  const inline = entries.map(([k, v]) => `${formatKey(k)}=${formatValue(v)}`).join(", ");
  const multiline = forceMultiline || inline.length > MAX_INLINE || entries.some(([, v]) => v.includes("\n"));
  if (!multiline) return `[${inline}]`;
  const inner = indent + INDENT;
  const lines = entries.map(([k, v]) => `${inner}${formatKey(k)}=${formatValueAt(v, inner + INDENT)}`);
  return `[\n${lines.join(",\n")}\n${indent}]`;
}

function nodeStatement(node: DocNode, indent: string): string {
  const block = attrBlock(node.attrs, indent);
  return block ? `${indent}${formatId(node.id)} ${block}` : `${indent}${formatId(node.id)}`;
}

/** Serializes a Studio document to DOT. */
export function writeDot(doc: StudioDoc): string {
  const out: string[] = [];
  const header = doc.header.trimEnd();
  if (header) out.push(header);
  out.push(`digraph ${formatId(doc.name || "pipeline")} {`);

  const graphAttrs = Object.entries(doc.graphAttrs);
  if (graphAttrs.length > 0) {
    out.push(`${INDENT}graph ${attrBlock(doc.graphAttrs, INDENT, graphAttrs.length > 1)}`);
  }

  if (Object.keys(doc.nodeDefaults).length > 0) out.push(`${INDENT}node ${attrBlock(doc.nodeDefaults, INDENT)}`);
  if (Object.keys(doc.edgeDefaults).length > 0) out.push(`${INDENT}edge ${attrBlock(doc.edgeDefaults, INDENT)}`);
  if (graphAttrs.length > 0 || Object.keys(doc.nodeDefaults).length > 0 || Object.keys(doc.edgeDefaults).length > 0) out.push("");

  // Nodes in document order; a group block is written where its first member (or nested member) appears.
  const groupsByParent = new Map<string | null, DocGroup[]>();
  for (const group of doc.groups) {
    const key = group.parent && doc.groups.some((g) => g.uid === group.parent) ? group.parent : null;
    groupsByParent.set(key, [...(groupsByParent.get(key) ?? []), group]);
  }
  const membersOf = new Map<string, DocNode[]>();
  for (const node of doc.nodes) {
    if (node.group && doc.groups.some((g) => g.uid === node.group)) {
      membersOf.set(node.group, [...(membersOf.get(node.group) ?? []), node]);
    }
  }
  const topGroupOf = (uid: string): string => {
    let current = doc.groups.find((g) => g.uid === uid);
    const seen = new Set<string>();
    while (current?.parent && !seen.has(current.uid) && doc.groups.some((g) => g.uid === current!.parent)) {
      seen.add(current.uid);
      current = doc.groups.find((g) => g.uid === current!.parent);
    }
    return current?.uid ?? uid;
  };

  const emitted = new Set<string>();
  const writeGroup = (group: DocGroup, indent: string) => {
    if (emitted.has(group.uid)) return;
    emitted.add(group.uid);
    out.push(`${indent}subgraph ${group.name ? formatId(group.name) + " " : ""}{`);
    const inner = indent + INDENT;
    for (const [k, v] of Object.entries(group.attrs)) out.push(`${inner}${formatKey(k)}=${formatValueAt(v, inner + INDENT)}`);
    if (Object.keys(group.nodeDefaults).length > 0) out.push(`${inner}node ${attrBlock(group.nodeDefaults, inner)}`);
    if (Object.keys(group.edgeDefaults).length > 0) out.push(`${inner}edge ${attrBlock(group.edgeDefaults, inner)}`);
    const members = membersOf.get(group.uid) ?? [];
    const children = groupsByParent.get(group.uid) ?? [];
    if ((Object.keys(group.attrs).length > 0 || Object.keys(group.nodeDefaults).length > 0) && (members.length > 0 || children.length > 0)) out.push("");
    for (const member of members) out.push(nodeStatement(member, inner));
    for (const child of children) writeGroup(child, inner);
    out.push(`${indent}}`);
  };

  for (const node of doc.nodes) {
    if (node.group && doc.groups.some((g) => g.uid === node.group)) {
      const top = doc.groups.find((g) => g.uid === topGroupOf(node.group!))!;
      writeGroup(top, INDENT);
    } else {
      out.push(nodeStatement(node, INDENT));
    }
  }

  for (const group of groupsByParent.get(null) ?? []) writeGroup(group, INDENT);

  if (doc.edges.length > 0) {
    out.push("");
    let i = 0;
    while (i < doc.edges.length) {
      const edge = doc.edges[i];
      const block = attrBlock(edge.attrs, INDENT);
      if (block) {
        out.push(`${INDENT}${formatId(edge.from)} -> ${formatId(edge.to)} ${block}`);
        i++;
        continue;
      }
      // Chain consecutive attribute-less edges a -> b, b -> c into a -> b -> c.
      const chain = [edge.from, edge.to];
      let j = i + 1;
      while (j < doc.edges.length && Object.keys(doc.edges[j].attrs).length === 0 && doc.edges[j].from === chain[chain.length - 1]) {
        chain.push(doc.edges[j].to);
        j++;
      }
      out.push(INDENT + chain.map(formatId).join(" -> "));
      i = j;
    }
  }

  out.push("}");
  return out.join("\n") + "\n";
}

/** The leading comment block of a DOT source (comments and blank lines before `digraph`). */
export function extractHeader(source: string): string {
  const lines = source.replace(/\r\n?/g, "\n").split("\n");
  const header: string[] = [];
  let inBlock = false;
  for (const line of lines) {
    const trimmed = line.trim();
    if (inBlock) {
      header.push(line);
      if (trimmed.includes("*/")) inBlock = false;
      continue;
    }
    if (trimmed === "" || trimmed.startsWith("//") || trimmed.startsWith("#")) {
      header.push(line);
      continue;
    }
    if (trimmed.startsWith("/*")) {
      header.push(line);
      inBlock = !trimmed.includes("*/", 2);
      continue;
    }
    break;
  }
  return header.join("\n").trim();
}

/** True when the source has comments after the header (they cannot survive a visual edit). */
export function hasBodyComments(source: string): boolean {
  const header = extractHeader(source);
  const rest = source.replace(/\r\n?/g, "\n").slice(source.indexOf("digraph") >= 0 ? source.indexOf("digraph") : header.length);
  // Ignore // and /* inside quoted strings.
  let inString = false;
  for (let i = 0; i < rest.length; i++) {
    const c = rest[i];
    if (inString) {
      if (c === "\\") i++;
      else if (c === '"') inString = false;
      continue;
    }
    if (c === '"') inString = true;
    else if (c === "/" && (rest[i + 1] === "/" || rest[i + 1] === "*")) return true;
    else if (c === "#" && (i === 0 || rest[i - 1] === "\n")) return true;
  }
  return false;
}
