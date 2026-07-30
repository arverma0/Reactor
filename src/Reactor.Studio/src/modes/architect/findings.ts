/** The Architect's checker findings and self-review, made readable: what kind of finding, about which stage, and the fix. */

export type FindingKind = "lint" | "contract" | "forecast" | "tunnel" | "syntax" | "review" | "note";

export interface Finding {
  kind: FindingKind;
  /** The lint rule (`fail_flows_onward`, …) of a lint finding. */
  rule?: string;
  /** The stage it is about (`stage 'plan'`), when it names one. */
  node?: string;
  /** The edge it is about (`edge a -> b`). */
  edge?: string;
  text: string;
  /** The suggested fix (`(fix: …)` at the end of a lint finding). */
  fix?: string;
}

export const FINDING_LABEL: Record<FindingKind, string> = {
  lint: "Design rule",
  contract: "Contract",
  forecast: "Forecast",
  tunnel: "Wind tunnel",
  syntax: "Syntax",
  review: "Self-review",
  note: "Note",
};

const PREFIXES: [RegExp, FindingKind][] = [
  [/^Contract:\s*/, "contract"],
  [/^Forecast:\s*/, "forecast"],
  [/^Wind tunnel:\s*/, "tunnel"],
  [/^DOT syntax error:\s*/, "syntax"],
  [/^Self-review(?:\s*\([^)]*\))?:\s*/, "review"],
];

/**
 * Parses one checker finding: `[rule] stage 'id': message (fix: hint)` (validator and design rules), or a finding with a
 * `Contract:`, `Forecast:`, `Wind tunnel:`, `DOT syntax error:`, or `Self-review:` prefix; anything else is a note.
 */
export function parseFinding(raw: string): Finding {
  const text = raw.trim();
  const lint = /^\[([a-z_]+)\]\s*(?:stage '([^']+)':\s*|edge (\S+ -> \S+):\s*)?([\s\S]*)$/.exec(text);
  if (lint) {
    let body = lint[4];
    let fix: string | undefined;
    const fixAt = body.lastIndexOf(" (fix: ");
    if (fixAt >= 0 && body.endsWith(")")) {
      fix = body.slice(fixAt + 7, -1).trim();
      body = body.slice(0, fixAt).trim();
    }
    return { kind: "lint", rule: lint[1], node: lint[2], edge: lint[3], text: body, fix };
  }
  for (const [prefix, kind] of PREFIXES) {
    if (prefix.test(text)) {
      const body = text.replace(prefix, "");
      const stage = kind === "tunnel" ? /last stage '([^']+)'/.exec(body)?.[1] : undefined;
      return { kind, text: body, node: stage };
    }
  }
  return { kind: "note", text };
}

/** One line for a finding: its kind, the stage, and the message (for the drafts list). */
export function findingSummary(raw: string): string {
  const f = parseFinding(raw);
  const where = f.node ? ` (${f.node})` : f.edge ? ` (${f.edge})` : "";
  return `${f.rule ?? FINDING_LABEL[f.kind]}${where}: ${f.text.replace(/`/g, "")}`;
}

/** The verdict of the Architect's self-review answer (`VERDICT: PASS` / `VERDICT: REVISE`), as the server reads it. */
export function reviewVerdict(review: string | null | undefined): "pass" | "revise" | "skipped" | null {
  if (!review || !review.trim()) return null;
  if (/^\(not reviewed/i.test(review.trim())) return "skipped";
  for (const line of review.replace(/\r\n/g, "\n").split("\n")) {
    const clean = line.trim().replace(/^[*`#\s]+|[*`#\s]+$/g, "");
    if (!/^VERDICT:/i.test(clean)) continue;
    return /^REVISE/i.test(clean.slice("VERDICT:".length).trim().replace(/^[*`]+/, "")) ? "revise" : "pass";
  }
  return "pass";
}
