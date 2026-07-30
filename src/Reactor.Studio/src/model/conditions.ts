/**
 * Edge conditions, as the engine reads them: clauses joined by `&&`, each `key op value` or a bare key (true when the value
 * is set and not false/0/no/none/null). The spec's operators are `=` and `!=`; Reactor adds the numeric comparisons `<`,
 * `<=`, `>`, `>=` (a missing or non-numeric value makes the clause false). Keys: `outcome`, `preferred_label`,
 * `context.<key>` (or a bare context key), `visits.<node>` (how many times the engine has run the stage in this run), and
 * `metrics.<name>` (the latest value of an outcome metric).
 */
export type ConditionOp = "=" | "!=" | "<" | "<=" | ">" | ">=" | "truthy";

/** One clause of an edge condition. `value` is kept as written (quoted literals keep their quotes), so text round-trips. */
export interface Clause {
  key: string;
  op: ConditionOp;
  value: string;
}

export const OUTCOMES = ["success", "partial_success", "retry", "fail", "skipped"] as const;

export const NUMERIC_OPS: readonly ConditionOp[] = ["<", "<=", ">", ">="];

export const isNumericOp = (op: ConditionOp) => NUMERIC_OPS.includes(op);

/** Operators for pickers, in the order people read them. */
export const OPERATORS: { op: ConditionOp; symbol: string; title: string }[] = [
  { op: "=", symbol: "=", title: "equals" },
  { op: "!=", symbol: "≠", title: "does not equal" },
  { op: "<", symbol: "<", title: "less than (numbers)" },
  { op: "<=", symbol: "≤", title: "at most (numbers)" },
  { op: ">", symbol: ">", title: "more than (numbers)" },
  { op: ">=", symbol: "≥", title: "at least (numbers)" },
  { op: "truthy", symbol: "is set", title: "is set (not empty, false, 0, no, none, or null)" },
];

const SYMBOL: Record<ConditionOp, string> = { "=": "=", "!=": "≠", "<": "<", "<=": "≤", ">": ">", ">=": "≥", truthy: "" };

/** A number as the engine parses literals and values (invariant culture, finite). */
export function isNumber(text: string | undefined): boolean {
  const trimmed = (text ?? "").trim();
  return /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/.test(trimmed) && Number.isFinite(Number(trimmed));
}

/** Splits on `&&` outside quoted literals. */
function splitClauses(text: string): string[] {
  const parts: string[] = [];
  let current = "";
  let quote: string | null = null;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quote) {
      current += c;
      if (c === "\\" && i + 1 < text.length) current += text[++i];
      else if (c === quote) quote = null;
      continue;
    }
    if (c === '"' || c === "'") {
      quote = c;
      current += c;
      continue;
    }
    if (c === "&" && text[i + 1] === "&") {
      parts.push(current);
      current = "";
      i++;
      continue;
    }
    current += c;
  }
  parts.push(current);
  return parts;
}

/** The first operator outside quotes; two-character operators (`!=`, `<=`, `>=`) win at a position (as in the engine). */
function findOperator(clause: string): { index: number; op: ConditionOp; length: number } | null {
  let quote: string | null = null;
  for (let i = 0; i < clause.length; i++) {
    const c = clause[i];
    if (quote) {
      if (c === "\\") i++;
      else if (c === quote) quote = null;
      continue;
    }
    if (c === '"' || c === "'") {
      quote = c;
      continue;
    }
    const next = clause[i + 1];
    if (c === "!" && next === "=") return { index: i, op: "!=", length: 2 };
    if (c === "<" && next === "=") return { index: i, op: "<=", length: 2 };
    if (c === ">" && next === "=") return { index: i, op: ">=", length: 2 };
    if (c === "=") return { index: i, op: "=", length: 1 };
    if (c === "<") return { index: i, op: "<", length: 1 };
    if (c === ">") return { index: i, op: ">", length: 1 };
  }
  return null;
}

export function parseCondition(text: string | undefined): Clause[] {
  if (!text || !text.trim()) return [];
  return splitClauses(text)
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part): Clause => {
      const found = findOperator(part);
      if (!found) return { key: part, op: "truthy", value: "" };
      return { key: part.slice(0, found.index).trim(), op: found.op, value: part.slice(found.index + found.length).trim() };
    });
}

export function formatCondition(clauses: Clause[]): string | undefined {
  const text = clauses
    .filter((c) => c.key.trim())
    .map((c) => (c.op === "truthy" ? c.key.trim() : `${c.key.trim()}${c.op}${c.value.trim()}`))
    .join(" && ");
  return text || undefined;
}

/** Condition text without insignificant whitespace (`outcome = fail` → `outcome=fail`, `visits.fix >= 3` → `visits.fix>=3`). */
export function normalizeCondition(text: string): string {
  return formatCondition(parseCondition(text)) ?? "";
}

/** The node of a `visits.<node>` (or `context.visits.<node>`) key. */
export function visitsNode(key: string): string | null {
  for (const prefix of ["visits.", "context.visits."]) if (key.startsWith(prefix) && key.length > prefix.length) return key.slice(prefix.length);
  return null;
}

/** The metric of a `metrics.<name>` (or `context.metrics.<name>`) key. */
export function metricName(key: string): string | null {
  for (const prefix of ["metrics.", "context.metrics."]) if (key.startsWith(prefix) && key.length > prefix.length) return key.slice(prefix.length);
  return null;
}

const unquote = (value: string) => (/^(["']).*\1$/s.test(value) ? value.slice(1, -1).replace(/\\(.)/g, "$1") : value);

/** One clause for people: `outcome=fail` → `failed`, `visits.polish<3` → `polish visits < 3`, `metrics.qa_failed>0` → `metric qa_failed > 0`. */
export function describeClause(c: Clause): string {
  const value = unquote(c.value);
  if (c.key === "outcome" && c.op === "=") return value === "success" ? "succeeded" : value === "fail" ? "failed" : value.replace(/_/g, " ");
  if (c.key === "outcome" && c.op === "!=") return `not ${value.replace(/_/g, " ")}`;
  const node = visitsNode(c.key);
  if (node) return c.op === "truthy" ? `${node} visited` : `${node} visits ${SYMBOL[c.op]} ${value}`;
  const metric = metricName(c.key);
  if (metric) return c.op === "truthy" ? `metric ${metric} set` : `metric ${metric} ${SYMBOL[c.op]} ${value}`;
  const key = c.key.replace(/^context\./, "");
  return c.op === "truthy" ? key : `${key} ${SYMBOL[c.op]} ${value}`;
}

/** Human-readable rendering: `outcome=fail && context.tests_passed!=true` → `failed · tests_passed ≠ true`. */
export function describeCondition(text: string | undefined): string {
  const clauses = parseCondition(text);
  if (clauses.length === 0) return "always";
  return clauses.map(describeClause).join(" · ");
}

/** A problem with a clause the engine would reject or never match (shown next to the clause), or null. */
export function clauseProblem(c: Clause): string | null {
  if (!c.key.trim()) return "Choose a key.";
  if (!/^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z0-9_][A-Za-z0-9_-]*)*$/.test(c.key.trim())) return "Keys are outcome, preferred_label, context.<key>, visits.<node>, or metrics.<name>.";
  if (isNumericOp(c.op) && !isNumber(c.value)) return `${SYMBOL[c.op]} compares numbers; the value must be a number.`;
  if (c.op !== "truthy" && /^[=!<>]/.test(c.value.trim())) return "Use =, !=, <, <=, >, or >= (not == or =>).";
  if (c.op !== "truthy" && c.value.trim() && !/^(["']).*\1$/s.test(c.value.trim()) && !/^[^\s"'&|=!<>]+$/.test(c.value.trim())) return "Quote values with spaces or special characters.";
  return null;
}

/** Metric names a pipeline's conditions already test (`metrics.<name>`), in order of appearance. */
export function metricsInConditions(conditions: Iterable<string | undefined>): string[] {
  const names: string[] = [];
  for (const condition of conditions) {
    for (const clause of parseCondition(condition)) {
      const name = metricName(clause.key);
      if (name && !names.includes(name)) names.push(name);
    }
  }
  return names;
}

export interface KeySuggestion {
  key: string;
  group: "Outcome" | "Visits" | "Metrics" | "Context";
  detail?: string;
}

/**
 * Keys the clause editor offers: the outcome and preferred label, `visits.<node>` for the given stages, `metrics.<name>`
 * for the known metrics (stages' scenario metrics, the flight record, other conditions), and context keys other
 * conditions use.
 */
export function keySuggestions(nodes: { id: string; label?: string }[], metrics: string[], contextKeys: string[] = []): KeySuggestion[] {
  const list: KeySuggestion[] = [
    { key: "outcome", group: "Outcome", detail: "the stage's status" },
    { key: "preferred_label", group: "Outcome", detail: "the route the stage chose" },
  ];
  for (const n of nodes) list.push({ key: `visits.${n.id}`, group: "Visits", detail: n.label && n.label !== n.id ? `times “${n.label}” ran` : "times it ran" });
  for (const m of metrics) list.push({ key: `metrics.${m}`, group: "Metrics", detail: "latest value" });
  for (const k of contextKeys) list.push({ key: k.startsWith("context.") ? k : `context.${k}`, group: "Context" });
  return list;
}
