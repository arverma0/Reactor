import { describe, expect, it } from "vitest";
import { parseOutputs, parseInputs, dataLinks } from "./artifacts";
import { defaultSoftTimeout, defaultTimeoutGrace, isSoftTimeout, metricsFileProblem, parseDuration, parsePositiveInt, parseUsd } from "./attributes";
import { clauseProblem, formatCondition, isNumber, keySuggestions, metricName, metricsInConditions, parseCondition, describeCondition, visitsNode } from "./conditions";
import { deriveClassName, nodeClasses, renameNode, handlerOf } from "./doc";
import { extractHeader, formatValue, hasBodyComments, writeDot } from "./dot";
import { acceleratorOf, normalizeCondition, routesOf } from "./routes";
import { effectiveModel, formatStylesheet, parseStylesheet } from "./stylesheet";
import { emptyDoc, type StudioDoc } from "./types";

const sample = (): StudioDoc => ({
  ...emptyDoc("factory"),
  header: "// A factory",
  graphAttrs: { goal: "Build a thing", model_stylesheet: "* { llm_model: base; }\n.deep-thinking { llm_model: deep; }" },
  nodes: [
    { id: "start", attrs: { shape: "Mdiamond" }, group: null },
    { id: "design", attrs: { prompt: "Line one\nLine \"two\"\n" }, group: "g:cluster_design" },
    { id: "test", attrs: { shape: "parallelogram", tool_command: "python -m pytest -q", outputs: "report" }, group: null },
    { id: "fix", attrs: { prompt: "Fix", inputs: "report" }, group: null },
    { id: "exit", attrs: { shape: "Msquare" }, group: null },
  ],
  edges: [
    { uid: "e0", from: "start", to: "design", attrs: {} },
    { uid: "e1", from: "design", to: "test", attrs: {} },
    { uid: "e2", from: "test", to: "exit", attrs: { condition: "outcome=success" } },
    { uid: "e3", from: "test", to: "fix", attrs: { condition: "outcome=fail" } },
    { uid: "e4", from: "fix", to: "test", attrs: {} },
  ],
  groups: [{ uid: "g:cluster_design", name: "cluster_design", parent: null, attrs: { label: "Deep thinking" }, nodeDefaults: {}, edgeDefaults: {} }],
});

describe("dot writer", () => {
  it("writes a readable, hand-written-looking file", () => {
    const text = writeDot(sample());
    expect(text).toContain("// A factory\ndigraph factory {");
    expect(text).toContain('    graph [\n        goal="Build a thing",\n        model_stylesheet="* { llm_model: base; }\\n"\n            + ".deep-thinking { llm_model: deep; }"\n    ]');
    expect(text).toContain("    subgraph cluster_design {\n        label=\"Deep thinking\"\n\n        design [\n            prompt=\"Line one\\n\"\n                + \"Line \\\"two\\\"\\n\"\n        ]\n    }");
    expect(text).toContain("    start -> design -> test\n");
    expect(text).toContain('    test -> exit [condition="outcome=success"]');
    expect(text).toContain("    fix -> test\n}");
    // The group block appears where its first member was declared (after start).
    expect(text.indexOf("start [shape=Mdiamond]")).toBeLessThan(text.indexOf("subgraph cluster_design"));
    expect(text.indexOf("subgraph cluster_design")).toBeLessThan(text.indexOf("test [shape=parallelogram"));
  });

  it("quotes keywords, escapes, and leaves typed values bare", () => {
    expect(formatValue("box")).toBe("box");
    expect(formatValue("900s")).toBe("900s");
    expect(formatValue("summary:high")).toBe("summary:high");
    expect(formatValue("-1")).toBe("-1");
    expect(formatValue("graph")).toBe('"graph"');
    expect(formatValue("Node")).toBe('"Node"');
    expect(formatValue("two words")).toBe('"two words"');
    expect(formatValue("C:\\temp")).toBe('"C:\\\\temp"');
    expect(formatValue("")).toBe('""');
  });

  it("extracts the header and detects body comments", () => {
    const src = "// one\n/* two\n three */\n\ndigraph g {\n  a // trailing\n}";
    expect(extractHeader(src)).toBe("// one\n/* two\n three */");
    expect(hasBodyComments(src)).toBe(true);
    expect(hasBodyComments('// h\ndigraph g { a [prompt="see http://x // not a comment"] }')).toBe(false);
  });
});

describe("routes", () => {
  it("projects edges onto ports", () => {
    const doc = sample();
    const test = doc.nodes.find((n) => n.id === "test")!;
    expect(routesOf(doc, test).map((r) => r.key)).toEqual(["success", "fail"]);
    expect(normalizeCondition(" outcome = fail ")).toBe("outcome=fail");
    expect(acceleratorOf("[A] Approve")).toEqual({ key: "A", text: "Approve" });
    expect(acceleratorOf("R) Revise")).toEqual({ key: "R", text: "Revise" });
    expect(acceleratorOf("Ship it")).toBeNull();
  });
});

describe("model helpers", () => {
  it("derives classes like the engine", () => {
    expect(deriveClassName("Deep thinking")).toBe("deep-thinking");
    expect(deriveClassName("Inner Loop!")).toBe("inner-loop");
    const doc = sample();
    expect(nodeClasses(doc, doc.nodes[1])).toEqual(["deep-thinking"]);
    expect(handlerOf(doc, doc.nodes[2])).toBe("tool");
  });

  it("resolves the stylesheet with provenance", () => {
    const doc = sample();
    expect(effectiveModel(doc, doc.nodes[1]).llm_model).toEqual({ value: "deep", source: "stylesheet", selector: ".deep-thinking" });
    expect(effectiveModel(doc, doc.nodes[3]).llm_model).toEqual({ value: "base", source: "stylesheet", selector: "*" });
    const rules = parseStylesheet("* { llm_model: a; } #x, #y { reasoning_effort: high }");
    expect(rules.map((r) => r.selector)).toEqual(["*", "#x", "#y"]);
    expect(formatStylesheet(rules)).toBe("*  { llm_model: a; }\n#x { reasoning_effort: high; }\n#y { reasoning_effort: high; }");
  });

  it("parses and writes quoted stylesheet values such as instructions", () => {
    const text = '/* house rules */ .docs { llm_model: claude-opus-4.8; instructions: "Write in parts; append {more}.\nQuote \\"this\\". Path C:\\\\tmp" }';
    const rules = parseStylesheet(text);
    expect(rules).toHaveLength(1);
    expect(rules[0].declarations).toEqual({ llm_model: "claude-opus-4.8", instructions: 'Write in parts; append {more}.\nQuote "this". Path C:\\tmp' });
    const written = formatStylesheet(rules);
    expect(written).toBe('.docs { llm_model: claude-opus-4.8; instructions: "Write in parts; append {more}.\nQuote \\"this\\". Path C:\\\\tmp"; }');
    expect(parseStylesheet(written)[0].declarations).toEqual(rules[0].declarations);

    const doc = { ...sample(), graphAttrs: { model_stylesheet: '.deep-thinking { instructions: "Think; then act." }' } };
    expect(effectiveModel(doc, doc.nodes[1]).instructions).toEqual({ value: "Think; then act.", source: "stylesheet", selector: ".deep-thinking" });
  });

  it("renames nodes everywhere", () => {
    const doc = { ...sample(), graphAttrs: { model_stylesheet: "#fix { llm_model: m; } #fixer { llm_model: n; }", retry_target: "fix" } };
    const renamed = renameNode(doc, "fix", "repair");
    expect(renamed.edges.filter((e) => e.from === "repair" || e.to === "repair")).toHaveLength(2);
    expect(renamed.graphAttrs.retry_target).toBe("repair");
    expect(renamed.graphAttrs.model_stylesheet).toBe("#repair { llm_model: m; } #fixer { llm_model: n; }");
  });

  it("parses artifacts and conditions", () => {
    expect(parseOutputs("design:docs/DESIGN.md, verdict ,")).toEqual([{ name: "design", path: "docs/DESIGN.md" }, { name: "verdict" }]);
    expect(parseInputs("a, b, a")).toEqual(["a", "b"]);
    expect(dataLinks(sample()).map((l) => l.id)).toEqual(["data:report:test->fix"]);
    const clauses = parseCondition("outcome=success && context.tests_passed!=true && context.ready");
    expect(clauses).toEqual([
      { key: "outcome", op: "=", value: "success" },
      { key: "context.tests_passed", op: "!=", value: "true" },
      { key: "context.ready", op: "truthy", value: "" },
    ]);
    expect(formatCondition(clauses)).toBe("outcome=success && context.tests_passed!=true && context.ready");
    expect(describeCondition("outcome=fail && context.x!=1")).toBe("failed · x ≠ 1");
  });
});

describe("conditions", () => {
  it("parses the numeric operators like the engine: two-character operators win at a position", () => {
    expect(parseCondition("visits.polish<3 && metrics.score>=4 && metrics.qa_failed>0 && visits.fix<=2 && metrics.x!=1")).toEqual([
      { key: "visits.polish", op: "<", value: "3" },
      { key: "metrics.score", op: ">=", value: "4" },
      { key: "metrics.qa_failed", op: ">", value: "0" },
      { key: "visits.fix", op: "<=", value: "2" },
      { key: "metrics.x", op: "!=", value: "1" },
    ]);
    expect(parseCondition(" visits.polish  <  3 ")).toEqual([{ key: "visits.polish", op: "<", value: "3" }]);
  });

  it("round-trips conditions exactly, quoted literals included", () => {
    for (const text of [
      "outcome=fail && visits.fix>=3",
      "metrics.visual_min_score>=4 && metrics.qa_pending=0",
      "visits.polish<3",
      'context.msg="a && b=c" && outcome!=success',
      "context.ready",
      "metrics.ratio<=0.75 && context.visits.fix>1",
    ]) {
      expect(formatCondition(parseCondition(text))).toBe(text);
    }
    const clauses = parseCondition('context.msg="x<y" && metrics.score>4');
    expect(clauses[0]).toEqual({ key: "context.msg", op: "=", value: '"x<y"' });
    expect(parseCondition(formatCondition(clauses))).toEqual(clauses);
  });

  it("normalizes whitespace around every operator", () => {
    expect(normalizeCondition("visits.fix >= 3 &&outcome = fail")).toBe("visits.fix>=3 && outcome=fail");
    expect(normalizeCondition("metrics.score < 4")).toBe("metrics.score<4");
  });

  it("describes visits and metrics for people", () => {
    expect(describeCondition("visits.polish<3")).toBe("polish visits < 3");
    expect(describeCondition("metrics.qa_failed>0")).toBe("metric qa_failed > 0");
    expect(describeCondition("outcome=fail && visits.fix>=3")).toBe("failed · fix visits ≥ 3");
    expect(describeCondition("metrics.score<=2 && context.visits.fix!=0")).toBe("metric score ≤ 2 · fix visits ≠ 0");
    expect(describeCondition('context.reason="out of time"')).toBe("reason = out of time");
  });

  it("flags clauses the engine rejects or can never match", () => {
    expect(clauseProblem({ key: "visits.fix", op: "<", value: "three" })).toMatch(/number/);
    expect(clauseProblem({ key: "visits.fix", op: "<", value: "3" })).toBeNull();
    expect(clauseProblem({ key: "metrics.score", op: ">=", value: "4.5" })).toBeNull();
    expect(clauseProblem({ key: "outcome", op: "=", value: "=fail" })).toMatch(/==/);
    expect(clauseProblem({ key: "context.msg", op: "=", value: "two words" })).toMatch(/Quote/);
    expect(clauseProblem({ key: "context.msg", op: "=", value: '"two words"' })).toBeNull();
    expect(clauseProblem({ key: "bad key", op: "truthy", value: "" })).toMatch(/Keys/);
  });

  it("suggests visits for stages and metrics the pipeline knows", () => {
    expect(metricsInConditions(["metrics.score>=4 && visits.fix<3", undefined, "metrics.qa_failed=0 && metrics.score<2"])).toEqual(["score", "qa_failed"]);
    const keys = keySuggestions([{ id: "fix", label: "Fix it" }, { id: "test" }], ["score"], ["context.ready"]);
    expect(keys.map((k) => k.key)).toEqual(["outcome", "preferred_label", "visits.fix", "visits.test", "metrics.score", "context.ready"]);
    expect(keys.find((k) => k.key === "visits.fix")?.detail).toBe("times “Fix it” ran");
    expect(visitsNode("context.visits.fix")).toBe("fix");
    expect(metricName("metrics.score")).toBe("score");
    expect(isNumber("1e3")).toBe(true);
    expect(isNumber("3x")).toBe(false);
  });

  it("renames visits.<node> conditions with the node, and only exact ids", () => {
    const doc = {
      ...sample(),
      edges: [...sample().edges, { uid: "e9", from: "test", to: "fix", attrs: { condition: "outcome=fail && visits.fix<3 && visits.fixer<2 && context.visits.fix >= 1" } }],
    };
    const renamed = renameNode(doc, "fix", "repair");
    expect(renamed.edges.find((e) => e.uid === "e9")!.attrs.condition).toBe("outcome=fail && visits.repair<3 && visits.fixer<2 && context.visits.repair >= 1");
    const withTarget = { ...sample(), nodes: sample().nodes.map((n) => (n.id === "test" ? { ...n, attrs: { ...n.attrs, max_visits: "3", on_max_visits: "fix" } } : n)) };
    expect(renameNode(withTarget, "fix", "repair").nodes.find((n) => n.id === "test")!.attrs.on_max_visits).toBe("repair");
  });
});

describe("attribute values", () => {
  it("validates durations, budgets, and visit limits like the engine", () => {
    expect(parseDuration("90s")).toBe(90_000);
    expect(parseDuration("1.5h")).toBe(5_400_000);
    expect(parseDuration("30")).toBe(30_000);
    expect(parseDuration("5 m")).toBe(300_000);
    expect(parseDuration("5min")).toBeNull();
    expect(parseDuration("-5m")).toBeNull();
    expect(isSoftTimeout("off")).toBe(true);
    expect(isSoftTimeout("none")).toBe(true);
    expect(isSoftTimeout("soon")).toBe(false);
    expect(defaultSoftTimeout(parseDuration("150m"))).toBe("2h");
    expect(defaultSoftTimeout(parseDuration("10m"))).toBe("8m");
    expect(defaultSoftTimeout(parseDuration("4m"))).toBe("off");
    expect(defaultTimeoutGrace(parseDuration("150m"))).toBe("15m");
    expect(defaultTimeoutGrace(parseDuration("30m"))).toBe("3m");
    expect(defaultTimeoutGrace(parseDuration("30s"))).toBe("30s");
    expect(parseUsd("$12.5")).toBe(12.5);
    expect(parseUsd("-1")).toBeNull();
    expect(parseUsd("ten")).toBeNull();
    expect(parsePositiveInt("3")).toBe(3);
    expect(parsePositiveInt("0")).toBeNull();
    expect(metricsFileProblem("qa/report.json")).toBeNull();
    expect(metricsFileProblem("../report.json")).toMatch(/leave/);
    expect(metricsFileProblem("C:\\qa\\report.json")).toMatch(/relative/);
  });
});
