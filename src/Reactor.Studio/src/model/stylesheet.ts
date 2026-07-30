import { nodeAttr, nodeClasses } from "./doc";
import type { DocNode, EffectiveValue, StudioDoc } from "./types";

export type SelectorKind = "universal" | "shape" | "class" | "id";
/** Stylesheet properties: the spec's model settings plus Reactor's `instructions` (standing instructions for the matched stages). */
export const STYLE_PROPERTIES = ["llm_model", "llm_provider", "reasoning_effort", "instructions"] as const;
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

/**
 * Lenient parser for the CSS-like model stylesheet (errors are reported by the server's stylesheet_syntax rule). Quoted
 * values may contain `;`, braces, and line breaks; `\` escapes the next character inside quotes. `/* … *\/` comments are
 * skipped outside quotes.
 */
export function parseStylesheet(text: string | undefined): StyleRule[] {
  if (!text || !text.trim()) return [];
  const rules: StyleRule[] = [];
  const len = text.length;
  let i = 0;
  const skipComment = () => {
    if (text.startsWith("/*", i)) {
      const end = text.indexOf("*/", i + 2);
      i = end < 0 ? len : end + 2;
      return true;
    }
    return false;
  };
  while (i < len) {
    let selectorText = "";
    while (i < len && text[i] !== "{") {
      if (skipComment()) continue;
      selectorText += text[i++];
    }
    if (i >= len) break;
    i++; // {
    const declarations: Record<string, string> = {};
    let key = "";
    let value = "";
    let inValue = false;
    let quoted = false;
    const commit = () => {
      const k = key.trim();
      if (k && inValue) declarations[k] = quoted ? value : value.trim();
      key = "";
      value = "";
      inValue = false;
      quoted = false;
    };
    while (i < len && text[i] !== "}") {
      const c = text[i];
      if (inValue && (c === '"' || c === "'") && !quoted && value.trim() === "") {
        // A quoted value: read to the closing quote, honoring escapes.
        const quote = c;
        value = "";
        quoted = true;
        i++;
        while (i < len && text[i] !== quote) {
          if (text[i] === "\\" && i + 1 < len) i++;
          value += text[i++];
        }
        i++;
        continue;
      }
      if (skipComment()) continue;
      i++;
      if (c === ";") commit();
      else if (c === ":" && !inValue) inValue = true;
      else if (inValue) {
        if (!quoted) value += c;
      } else key += c;
    }
    commit();
    i++; // }
    const selectors = selectorText.split(",").map((s) => s.trim()).filter(Boolean);
    for (const selector of selectors) {
      const kind = selectorKind(selector);
      rules.push({ selector, kind, value: kind === "class" || kind === "id" ? selector.slice(1) : selector, declarations: { ...declarations } });
    }
  }
  return rules;
}

/** A declaration value as the server's parser reads it: bare when it is a simple token, else quoted with escapes. */
export function formatStyleValue(value: string): string {
  return /^[A-Za-z0-9_.:/@+-]+$/.test(value) ? value : `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}

export function formatStylesheet(rules: StyleRule[]): string {
  if (rules.length === 0) return "";
  const width = Math.max(...rules.map((r) => r.selector.length));
  return rules
    .map((rule) => {
      const body = Object.entries(rule.declarations)
        .filter(([, v]) => v !== undefined && v !== "")
        .map(([k, v]) => `${k}: ${formatStyleValue(v)};`)
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
