import { describe, expect, it } from "vitest";
import { parseOutputs, parseInputs, dataLinks } from "./artifacts";
import { formatCondition, parseCondition, describeCondition } from "./conditions";
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
