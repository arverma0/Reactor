import { nodeAttr, nodeClasses } from "./doc";
import type { DocNode, EffectiveValue, StudioDoc } from "./types";

export type SelectorKind = "universal" | "shape" | "class" | "id";
export const STYLE_PROPERTIES = ["llm_model", "llm_provider", "reasoning_effort"] as const;
export type StyleProperty = (typeof STYLE_PROPERTIES)[number];

export interface StyleRule {
  selector: string;
  kind: SelectorKind;
  value: string;
  declarations: Partial<Record<StyleProperty, string>> & Record<string, string>;
}

const SPECIFICITY: Record<SelectorKind, number> = { universal: 0, shape: 1, class: 2, id: 3 };

export function selectorKind(selector: string): SelectorKind {
  if (selector === "*") return "universal";
  if (selector.startsWith(".")) return "class";
  if (selector.startsWith("#")) return "id";
  return "shape";
}

function stripComments(text: string): string {
  return text.replace(/\/\*[\s\S]*?\*\//g, " ").replace(/(^|[^:])\/\/.*$/gm, "$1");
}

/** Lenient parser for the CSS-like model stylesheet (errors are reported by the server's stylesheet_syntax rule). */
export function parseStylesheet(text: string | undefined): StyleRule[] {
  if (!text || !text.trim()) return [];
  const rules: StyleRule[] = [];
  const pattern = /([^{}]+)\{([^}]*)\}/g;
  for (const match of stripComments(text).matchAll(pattern)) {
    const selectors = match[1].split(",").map((s) => s.trim()).filter(Boolean);
    const declarations: Record<string, string> = {};
    for (const part of match[2].split(";")) {
      const colon = part.indexOf(":");
      if (colon < 0) continue;
      const key = part.slice(0, colon).trim();
      let value = part.slice(colon + 1).trim();
      if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))) value = value.slice(1, -1);
      if (key) declarations[key] = value;
    }
    for (const selector of selectors) {
      const kind = selectorKind(selector);
      rules.push({ selector, kind, value: kind === "class" || kind === "id" ? selector.slice(1) : selector, declarations: { ...declarations } });
    }
  }
  return rules;
}

export function formatStylesheet(rules: StyleRule[]): string {
  if (rules.length === 0) return "";
  const width = Math.max(...rules.map((r) => r.selector.length));
  return rules
    .map((rule) => {
      const body = Object.entries(rule.declarations)
        .filter(([, v]) => v !== undefined && v !== "")
        .map(([k, v]) => `${k}: ${/[;{}\s]/.test(v) ? `"${v}"` : v};`)
        .join(" ");
      return `${rule.selector.padEnd(width)} { ${body} }`;
    })
    .join("\n");
}

function matches(doc: StudioDoc, rule: StyleRule, node: DocNode): boolean {
  switch (rule.kind) {
    case "universal":
      return true;
    case "shape":
      return (nodeAttr(doc, node, "shape") || "box") === rule.value;
    case "class":
      return nodeClasses(doc, node).includes(rule.value);
    case "id":
      return node.id === rule.value;
  }
}

/** Effective model settings with provenance, mirroring StylesheetTransform (explicit node keys always win). */
export function effectiveModel(doc: StudioDoc, node: DocNode, rules = parseStylesheet(doc.graphAttrs.model_stylesheet)): Partial<Record<StyleProperty, EffectiveValue>> {
  const result: Partial<Record<StyleProperty, EffectiveValue>> = {};
  const winners: Partial<Record<StyleProperty, { rule: StyleRule; order: number }>> = {};
  rules.forEach((rule, order) => {
    if (!matches(doc, rule, node)) return;
    for (const property of STYLE_PROPERTIES) {
      if (rule.declarations[property] === undefined) continue;
      const current = winners[property];
      if (!current || SPECIFICITY[rule.kind] > SPECIFICITY[current.rule.kind] || (SPECIFICITY[rule.kind] === SPECIFICITY[current.rule.kind] && order >= current.order)) {
        winners[property] = { rule, order };
      }
    }
  });
  for (const property of STYLE_PROPERTIES) {
    const explicit = node.attrs[property];
    if (explicit) {
      result[property] = { value: explicit, source: "node" };
    } else if (winners[property]) {
      result[property] = { value: winners[property]!.rule.declarations[property]!, source: "stylesheet", selector: winners[property]!.rule.selector };
    } else {
      const inherited = nodeAttr(doc, node, property);
      if (inherited) result[property] = { value: inherited, source: "default" };
    }
  }
  return result;
}
