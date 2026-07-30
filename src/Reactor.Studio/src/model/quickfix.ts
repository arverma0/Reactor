import { formatInputs, nodeOutputs, parseInputs } from "./artifacts";
import { normalizeCondition, parseCondition } from "./conditions";
import { edgeAttr, findNode, handlerOf, kindOf, nodeAttr, nodeLabel, outgoing, setEdgeAttr, setGraphAttr, setNodeAttr } from "./doc";
import type { Diagnostic, DocEdge, StudioDoc } from "./types";

/**
 * One-click fixes for the engine's flow-control lint findings. Each fix re-checks the document it is applied to (the
 * analysis may be a moment behind the edits) and returns it unchanged when it no longer applies, so a fix is always a
 * single, undoable document edit.
 */
export interface QuickFix {
  id: string;
  /** Button text. */
  label: string;
  /** What the fix changes, for tooltips. */
  title: string;
  /** Fixes sharing a group are alternatives the person chooses between (shown together under the group's text). */
  group?: string;
  /** The recommended choice within its group. */
  recommended?: boolean;
  apply: (doc: StudioDoc) => StudioDoc;
  /** Confirmation shown after applying. */
  done: string;
  /** The stage to show after applying (when the person has more to decide there). */
  focus?: string;
}

const WORK = new Set(["agent", "tool", "supervisor", "custom"]);

const isConditional = (doc: StudioDoc, e: DocEdge) => !!(edgeAttr(doc, e, "condition") ?? "").trim();

const weightOf = (doc: StudioDoc, e: DocEdge) => {
  const n = Number(edgeAttr(doc, e, "weight") ?? 0);
  return Number.isFinite(n) ? n : 0;
};

const labelOf = (doc: StudioDoc, id: string) => {
  const node = findNode(doc, id);
  return node ? nodeLabel(doc, node) : id;
};

/** True when a condition can only hold for an outcome other than success (`outcome=partial_success`, `outcome!=success`). */
function excludesSuccess(condition: string | undefined): boolean {
  return parseCondition(condition).some((c) => c.key === "outcome" && ((c.op === "=" && c.value !== "success") || (c.op === "!=" && c.value === "success")));
}

/** Edges of an edge diagnostic (`from -> to`). */
export function diagnosticEdge(d: Diagnostic): { from: string; to: string } | null {
  const match = /^\s*(\S+)\s*->\s*(\S+)\s*$/.exec(d.edge ?? "");
  return match ? { from: match[1], to: match[2] } : null;
}

/** The strongly connected component (a loop) of the graph that contains `start`, in document order; empty when `start` is on no cycle. */
export function loopOf(doc: StudioDoc, start: string): string[] {
  const next = new Map<string, string[]>();
  const prev = new Map<string, string[]>();
  for (const e of doc.edges) {
    next.set(e.from, [...(next.get(e.from) ?? []), e.to]);
    prev.set(e.to, [...(prev.get(e.to) ?? []), e.from]);
  }
  const reach = (from: string, edges: Map<string, string[]>) => {
    const seen = new Set<string>();
    const stack = [...(edges.get(from) ?? [])];
    while (stack.length) {
      const id = stack.pop()!;
      if (seen.has(id)) continue;
      seen.add(id);
      stack.push(...(edges.get(id) ?? []));
    }
    return seen;
  };
  // A node is in start's loop when start reaches it and it reaches start.
  const forward = reach(start, next);
  if (!forward.has(start)) return [];
  const backward = reach(start, prev);
  return doc.nodes.map((n) => n.id).filter((id) => forward.has(id) && backward.has(id));
}

const REWORK = /fix|rework|repair|polish|revis|retry|redo|refine|improve|patch/i;
const SAFE_LABEL = /rework|fix|revis|reject|retry|again|back|changes|review|escalat|human|person/i;

function failFlowsOnward(doc: StudioDoc, d: Diagnostic): QuickFix[] {
  const id = d.node_id;
  const node = id ? findNode(doc, id) : undefined;
  if (!id || !node) return [];
  const fixes: QuickFix[] = [];
  if (!(doc.graphAttrs.default_on_fail ?? "").trim()) {
    fixes.push({
      id: "graph-stop",
      label: "Stop the run on unrouted failures",
      title: 'default_on_fail="stop" on the pipeline: a stage whose failure nothing routes ends the run instead of carrying on (for every such stage).',
      apply: (x) => ((x.graphAttrs.default_on_fail ?? "").trim() ? x : setGraphAttr(x, "default_on_fail", "stop")),
      done: "Unrouted failures now stop the run (default_on_fail=stop).",
    });
  }
  fixes.push({
    id: "node-stop",
    label: "Stop if this stage fails",
    title: `on_fail="stop" on “${nodeLabel(doc, node)}”: if it fails and nothing routes the failure, the run ends with its reason.`,
    apply: (x) => (findNode(x, id) ? setNodeAttr(x, id, "on_fail", "stop") : x),
    done: `A failure of “${nodeLabel(doc, node)}” now stops the run.`,
  });

  // Conditioning the only way on is sound when nothing else can match together with it on success.
  const edges = outgoing(doc, id);
  const onward = edges.filter((e) => !isConditional(doc, e));
  const others = edges.filter((e) => isConditional(doc, e));
  if (onward.length === 1 && others.every((e) => excludesSuccess(edgeAttr(doc, e, "condition")))) {
    const edge = onward[0];
    const keepPartial = (nodeAttr(doc, node, "allow_partial") ?? "").toLowerCase() === "true" && !others.some((e) => normalizeCondition(edgeAttr(doc, e, "condition") ?? "") === "outcome=partial_success");
    const condition = keepPartial ? "outcome!=fail" : "outcome=success";
    const retry = nodeAttr(doc, node, "retry_target") || nodeAttr(doc, node, "fallback_retry_target") || doc.graphAttrs.retry_target || doc.graphAttrs.fallback_retry_target;
    fixes.push({
      id: "route-success",
      label: "Route failures explicitly",
      title: `condition="${condition}" on the edge to “${labelOf(doc, edge.to)}”: a failure no longer flows on; it ${retry ? `jumps to the retry target “${labelOf(doc, retry)}”` : "fails the run"} until you add an on-fail route (drag from ✕ on fail).`,
      apply: (x) => {
        const current = x.edges.find((e) => e.uid === edge.uid);
        return current && !isConditional(x, current) ? setEdgeAttr(x, edge.uid, "condition", condition) : x;
      },
      done: `“${nodeLabel(doc, node)}” now goes on to “${labelOf(doc, edge.to)}” only when it ${keepPartial ? "does not fail" : "succeeds"}.`,
      focus: id,
    });
  }
  fixes.push({
    id: "node-continue",
    label: "It's intended",
    title: `on_fail="continue" on “${nodeLabel(doc, node)}”: says carrying on after a failure is what you want (and silences this warning).`,
    apply: (x) => (findNode(x, id) ? setNodeAttr(x, id, "on_fail", "continue") : x),
    done: `Carrying on after a failure of “${nodeLabel(doc, node)}” is marked as intended.`,
  });
  return fixes;
}

function defaultRoute(doc: StudioDoc, d: Diagnostic): QuickFix[] {
  const id = d.node_id;
  if (!id || !findNode(doc, id)) return [];
  const onward = outgoing(doc, id).filter((e) => !isConditional(doc, e));
  if (onward.length === 0) return [];
  const top = Math.max(...onward.map((e) => weightOf(doc, e)));
  const tied = onward.filter((e) => weightOf(doc, e) === top);
  if (tied.length < 2) return [];
  // Safe defaults first: a person's gate, then rework routes.
  const rank = (e: DocEdge) => {
    const target = findNode(doc, e.to);
    if (target && handlerOf(doc, target) === "wait.human") return 0;
    if (SAFE_LABEL.test(edgeAttr(doc, e, "label") ?? "") || REWORK.test(e.to) || (target && REWORK.test(nodeLabel(doc, target)))) return 1;
    return 2;
  };
  const ordered = [...tied].sort((a, b) => rank(a) - rank(b));
  const weight = String(top + 1);
  return ordered.map((e, i) => {
    const label = edgeAttr(doc, e, "label");
    const text = label ? `${label} → ${labelOf(doc, e.to)}` : labelOf(doc, e.to);
    return {
      id: `default:${e.uid}`,
      label: text,
      title: `weight=${weight} on the edge to “${labelOf(doc, e.to)}”: when the stage picks no label, the run goes there.`,
      group: "Make the default",
      recommended: i === 0 && rank(e) < 2,
      apply: (x: StudioDoc) => {
        const current = x.edges.find((c) => c.uid === e.uid);
        return current ? setEdgeAttr(x, e.uid, "weight", weight) : x;
      },
      done: `Without a label, “${labelOf(doc, id)}” now goes to “${labelOf(doc, e.to)}”.`,
    };
  });
}

function unboundedLoop(doc: StudioDoc, d: Diagnostic): QuickFix[] {
  const id = d.node_id;
  if (!id) return [];
  const members = loopOf(doc, id)
    .map((m) => findNode(doc, m)!)
    .filter((n) => WORK.has(kindOf(doc, n)));
  const rank = (n: (typeof members)[number]) => (REWORK.test(n.id) || REWORK.test(nodeLabel(doc, n)) ? 0 : kindOf(doc, n) === "agent" ? 1 : 2);
  const ordered = [...members].sort((a, b) => rank(a) - rank(b));
  return ordered.map((n, i) => ({
    id: `bound:${n.id}`,
    label: nodeLabel(doc, n),
    title: `max_visits=3 on “${nodeLabel(doc, n)}”: it runs at most three times per run; then the run fails, or goes where on_max_visits says.`,
    group: "Bound the loop at",
    recommended: i === 0,
    apply: (x: StudioDoc) => (findNode(x, n.id) && !findNode(x, n.id)!.attrs.max_visits ? setNodeAttr(x, n.id, "max_visits", "3") : x),
    done: `“${nodeLabel(doc, n)}” now runs at most 3 times; choose where the run goes after that under Flow control.`,
    focus: n.id,
  }));
}

function failureEvidence(doc: StudioDoc, d: Diagnostic): QuickFix[] {
  const edge = diagnosticEdge(d);
  const fixer = edge ? findNode(doc, edge.to) : d.node_id ? findNode(doc, d.node_id) : undefined;
  const source = edge ? findNode(doc, edge.from) : undefined;
  if (!fixer || !source) return [];
  const inputs = parseInputs(fixer.attrs.inputs);
  const missing = nodeOutputs(doc, source)
    .map((o) => o.name)
    .filter((name, i, all) => !inputs.includes(name) && all.indexOf(name) === i)
    .slice(0, 4);
  return missing.map((name, i) => ({
    id: `input:${name}`,
    label: `Read ${name}`,
    title: `Add ${name} (produced by “${nodeLabel(doc, source)}”) to the inputs of “${nodeLabel(doc, fixer)}”, so its prompt says where the failing evidence is.`,
    group: missing.length > 1 ? "Give it the evidence" : undefined,
    recommended: i === 0,
    apply: (x: StudioDoc) => {
      const current = findNode(x, fixer.id);
      if (!current) return x;
      const list = parseInputs(current.attrs.inputs);
      return list.includes(name) ? x : setNodeAttr(x, fixer.id, "inputs", formatInputs([...list, name]));
    },
    done: `“${nodeLabel(doc, fixer)}” now reads ${name}.`,
  }));
}

function visitLimits(doc: StudioDoc, d: Diagnostic): QuickFix[] {
  const id = d.node_id;
  const node = id ? findNode(doc, id) : undefined;
  if (!id || !node || !/without max_visits/.test(d.message) || node.attrs.max_visits) return [];
  return [
    {
      id: "max-visits",
      label: "Set max visits to 3",
      title: `max_visits=3 on “${nodeLabel(doc, node)}”, so its on_max_visits reroute takes effect after three visits.`,
      apply: (x) => (findNode(x, id) && !findNode(x, id)!.attrs.max_visits ? setNodeAttr(x, id, "max_visits", "3") : x),
      done: `“${nodeLabel(doc, node)}” now runs at most 3 times.`,
      focus: id,
    },
  ];
}

/** The quick fixes for a diagnostic (none for rules without one). */
export function quickFixes(doc: StudioDoc, d: Diagnostic): QuickFix[] {
  switch (d.rule) {
    case "fail_flows_onward":
      return failFlowsOnward(doc, d);
    case "default_route":
      return defaultRoute(doc, d);
    case "unbounded_loop":
      return unboundedLoop(doc, d);
    case "failure_evidence":
      return failureEvidence(doc, d);
    case "visit_limits":
      return visitLimits(doc, d);
    default:
      return [];
  }
}
