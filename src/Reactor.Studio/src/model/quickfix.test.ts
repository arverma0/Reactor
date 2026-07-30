import { describe, expect, it } from "vitest";
import { findNode } from "./doc";
import { loopOf, quickFixes } from "./quickfix";
import { emptyDoc, type Diagnostic, type StudioDoc } from "./types";

/** start → plan → build → check; check fails back to fix → build (an unbounded loop); review picks Approve or Rework. */
const doc = (): StudioDoc => ({
  ...emptyDoc("factory"),
  nodes: [
    { id: "start", attrs: { shape: "Mdiamond" }, group: null },
    { id: "plan", attrs: { prompt: "Plan" }, group: null },
    { id: "build", attrs: { prompt: "Build" }, group: null },
    { id: "check", attrs: { shape: "parallelogram", tool_command: "npm test", outputs: "test_report, coverage:qa/coverage.json" }, group: null },
    { id: "fix", attrs: { prompt: "Fix", inputs: "plan_notes" }, group: null },
    { id: "review", attrs: { prompt: "Review" }, group: null },
    { id: "gate", attrs: { shape: "hexagon", label: "Ship it?" }, group: null },
    { id: "exit", attrs: { shape: "Msquare" }, group: null },
  ],
  edges: [
    { uid: "e0", from: "start", to: "plan", attrs: {} },
    { uid: "e1", from: "plan", to: "build", attrs: {} },
    { uid: "e2", from: "build", to: "check", attrs: {} },
    { uid: "e3", from: "check", to: "review", attrs: { condition: "outcome=success" } },
    { uid: "e4", from: "check", to: "fix", attrs: { condition: "outcome=fail" } },
    { uid: "e5", from: "fix", to: "build", attrs: {} },
    { uid: "e6", from: "review", to: "exit", attrs: { label: "Approve" } },
    { uid: "e7", from: "review", to: "gate", attrs: { label: "Rework" } },
    { uid: "e8", from: "gate", to: "exit", attrs: { label: "[S] Ship" } },
  ],
});

const diag = (rule: string, node_id: string, extra: Partial<Diagnostic> = {}): Diagnostic => ({ rule, severity: "warning", message: "", node_id, ...extra });
const fix = (d: StudioDoc, diagnostic: Diagnostic, id: string) => {
  const found = quickFixes(d, diagnostic).find((f) => f.id === id);
  if (!found) throw new Error(`no fix ${id}: ${quickFixes(d, diagnostic).map((f) => f.id).join(", ")}`);
  return found;
};

describe("quick fixes: failures that flow onward", () => {
  it("stops the run on unrouted failures for the whole pipeline or one stage", () => {
    const d = doc();
    const pipeline = fix(d, diag("fail_flows_onward", "plan"), "graph-stop").apply(d);
    expect(pipeline.graphAttrs.default_on_fail).toBe("stop");
    // Once the pipeline has a default, it is no longer offered.
    expect(quickFixes(pipeline, diag("fail_flows_onward", "plan")).map((f) => f.id)).not.toContain("graph-stop");
    expect(findNode(fix(d, diag("fail_flows_onward", "plan"), "node-stop").apply(d), "plan")!.attrs.on_fail).toBe("stop");
    expect(findNode(fix(d, diag("fail_flows_onward", "plan"), "node-continue").apply(d), "plan")!.attrs.on_fail).toBe("continue");
  });

  it("routes failures explicitly by conditioning the only way on", () => {
    const d = doc();
    const fixed = fix(d, diag("fail_flows_onward", "build"), "route-success").apply(d);
    expect(fixed.edges.find((e) => e.uid === "e2")!.attrs.condition).toBe("outcome=success");
    // Applying it again (the analysis may lag behind) changes nothing.
    expect(fix(d, diag("fail_flows_onward", "build"), "route-success").apply(fixed)).toBe(fixed);
  });

  it("keeps partial successes flowing when the stage accepts them", () => {
    const d = { ...doc(), nodes: doc().nodes.map((n) => (n.id === "build" ? { ...n, attrs: { ...n.attrs, allow_partial: "true" } } : n)) };
    expect(fix(d, diag("fail_flows_onward", "build"), "route-success").apply(d).edges.find((e) => e.uid === "e2")!.attrs.condition).toBe("outcome!=fail");
  });

  it("does not offer to condition a route when that could change where successes go", () => {
    const labeled = doc();
    // review has two labeled ways on: conditioning one would override the agent's label choice.
    expect(quickFixes(labeled, diag("fail_flows_onward", "review")).map((f) => f.id)).toEqual(["graph-stop", "node-stop", "node-continue"]);
    const other = { ...doc(), edges: [...doc().edges, { uid: "x", from: "plan", to: "fix", attrs: { condition: "context.hotfix=true" } }] };
    expect(quickFixes(other, diag("fail_flows_onward", "plan")).map((f) => f.id)).not.toContain("route-success");
    const exclusive = { ...doc(), edges: [...doc().edges, { uid: "x", from: "plan", to: "fix", attrs: { condition: "outcome=partial_success" } }] };
    expect(quickFixes(exclusive, diag("fail_flows_onward", "plan")).map((f) => f.id)).toContain("route-success");
  });
});

describe("quick fixes: default routes", () => {
  it("lets the person pick the default, a person's gate or rework first", () => {
    const d = doc();
    const fixes = quickFixes(d, diag("default_route", "review"));
    expect(fixes.map((f) => f.label)).toEqual(["Rework → Ship it?", "Approve → exit"]);
    expect(fixes[0].recommended).toBe(true);
    expect(fixes.every((f) => f.group === "Make the default")).toBe(true);
    const fixed = fixes[0].apply(d);
    expect(fixed.edges.find((e) => e.uid === "e7")!.attrs.weight).toBe("1");
    expect(fixed.edges.find((e) => e.uid === "e6")!.attrs.weight).toBeUndefined();
    // Above the shared top weight, whatever it is.
    const weighted = { ...d, edges: d.edges.map((e) => (e.from === "review" ? { ...e, attrs: { ...e.attrs, weight: "2" } } : e)) };
    expect(quickFixes(weighted, diag("default_route", "review"))[1].apply(weighted).edges.find((e) => e.uid === "e6")!.attrs.weight).toBe("3");
  });
});

describe("quick fixes: unbounded loops", () => {
  it("finds the loop and bounds a chosen stage in it, a rework stage first", () => {
    const d = doc();
    expect(loopOf(d, "build")).toEqual(["build", "check", "fix"]);
    expect(loopOf(d, "plan")).toEqual([]);
    const fixes = quickFixes(d, diag("unbounded_loop", "build"));
    expect(fixes.map((f) => f.id)).toEqual(["bound:fix", "bound:build", "bound:check"]);
    expect(fixes[0].recommended).toBe(true);
    const bounded = fixes[1].apply(d);
    expect(findNode(bounded, "build")!.attrs.max_visits).toBe("3");
    expect(fixes[1].focus).toBe("build");
  });
});

describe("quick fixes: failure evidence", () => {
  it("adds the failing stage's outputs to the fixer's inputs", () => {
    const d = doc();
    const fixes = quickFixes(d, diag("failure_evidence", "fix", { edge: "check -> fix" }));
    expect(fixes.map((f) => f.label)).toEqual(["Read test_report", "Read coverage"]);
    expect(findNode(fixes[0].apply(d), "fix")!.attrs.inputs).toBe("plan_notes, test_report");
    const withReport = fixes[0].apply(d);
    expect(quickFixes(withReport, diag("failure_evidence", "fix", { edge: "check -> fix" })).map((f) => f.label)).toEqual(["Read coverage"]);
  });

  it("offers nothing for rules without a fix or stale findings", () => {
    expect(quickFixes(doc(), diag("artifact_inputs", "fix"))).toEqual([]);
    expect(quickFixes(doc(), diag("fail_flows_onward", "gone"))).toEqual([]);
    expect(quickFixes(doc(), diag("visit_limits", "fix", { message: "on_max_visits has no effect without max_visits." }))[0].apply(doc()).nodes.find((n) => n.id === "fix")!.attrs.max_visits).toBe("3");
  });
});
