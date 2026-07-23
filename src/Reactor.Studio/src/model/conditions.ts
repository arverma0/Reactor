/** One clause of an edge condition. The engine supports only `=`, `!=`, bare keys, and `&&`. */
export interface Clause {
  key: string;
  op: "=" | "!=" | "truthy";
  value: string;
}

export const OUTCOMES = ["success", "partial_success", "retry", "fail", "skipped"] as const;

export function parseCondition(text: string | undefined): Clause[] {
  if (!text || !text.trim()) return [];
  return text
    .split("&&")
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part): Clause => {
      const ne = part.indexOf("!=");
      if (ne >= 0) return { key: part.slice(0, ne).trim(), op: "!=", value: part.slice(ne + 2).trim() };
      const eq = part.indexOf("=");
      if (eq >= 0) return { key: part.slice(0, eq).trim(), op: "=", value: part.slice(eq + 1).trim() };
      return { key: part, op: "truthy", value: "" };
    });
}

export function formatCondition(clauses: Clause[]): string | undefined {
  const text = clauses
    .filter((c) => c.key.trim())
    .map((c) => (c.op === "truthy" ? c.key.trim() : `${c.key.trim()}${c.op}${c.value.trim()}`))
    .join(" && ");
  return text || undefined;
}

/** Human-readable rendering: `outcome=fail && context.tests_passed!=true` → `failed · tests_passed ≠ true`. */
export function describeCondition(text: string | undefined): string {
  const clauses = parseCondition(text);
  if (clauses.length === 0) return "always";
  return clauses
    .map((c) => {
      if (c.key === "outcome" && c.op === "=") return c.value === "success" ? "succeeded" : c.value === "fail" ? "failed" : c.value.replace("_", " ");
      if (c.key === "outcome" && c.op === "!=") return `not ${c.value.replace("_", " ")}`;
      const key = c.key.replace(/^context\./, "");
      return c.op === "truthy" ? key : `${key} ${c.op === "=" ? "=" : "≠"} ${c.value}`;
    })
    .join(" · ");
}
