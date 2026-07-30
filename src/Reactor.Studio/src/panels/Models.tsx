import { useMemo, useState } from "react";
import type { ModelDescriptor } from "../api/types";
import { Button, Dropdown, Segmented, TextArea, cx, type DropdownOption } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { modelPriceText, modelPriceTitle } from "../model/cost";
import { kindOf, nodeClasses, nodeLabel, setGraphAttr } from "../model/doc";
import { KINDS } from "../model/handlers";
import { effectiveModel, formatStylesheet, parseStylesheet, selectorKind, type StyleRule } from "../model/stylesheet";
import type { StudioDoc } from "../model/types";
import { applyDoc, useStudio } from "../state/store";

const EFFORTS = ["low", "medium", "high"] as const;

export function modelOptions(models: ModelDescriptor[], current?: string): DropdownOption<string>[] {
  const options: DropdownOption<string>[] = [];
  const seen = new Set<string>();
  const sorted = [...models].sort((a, b) => Number(b.available) - Number(a.available) || a.id.localeCompare(b.id));
  for (const m of sorted) {
    if (seen.has(m.id)) continue;
    seen.add(m.id);
    const group = m.source === "copilot" ? "GitHub Copilot" : `${m.provider}${m.available ? "" : " · no key"}`;
    const context = m.context_window ? `${m.context_window >= 1_000_000 ? `${(m.context_window / 1_000_000).toFixed(m.context_window % 1_000_000 ? 1 : 0)}M` : `${Math.round(m.context_window / 1000)}k`}` : undefined;
    const detail = modelPriceText(m) ?? context;
    options.push({ value: m.id, label: <span className="mono" title={modelPriceTitle(m)}>{m.id}</span>, text: `${m.id} ${m.display_name ?? ""} ${m.provider}`, group, detail, disabled: m.policy_state === "disabled" });
  }
  if (current && !seen.has(current)) options.unshift({ value: current, label: <span className="mono">{current}</span>, text: current, group: "Current", detail: "custom" });
  return options;
}

function matchingNodes(doc: StudioDoc, rule: StyleRule) {
  return doc.nodes.filter((n) => {
    if (!KINDS[kindOf(doc, n)].llm && kindOf(doc, n) !== "join") return false;
    switch (rule.kind) {
      case "universal":
        return true;
      case "shape":
        return (n.attrs.shape ?? "box") === rule.value;
      case "class":
        return nodeClasses(doc, n).includes(rule.value);
      case "id":
        return n.id === rule.value;
    }
  });
}

export function ModelsPanel() {
  const doc = useStudio((s) => s.doc);
  const models = useStudio((s) => s.models);
  const modelsError = useStudio((s) => s.modelsError);
  const [raw, setRaw] = useState(false);
  const rules = useMemo(() => parseStylesheet(doc.graphAttrs.model_stylesheet), [doc.graphAttrs.model_stylesheet]);
  const classes = useMemo(() => [...new Set(doc.nodes.flatMap((n) => nodeClasses(doc, n)))].sort(), [doc]);
  const llmNodes = doc.nodes.filter((n) => KINDS[kindOf(doc, n)].llm);

  const write = (next: StyleRule[]) => applyDoc((d) => setGraphAttr(d, "model_stylesheet", formatStylesheet(next) || undefined), { coalesce: "stylesheet" });
  const update = (index: number, patch: Partial<StyleRule>) => write(rules.map((r, i) => (i === index ? { ...r, ...patch } : r)));
  const setDecl = (index: number, key: string, value: string | undefined) => {
    const declarations = { ...rules[index].declarations };
    if (value === undefined || value === "") delete declarations[key];
    else declarations[key] = value;
    update(index, { declarations });
  };
  const add = (selector: string) => write([...rules, { selector, kind: selectorKind(selector), value: selector.replace(/^[.#]/, ""), declarations: {} }]);

  return (
    <div className="rs-models">
      <div className="rs-panel-scroll">
        <p className="rs-models__intro">
          Rules assign models like CSS: <span className="mono">*</span> &lt; shape &lt; <span className="mono">.class</span> &lt; <span className="mono">#stage</span>. A value set on the stage itself always wins.
        </p>
        {modelsError ? <p className="rs-models__warn">Model list unavailable: {modelsError}</p> : null}
        {rules.map((rule, index) => {
          const matched = matchingNodes(doc, rule);
          return (
            <div key={index} className="rs-rule">
              <div className="rs-rule__head">
                <span className={cx("rs-rule__kind mono", `is-${rule.kind}`)}>{rule.kind === "universal" ? "all" : rule.kind}</span>
                <input
                  className="rs-rule__selector mono"
                  value={rule.selector}
                  onChange={(e) => update(index, { selector: e.target.value, kind: selectorKind(e.target.value), value: e.target.value.replace(/^[.#]/, "") })}
                  onKeyDown={(e) => e.stopPropagation()}
                />
                <Button size="sm" variant="ghost" icon="chevronUp" title="Earlier (loses ties)" disabled={index === 0} onClick={() => write(rules.map((r, i) => (i === index - 1 ? rules[index] : i === index ? rules[index - 1] : r)))} />
                <Button size="sm" variant="ghost" icon="trash" title="Remove rule" onClick={() => write(rules.filter((_, i) => i !== index))} />
              </div>
              <div className="rs-rule__row">
                <span className="rs-rule__prop">model</span>
                <Dropdown value={rule.declarations.llm_model} options={modelOptions(models, rule.declarations.llm_model)} onChange={(v) => setDecl(index, "llm_model", v)} searchable allowCustom placeholder="inherit" clearLabel="inherit" mono width={320} />
              </div>
              <div className="rs-rule__row">
                <span className="rs-rule__prop">effort</span>
                <Segmented
                  size="sm"
                  value={rule.declarations.reasoning_effort ?? "inherit"}
                  options={[{ value: "inherit", label: "—" }, ...EFFORTS.map((e) => ({ value: e, label: e }))]}
                  onChange={(v) => setDecl(index, "reasoning_effort", v === "inherit" ? undefined : v)}
                />
              </div>
              <div className="rs-rule__row">
                <span className="rs-rule__prop">provider</span>
                <Dropdown
                  value={rule.declarations.llm_provider}
                  options={["openai", "anthropic", "gemini", "openai-compatible"].map((p) => ({ value: p, label: p, text: p }))}
                  onChange={(v) => setDecl(index, "llm_provider", v)}
                  allowCustom
                  placeholder="inherit"
                  clearLabel="inherit"
                  mono
                />
              </div>
              <div className="rs-rule__row">
                <span className="rs-rule__prop" title="Standing instructions for the matched stages (Reactor extension; every backend honors them)">instructions</span>
                <TextArea value={rule.declarations.instructions ?? ""} onChange={(v) => setDecl(index, "instructions", v.trim() ? v : undefined)} placeholder="inherit" minRows={1} />
              </div>
              <div className="rs-rule__matches">
                {matched.length === 0 ? <span className="faint">matches no LLM stage</span> : matched.slice(0, 8).map((n) => <span key={n.id} className="rs-chip">{n.id}</span>)}
                {matched.length > 8 ? <span className="faint">+{matched.length - 8}</span> : null}
              </div>
            </div>
          );
        })}
        <div className="rs-models__add">
          <Button size="sm" icon="plus" onClick={() => add("*")} disabled={rules.some((r) => r.selector === "*")}>
            All stages
          </Button>
          {classes.filter((c) => !rules.some((r) => r.selector === "." + c)).slice(0, 4).map((c) => (
            <Button key={c} size="sm" variant="quiet" icon="plus" onClick={() => add("." + c)}>
              .{c}
            </Button>
          ))}
          <Button size="sm" variant="quiet" icon="plus" onClick={() => add("#stage_id")}>
            One stage
          </Button>
        </div>

        <div className="rs-models__heading label">Effective models</div>
        <div className="rs-models__table">
          {llmNodes.map((n) => {
            const eff = effectiveModel(doc, n, rules);
            return (
              <div key={n.id} className="rs-models__row">
                <span className="rs-models__node">{nodeLabel(doc, n)}</span>
                <span className={cx("rs-models__value mono", !eff.llm_model && "faint")}>{eff.llm_model?.value ?? "backend default"}</span>
                <span className="rs-models__source mono">{eff.llm_model ? (eff.llm_model.source === "stylesheet" ? eff.llm_model.selector : eff.llm_model.source === "node" ? "stage" : "default") : ""}</span>
              </div>
            );
          })}
        </div>

        <button type="button" className="rs-models__raw-toggle" onClick={() => setRaw(!raw)}>
          <Glyph name={raw ? "chevronDown" : "chevronRight"} size={11} /> Stylesheet source
        </button>
        {raw ? <TextArea value={doc.graphAttrs.model_stylesheet ?? ""} onChange={(v) => applyDoc((d) => setGraphAttr(d, "model_stylesheet", v || undefined), { coalesce: "stylesheet-raw" })} mono minRows={4} /> : null}
      </div>
    </div>
  );
}
