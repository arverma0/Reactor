import { useMemo, useState } from "react";
import { Button, ComboInput, Dropdown, Field, Section, Segmented, Stepper, TextArea, TextInput, Toggle, cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { artifactIndex, formatInputs, formatOutputs, isArtifactName, parseInputs, parseOutputs } from "../model/artifacts";
import { defaultSoftTimeout, defaultTimeoutGrace, isDuration, isSoftTimeout, metricsFileProblem, parseDuration, parsePositiveInt, parseUsd } from "../model/attributes";
import { clauseProblem, formatCondition, isNumericOp, keySuggestions, metricName, metricsInConditions, OPERATORS, OUTCOMES, parseCondition, visitsNode, type Clause, type KeySuggestion } from "../model/conditions";
import {
  IDENT,
  edgeAttr,
  findNode,
  groupById,
  handlerOf,
  kindOf,
  moveEdge,
  nodeAttr,
  nodeClasses,
  nodeLabel,
  outgoing,
  removeGroup,
  renameNode,
  setEdgeAttr,
  setEdgeAttrs,
  setGraphAttr,
  setNodeAttr,
  setNodeAttrs,
  setNodeGroup,
  updateGroup,
} from "../model/doc";
import { KINDS, LIBRARY_KINDS, kindAttrs, type NodeKind } from "../model/handlers";
import { lineColor } from "../model/palette";
import { acceleratorOf, describeEdge, routeKeyOf } from "../model/routes";
import { effectiveModel } from "../model/stylesheet";
import type { DocEdge, DocNode, StudioDoc } from "../model/types";
import { deleteSelection, groupSelection } from "../state/editing";
import { applyDoc, focusNode, select, setBottomPanel, setLeftPanel, toast, useStudio } from "../state/store";
import { useTelemetry } from "../state/telemetry";
import { useTunnel } from "../modes/tunnel/store";
import { modelOptions } from "./Models";
import { QuickFixes } from "./QuickFixes";

const KNOWN_NODE_KEYS = new Set([
  "shape", "type", "label", "prompt", "max_retries", "retry_policy", "goal_gate", "retry_target", "fallback_retry_target", "timeout",
  "llm_model", "llm_provider", "reasoning_effort", "fidelity", "thread_id", "class", "auto_status", "allow_partial", "backend",
  "tool_command", "human.timeout", "human.default_choice", "human.input", "human.question", "join_policy", "max_parallel",
  "manager.poll_interval", "manager.max_cycles", "manager.stop_condition", "manager.actions", "manager.steer_cooldown", "manager.steer_prompt",
  "stack.child_dotfile", "stack.child_workdir", "stack.child_autostart", "stack.child_logs_root",
  "agent.max_tool_rounds", "agent.max_turns", "agent.max_tokens", "agent.instructions", "agent.shell_timeout", "inputs", "outputs",
  "tool_hooks.pre", "tool_hooks.post", "instructions", "house_rules", "branch_workdir", "promote", "write_scope", "write_scope_enforce", "concurrency_group", "track_changes",
  "max_visits", "on_max_visits", "on_fail", "on_timeout", "soft_timeout", "timeout_grace", "metrics_file", "tool_color",
]);

const FIDELITIES = ["full", "compact", "truncate", "summary:low", "summary:medium", "summary:high"];

/** A dropdown option for a stage: its label, plus the id when they differ. */
function nodeOption(doc: StudioDoc, n: DocNode) {
  const label = nodeLabel(doc, n);
  return {
    value: n.id,
    label: (
      <span className="rs-kind-option">
        <Glyph name={kindOf(doc, n)} size={12} />
        {label}
        {label !== n.id ? <span className="faint mono">{n.id}</span> : null}
      </span>
    ),
    text: `${n.id} ${label}`,
  };
}

export function Inspector() {
  const selection = useStudio((s) => s.selection);
  const doc = useStudio((s) => s.doc);
  if (selection.nodes.length === 1) {
    const node = findNode(doc, selection.nodes[0]);
    if (node) return <NodeInspector key={node.id} node={node} doc={doc} />;
  }
  if (selection.nodes.length > 1) return <MultiInspector ids={selection.nodes} />;
  if (selection.edges.length === 1) {
    const edge = doc.edges.find((e) => e.uid === selection.edges[0]);
    if (edge) return <EdgeInspector key={edge.uid} edge={edge} doc={doc} />;
  }
  if (selection.group) {
    const group = groupById(doc, selection.group);
    if (group) return <GroupInspector uid={group.uid} />;
  }
  if (selection.note) return <NoteInspector id={selection.note} />;
  return <GraphInspector />;
}

// ---------- bound fields ----------

function useNodeSetter(id: string) {
  return (key: string, value: string | undefined, coalesce = true) => applyDoc((d) => setNodeAttr(d, id, key, value === "" ? undefined : value), { coalesce: coalesce ? `node:${id}:${key}` : undefined });
}

function InspectorHead({ icon, kicker, title, accent, children }: { icon: string; kicker: string; title: React.ReactNode; accent?: string; children?: React.ReactNode }) {
  return (
    <header className="rs-insp__head" style={{ ["--accent" as string]: accent ?? "var(--ink-2)" }}>
      <div className="rs-insp__kicker">
        <span className="rs-insp__glyph">
          <Glyph name={icon} size={14} />
        </span>
        <span className="label">{kicker}</span>
      </div>
      <div className="rs-insp__title">{title}</div>
      {children}
    </header>
  );
}

function IdEditor({ node, doc }: { node: DocNode; doc: StudioDoc }) {
  const [value, setValue] = useState(node.id);
  const taken = value !== node.id && doc.nodes.some((n) => n.id === value);
  const valid = IDENT.test(value) && !taken;
  return (
    <div className="rs-insp__id">
      <span className="label">id</span>
      <TextInput
        value={node.id}
        mono
        invalid={!valid}
        onChange={setValue}
        onCommit={(v) => {
          if (v === node.id) return;
          if (!IDENT.test(v)) return toast("Ids must be letters, digits, and underscores, starting with a letter.", "error");
          if (doc.nodes.some((n) => n.id === v)) return toast(`“${v}” is already used.`, "error");
          applyDoc((d) => renameNode(d, node.id, v), { select: { nodes: [v] } });
        }}
      />
    </div>
  );
}

function NodeInspector({ node, doc }: { node: DocNode; doc: StudioDoc }) {
  const set = useNodeSetter(node.id);
  const kind = kindOf(doc, node);
  const meta = KINDS[kind];
  const models = useStudio((s) => s.models);
  const info = useStudio((s) => s.info);
  const allDiagnostics = useStudio((s) => s.analysis.diagnostics);
  const diagnostics = useMemo(() => allDiagnostics.filter((d) => d.node_id === node.id), [allDiagnostics, node.id]);
  const attr = (key: string) => nodeAttr(doc, node, key) ?? "";
  const eff = effectiveModel(doc, node);
  const otherIds = doc.nodes.filter((n) => n.id !== node.id).map((n) => nodeOption(doc, n));

  const convert = (to: NodeKind) => {
    const patch: Record<string, string | undefined> = { shape: undefined, type: undefined, ...kindAttrs(to) };
    applyDoc((d) => setNodeAttrs(d, node.id, patch));
  };

  return (
    <div className="rs-insp">
      <InspectorHead icon={kind} kicker={`${meta.code} · ${meta.name}`} accent={`var(--kind-${kind})`} title={nodeLabel(doc, node)}>
        <IdEditor node={node} doc={doc} />
      </InspectorHead>

      {diagnostics.length > 0 ? (
        <div className="rs-insp__diags">
          {diagnostics.map((d, i) => (
            <div key={i} className={cx("rs-insp__diag", `is-${d.severity}`)}>
              <span className="mono">{d.rule}</span> {d.message}
              {d.fix ? <div className="rs-insp__fix">{d.fix}</div> : null}
              <QuickFixes diagnostic={d} />
            </div>
          ))}
        </div>
      ) : null}

      <Section title="Stage" id="insp-stage">
        <Field label={kind === "human" ? "Question" : "Label"}>
          <TextInput value={attr("label")} onChange={(v) => set("label", v)} placeholder={node.id} />
        </Field>
        {kind === "agent" || kind === "join" ? (
          <Field
            label={kind === "join" ? "Judge prompt (optional)" : "Prompt"}
            aside={
              kind === "agent" ? (
                <button type="button" className="rs-linkbtn" onClick={() => setBottomPanel("prompt")}>
                  preview what the agent sees
                </button>
              ) : undefined
            }
            hint={kind === "join" ? "With a prompt, an LLM picks the best branch (reply BEST: <id>). Without one, branches rank by status." : "$goal expands to the pipeline goal. Earlier stages arrive as a context preamble (see Context)."}
          >
            <TextArea value={attr("prompt")} onChange={(v) => set("prompt", v)} placeholder={kind === "join" ? "Compare the branches in parallel.results…" : "What should this stage accomplish?"} minRows={4} />
          </Field>
        ) : null}
        {kind === "tool" ? (
          <>
            <Field label="Command" hint="Runs with cmd.exe /c on Windows and bash -c elsewhere, in the run's working directory. Exit code 0 is success.">
              <TextArea value={attr("tool_command")} onChange={(v) => set("tool_command", v)} placeholder="dotnet test" mono minRows={2} />
            </Field>
            <Toggle
              checked={attr("tool_color") === "true"}
              onChange={(v) => set("tool_color", v ? "true" : undefined, false)}
              label="Let it print in color"
              hint="By default the command runs with NO_COLOR=1, FORCE_COLOR=0, and TERM=dumb. Either way, escape sequences are stripped from what is recorded (the raw capture is kept in stdout.raw.txt)."
            />
            <div style={{ height: 10 }} />
          </>
        ) : null}
        {kind === "human" ? <HumanFields node={node} doc={doc} set={set} /> : null}
        {kind === "fanout" ? (
          <>
            <Field label="Join policy" hint={attr("join_policy") === "first_success" ? "The first successful branch wins; the others are cancelled." : "Waits for every branch; partial success if any fails."}>
              <Segmented value={attr("join_policy") || "wait_all"} options={[{ value: "wait_all", label: "Wait for all" }, { value: "first_success", label: "First success" }]} onChange={(v) => set("join_policy", v === "wait_all" ? undefined : v)} />
            </Field>
            <Field label="Max concurrent branches" inline>
              <Stepper value={attr("max_parallel") ? Number(attr("max_parallel")) : undefined} onChange={(v) => set("max_parallel", v?.toString())} min={1} max={32} placeholder="4" />
            </Field>
            <Field
              label="Branch workspaces"
              hint={
                attr("branch_workdir") === "worktree"
                  ? "Each branch works in its own git worktree of the current files (uncommitted ones included). Needs git; node_modules is not checked out."
                  : attr("branch_workdir") === "copy"
                    ? "Each branch works in its own copy of the files (ignored files such as node_modules are not copied)."
                    : "Branches work in the same directory and can overwrite each other's files."
              }
            >
              <Segmented
                value={attr("branch_workdir") || "shared"}
                options={[{ value: "shared", label: "Shared" }, { value: "worktree", label: "Git worktree" }, { value: "copy", label: "Copy" }]}
                onChange={(v) => set("branch_workdir", v === "shared" ? undefined : v, false)}
              />
            </Field>
          </>
        ) : null}
        {kind === "join" ? (
          <Toggle
            checked={attr("promote") === "best"}
            onChange={(v) => set("promote", v ? "best" : undefined, false)}
            label="Promote the winner"
            hint="Apply the best branch's changes to this working directory (branches need their own workspaces). Fails without changing anything if the same files changed here meanwhile."
          />
        ) : null}
        {kind === "supervisor" ? <SupervisorFields node={node} doc={doc} set={set} /> : null}
        {kind === "router" ? <p className="rs-insp__note">A router has no work of its own. It passes the previous stage's outcome through and picks a route by the conditions below.</p> : null}
        {kind === "custom" ? (
          <Field label="Handler type" hint="Registered in your Reactor host with runner.RegisterHandler(type, handler).">
            <TextInput value={attr("type")} onChange={(v) => set("type", v)} mono />
          </Field>
        ) : null}
        <Field label="Kind">
          <Dropdown
            value={kind}
            options={LIBRARY_KINDS.concat("custom").map((k) => ({ value: k, label: <span className="rs-kind-option"><Glyph name={k} size={13} />{KINDS[k].name}</span>, text: KINDS[k].name }))}
            onChange={(v) => v && convert(v as NodeKind)}
          />
        </Field>
      </Section>

      {meta.llm ? (
        <Section title="Model" id="insp-model" aside={eff.llm_model ? <span className="faint mono">{eff.llm_model.source === "stylesheet" ? `from ${eff.llm_model.selector}` : eff.llm_model.source === "node" ? "set here" : "default"}</span> : undefined}>
          <Field label="Model" hint={!node.attrs.llm_model && eff.llm_model ? `Inherits ${eff.llm_model.value} from ${eff.llm_model.selector ?? "defaults"}. Choose one to pin it on this stage.` : undefined}>
            <Dropdown
              value={node.attrs.llm_model}
              options={modelOptions(models, node.attrs.llm_model)}
              onChange={(v) => set("llm_model", v, false)}
              searchable
              allowCustom
              mono
              width={340}
              clearLabel={eff.llm_model && !node.attrs.llm_model ? `inherit (${eff.llm_model.value})` : "inherit"}
              placeholder={eff.llm_model ? `inherit · ${eff.llm_model.value}` : "backend default"}
            />
          </Field>
          <Field label="Reasoning effort">
            <Segmented
              size="sm"
              value={node.attrs.reasoning_effort ?? "inherit"}
              options={[{ value: "inherit", label: eff.reasoning_effort && !node.attrs.reasoning_effort ? `— ${eff.reasoning_effort.value}` : "—" }, { value: "low", label: "low" }, { value: "medium", label: "medium" }, { value: "high", label: "high" }]}
              onChange={(v) => set("reasoning_effort", v === "inherit" ? undefined : v, false)}
            />
          </Field>
          <Field label="Backend" hint="Route this stage to a specific backend; otherwise the server's default runs it.">
            <Dropdown
              value={node.attrs.backend}
              options={(info?.backend.routes ?? []).map((r) => ({ value: r, label: <span className="mono">{r}</span>, text: r }))}
              onChange={(v) => set("backend", v, false)}
              allowCustom
              clearLabel={`server default${info ? ` (${info.backend.kind})` : ""}`}
              placeholder={`server default${info ? ` · ${info.backend.kind}` : ""}`}
              mono
            />
          </Field>
          <button type="button" className="rs-linkbtn" onClick={() => setLeftPanel("models")}>
            Edit the model stylesheet →
          </button>
        </Section>
      ) : null}

      {meta.pins ? <ArtifactFields node={node} doc={doc} /> : null}

      <Section title="Routes" id="insp-routes" aside={<span className="faint num">{outgoing(doc, node.id).length}</span>}>
        <RouteList node={node} doc={doc} />
      </Section>

      {kind !== "start" && kind !== "exit" ? (
        <Section title="Reliability" id="insp-reliability" collapsible>
          <Toggle checked={attr("goal_gate") === "true"} onChange={(v) => set("goal_gate", v ? "true" : undefined, false)} label="Goal gate" hint="The run can only finish once this stage has succeeded." />
          <div className="rs-insp__grid">
            <Field label="Extra attempts">
              <Stepper value={attr("max_retries") ? Number(attr("max_retries")) : undefined} onChange={(v) => set("max_retries", v?.toString(), false)} max={20} placeholder={doc.graphAttrs.default_max_retries ?? "0"} />
            </Field>
            <Field label="Timeout" hint={attr("timeout") && !isDuration(attr("timeout")) ? "A duration: 90s, 45m, 2h." : undefined}>
              <TextInput value={attr("timeout")} onChange={(v) => set("timeout", v)} placeholder="none" mono invalid={!!attr("timeout") && !isDuration(attr("timeout"))} />
            </Field>
          </div>
          {kind !== "human" && (attr("timeout") || attr("on_timeout") || attr("soft_timeout") || attr("timeout_grace")) ? <TimeoutFields node={node} doc={doc} set={set} llm={meta.llm} /> : null}
          <Field label="Backoff">
            <Segmented size="sm" value={attr("retry_policy") || "default"} options={["default", "none", "standard", "aggressive", "linear", "patient"].map((p) => ({ value: p, label: p }))} onChange={(v) => set("retry_policy", v === "default" ? undefined : v, false)} />
          </Field>
          <Field label="On unrecoverable failure, jump to" hint="Used when this stage fails and no route matches, or when its goal gate is unsatisfied at exit.">
            <Dropdown value={node.attrs.retry_target} options={otherIds} onChange={(v) => set("retry_target", v, false)} searchable clearLabel="nowhere (fail the run)" placeholder={doc.graphAttrs.retry_target ? `graph default · ${doc.graphAttrs.retry_target}` : "nowhere"} />
          </Field>
          <Toggle checked={attr("allow_partial") === "true"} onChange={(v) => set("allow_partial", v ? "true" : undefined, false)} label="Accept partial success" hint="When retries run out on a retry outcome, continue as partial success." />
          <div style={{ height: 10 }} />
          <Toggle checked={attr("auto_status") === "true"} onChange={(v) => set("auto_status", v ? "true" : undefined, false)} label="Auto status" hint="Record success when the handler writes no status." />
        </Section>
      ) : null}

      {kind !== "start" && kind !== "exit" ? <FlowFields node={node} doc={doc} set={set} kind={kind} options={otherIds} /> : null}

      {kind === "agent" || kind === "tool" || kind === "custom" || kind === "supervisor" ? <MetricsFields node={node} doc={doc} set={set} kind={kind} /> : null}

      {meta.llm ? (
        <Section title="Context" id="insp-context" collapsible>
          <Field label="Fidelity" hint={fidelityHint(attr("fidelity") || doc.graphAttrs.default_fidelity || "compact")}>
            <Dropdown
              value={node.attrs.fidelity}
              options={FIDELITIES.map((f) => ({ value: f, label: <span className="mono">{f}</span>, text: f }))}
              onChange={(v) => set("fidelity", v, false)}
              clearLabel={`inherit (${doc.graphAttrs.default_fidelity || "compact"})`}
              placeholder={`inherit · ${doc.graphAttrs.default_fidelity || "compact"}`}
            />
          </Field>
          <ThreadField node={node} doc={doc} set={set} />
          <Field label="Classes" hint="Stylesheet classes. Groups add their label as a class too.">
            <TextInput value={attr("class")} onChange={(v) => set("class", v)} placeholder={nodeClasses(doc, node).join(", ") || "e.g. critical, deep-thinking"} mono />
          </Field>
        </Section>
      ) : null}

      {meta.llm ? (
        <Section title="Instructions" id="insp-instructions" collapsible defaultOpen={!!node.attrs.instructions} aside={eff.instructions ? <span className="faint mono">{eff.instructions.source === "stylesheet" ? `from ${eff.instructions.selector}` : eff.instructions.source === "node" ? "set here" : "default"}</span> : undefined}>
          <Field
            label="Standing instructions"
            hint={
              !node.attrs.instructions && eff.instructions
                ? `Inherits instructions from ${eff.instructions.selector ?? "defaults"}. Text here replaces them for this stage.`
                : `Every backend gives these to the agent as system-level instructions, after ${doc.graphAttrs.house_rules === "false" ? "" : "the built-in house rules and "}the pipeline's instructions${doc.graphAttrs.instructions ? "" : " (none set)"}.`
            }
          >
            <TextArea value={node.attrs.instructions ?? ""} onChange={(v) => set("instructions", v)} placeholder={eff.instructions && !node.attrs.instructions ? eff.instructions.value : "e.g. Use PowerShell syntax; never download browsers."} minRows={2} />
          </Field>
          <Toggle
            checked={(node.attrs.house_rules ?? doc.graphAttrs.house_rules) !== "false"}
            onChange={(v) => set("house_rules", v === (doc.graphAttrs.house_rules !== "false") ? undefined : String(v), false)}
            label="Built-in house rules"
            hint={node.attrs.house_rules ? "Set on this stage." : "Follows the pipeline setting."}
          />
        </Section>
      ) : null}

      {kind === "agent" ? (
        <Section title="Agent limits" id="insp-agent" collapsible defaultOpen={false}>
          <div className="rs-insp__grid">
            <Field label="Tool rounds">
              <TextInput value={attr("agent.max_tool_rounds")} onChange={(v) => set("agent.max_tool_rounds", v)} placeholder="200" mono />
            </Field>
            <Field label="Turns">
              <TextInput value={attr("agent.max_turns")} onChange={(v) => set("agent.max_turns", v)} placeholder="∞" mono />
            </Field>
            <Field label="Output tokens">
              <TextInput value={attr("agent.max_tokens")} onChange={(v) => set("agent.max_tokens", v)} placeholder="default" mono />
            </Field>
            <Field label="Shell timeout">
              <TextInput value={attr("agent.shell_timeout")} onChange={(v) => set("agent.shell_timeout", v)} placeholder="default" mono />
            </Field>
          </div>
          <Field label="Extra system instructions (agent backend)" hint="Legacy: only the agent backend reads agent.instructions. Prefer Instructions above, which every backend honors.">
            <TextArea value={attr("agent.instructions")} onChange={(v) => set("agent.instructions", v)} minRows={2} />
          </Field>
        </Section>
      ) : null}

      {kind === "agent" || kind === "tool" || kind === "supervisor" || kind === "custom" ? (
        <Section title="Workspace" id="insp-workspace" collapsible defaultOpen={!!(attr("write_scope") || attr("concurrency_group"))}>
          <Field label="Write scope" hint="Globs (relative to the working directory) this stage may change, e.g. prototypes/proto_a/**. Changes outside are flagged in the flight record.">
            <TextInput value={attr("write_scope")} onChange={(v) => set("write_scope", v)} placeholder="anywhere" mono />
          </Field>
          <Toggle
            checked={attr("write_scope_enforce") === "true"}
            onChange={(v) => set("write_scope_enforce", v ? "true" : undefined, false)}
            label="Fail when it writes outside the scope"
            hint="The stage fails (without a retry); its files stay as they are."
          />
          <div style={{ height: 10 }} />
          <Field label="Concurrency group" hint="Stages in the same group never run at the same time, e.g. browser-based QA in parallel branches.">
            <TextInput value={attr("concurrency_group")} onChange={(v) => set("concurrency_group", v)} placeholder="none" mono />
          </Field>
          <Toggle
            checked={attr("track_changes") !== "false"}
            onChange={(v) => set("track_changes", v ? undefined : "false", false)}
            label="Record file changes"
            hint="Snapshot the working directory before and after this stage (changes.json)."
          />
        </Section>
      ) : null}

      <Section title="All attributes" id="insp-raw" collapsible defaultOpen={false}>
        <RawAttributes attrs={node.attrs} onSet={(k, v) => applyDoc((d) => setNodeAttr(d, node.id, k, v))} highlightUnknown />
      </Section>

      <div className="rs-insp__footer">
        <Button variant="danger" size="sm" icon="trash" onClick={deleteSelection}>
          Delete stage
        </Button>
      </div>
    </div>
  );
}

function fidelityHint(mode: string) {
  switch (mode) {
    case "full":
      return "Continues the same agent conversation as earlier stages on its thread.";
    case "truncate":
      return "Sees only the goal.";
    case "summary:low":
    case "summary:medium":
    case "summary:high":
      return "Gets a summary of earlier stages, context, and logs (≈600 / 1500 / 3000 tokens).";
    default:
      return "Gets the goal, completed stages with outcomes, and key context values.";
  }
}

type Setter = (k: string, v: string | undefined, c?: boolean) => void;

const ON_TIMEOUT_HINT: Record<string, string> = {
  retry: "A timeout is retried like other transient errors (extra attempts), and the retry is told what the stopped attempt did and said last.",
  fail: "A timeout fails the stage; it is never retried.",
  wrap_up: "At the limit the agent is told time is up and gets the grace period to finish and summarize (agent and copilot backends); then it is stopped.",
};

/** What a stage does when it reaches its `timeout`: retry, fail, or wrap up — and the soft-deadline reminder before it. */
function TimeoutFields({ node, doc, set, llm }: { node: DocNode; doc: StudioDoc; set: Setter; llm: boolean }) {
  const attr = (key: string) => nodeAttr(doc, node, key) ?? "";
  const timeoutMs = parseDuration(attr("timeout"));
  const raw = attr("on_timeout").trim().toLowerCase().replace("-", "_");
  const onTimeout = raw === "wrapup" ? "wrap_up" : raw || "retry";
  const soft = attr("soft_timeout");
  const grace = attr("timeout_grace");
  const options = [
    { value: "retry", label: "retry", title: ON_TIMEOUT_HINT.retry },
    { value: "fail", label: "fail", title: ON_TIMEOUT_HINT.fail },
    ...(llm || onTimeout === "wrap_up" ? [{ value: "wrap_up", label: "wrap up first", title: ON_TIMEOUT_HINT.wrap_up }] : []),
  ];
  return (
    <div className="rs-insp__sub">
      <Field label="When it times out" hint={ON_TIMEOUT_HINT[onTimeout] ?? `on_timeout="${attr("on_timeout")}" is not retry, fail, or wrap_up.`}>
        <Segmented size="sm" value={onTimeout} options={options} onChange={(v) => set("on_timeout", v === "retry" ? undefined : v, false)} />
      </Field>
      {llm ? (
        <div className="rs-insp__grid">
          <Field label="Soft deadline" hint={soft && !isSoftTimeout(soft) ? "A duration (e.g. 70m) or off." : "Reminds the agent to wrap up (agent and copilot backends). off disables it."}>
            <TextInput value={soft} onChange={(v) => set("soft_timeout", v)} placeholder={defaultSoftTimeout(timeoutMs)} mono invalid={!!soft && !isSoftTimeout(soft)} />
          </Field>
          {onTimeout === "wrap_up" || grace ? (
            <Field label="Wrap-up grace" hint={grace && !isDuration(grace) ? "A duration, e.g. 5m." : "Time to finish after the limit."}>
              <TextInput value={grace} onChange={(v) => set("timeout_grace", v)} placeholder={defaultTimeoutGrace(timeoutMs)} mono invalid={!!grace && !isDuration(grace)} />
            </Field>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

const WORK_KINDS = new Set<NodeKind>(["agent", "tool", "supervisor", "custom"]);

/** Unrouted failures (`on_fail`) and loop bounds (`max_visits`, `on_max_visits`). */
function FlowFields({ node, doc, set, kind, options }: { node: DocNode; doc: StudioDoc; set: Setter; kind: NodeKind; options: ReturnType<typeof nodeOption>[] }) {
  const attr = (key: string) => nodeAttr(doc, node, key) ?? "";
  const graphDefault = (doc.graphAttrs.default_on_fail ?? "").trim().toLowerCase();
  const onFail = attr("on_fail").trim().toLowerCase();
  const maxText = attr("max_visits");
  const max = parsePositiveInt(maxText);
  const target = node.attrs.on_max_visits;
  const targetNode = target ? findNode(doc, target) : undefined;
  const effective = onFail || graphDefault || "continue";
  return (
    <Section title="Flow control" id="insp-flow" collapsible aside={max ? <span className="faint mono">≤{max} visits</span> : undefined}>
      {WORK_KINDS.has(kind) ? (
        <Field
          label="If it fails and no route takes the failure"
          hint={
            effective === "stop"
              ? "The run stops with this stage's failure reason (an outcome=fail edge, a chosen label, or a retry target still route it)."
              : onFail === "continue"
                ? "Carrying on is intended: an unconditional edge takes the failure on as if the stage had succeeded."
                : "Per the spec, an unconditional edge takes the failure on as if the stage had succeeded; the validator warns about it."
          }
        >
          <Segmented
            size="sm"
            value={onFail === "stop" || onFail === "continue" ? onFail : "inherit"}
            options={[
              { value: "inherit", label: graphDefault === "stop" ? "pipeline · stop" : graphDefault === "continue" ? "pipeline · carry on" : "pipeline default", title: "Follow the pipeline's default_on_fail" },
              { value: "stop", label: "stop the run", title: 'on_fail="stop"' },
              { value: "continue", label: "carry on", title: 'on_fail="continue": carrying on is intended' },
            ]}
            onChange={(v) => set("on_fail", v === "inherit" ? undefined : v, false)}
          />
        </Field>
      ) : null}
      <div className="rs-insp__grid">
        <Field label="Max visits" hint={maxText && !max ? `max_visits="${maxText}" is not a positive whole number.` : undefined}>
          <Stepper value={max ?? undefined} onChange={(v) => set("max_visits", v?.toString(), false)} min={1} max={99} placeholder="∞" />
        </Field>
        <Field label="Then go to">
          <Dropdown value={target} options={options} onChange={(v) => set("on_max_visits", v, false)} searchable clearLabel="nowhere (fail the run)" placeholder={max ? "fail the run" : "—"} />
        </Field>
      </div>
      <p className="rs-insp__note">
        {max
          ? `Runs at most ${max} time${max === 1 ? "" : "s"} per run; a ${ordinal(max + 1)} visit ${targetNode ? `goes to “${nodeLabel(doc, targetNode)}” instead` : target ? `goes to “${target}”, which does not exist, so the run fails` : "fails the run"}.`
          : target
            ? "Set max visits for the reroute to take effect."
            : "Bound a loop: the most times this stage may run in one run, and where the run goes instead (e.g. a person's review)."}
      </p>
    </Section>
  );
}

const ordinal = (n: number) => `${n}${n % 100 >= 11 && n % 100 <= 13 ? "th" : ["th", "st", "nd", "rd"][n % 10] ?? "th"}`;

/** Outcome metrics: where the stage's numbers come from, and what the flight record has seen. */
function MetricsFields({ node, doc, set, kind }: { node: DocNode; doc: StudioDoc; set: Setter; kind: NodeKind }) {
  const file = node.attrs.metrics_file ?? "";
  const problem = metricsFileProblem(file);
  const record = useTelemetry((s) => s.byName[doc.name]);
  const seen = record?.nodes[node.id]?.metrics;
  const names = seen ? Object.keys(seen) : [];
  return (
    <Section title="Outcome metrics" id="insp-metrics" collapsible defaultOpen={!!file || names.length > 0} aside={names.length ? <span className="faint num">{names.length}</span> : undefined}>
      <Field
        label="Metrics file"
        hint={
          problem ??
          `A JSON report in the working directory whose numbers are recorded as metrics.<name> (nested objects as dotted names). ${kind === "tool" ? "The command can also write $REACTOR_STAGE_DIR/metrics.json." : "Agents can also report metrics in status.json."}`
        }
      >
        <TextInput value={file} onChange={(v) => set("metrics_file", v)} placeholder="qa/report.json" mono invalid={!!problem} />
      </Field>
      {names.length > 0 && seen ? (
        <div className="rs-insp__metrics">
          <span className="label">in the flight record</span>
          {names.map((name) => (
            <span key={name} className="rs-chip" title={`${seen[name].count ?? 0} values · min ${seen[name].min} · max ${seen[name].max} · last ${seen[name].last}`}>
              <span className="rs-chip__key">{name}</span> ⌀ {formatMetric(seen[name].mean)}
            </span>
          ))}
        </div>
      ) : null}
    </Section>
  );
}

/** A metric value for readouts: whole numbers as is, others to two decimals. */
const formatMetric = (n: number) => (Number.isInteger(n) ? String(n) : n.toFixed(2));

function ThreadField({ node, doc, set }: { node: DocNode; doc: StudioDoc; set: (k: string, v: string | undefined, c?: boolean) => void }) {
  const threads = [...new Set(doc.nodes.map((n) => nodeAttr(doc, n, "thread_id")).filter((t): t is string => !!t))];
  const fidelity = nodeAttr(doc, node, "fidelity") || doc.graphAttrs.default_fidelity;
  return (
    <Field label="Thread" hint={fidelity === "full" ? "Stages on one thread share a conversation — like stops on one line." : "Threads apply to stages with fidelity full."}>
      <Dropdown
        value={node.attrs.thread_id}
        options={threads.map((t) => ({ value: t, label: <span className="rs-kind-option"><span className="rs-dot" style={{ background: lineColor(t, doc.layout.threads?.[t]?.color) }} /><span className="mono">{t}</span></span>, text: t }))}
        onChange={(v) => set("thread_id", v, false)}
        allowCustom
        clearLabel="none"
        placeholder="none"
      />
    </Field>
  );
}

function HumanFields({ node, doc, set }: { node: DocNode; doc: StudioDoc; set: (k: string, v: string | undefined, c?: boolean) => void }) {
  const attr = (key: string) => nodeAttr(doc, node, key) ?? "";
  const options = outgoing(doc, node.id).map((e) => ({ value: e.to, label: e.attrs.label || e.to, text: `${e.attrs.label ?? ""} ${e.to}` }));
  const freeform = attr("human.input") === "freeform";
  return (
    <>
      <Toggle checked={freeform} onChange={(v) => set("human.input", v ? "freeform" : undefined, false)} label="Free-text answer" hint="Collect text into human.gate.text instead of choosing a route." />
      {freeform ? (
        <Field label="Prompt for the person">
          <TextArea value={attr("human.question")} onChange={(v) => set("human.question", v)} minRows={2} placeholder={attr("label")} />
        </Field>
      ) : null}
      <div className="rs-insp__grid" style={{ marginTop: 14 }}>
        <Field label="Timeout">
          <TextInput value={attr("human.timeout")} onChange={(v) => set("human.timeout", v)} placeholder="never" mono />
        </Field>
        <Field label="On timeout">
          <Dropdown value={node.attrs["human.default_choice"]} options={options} onChange={(v) => set("human.default_choice", v, false)} clearLabel="retry the question" placeholder="retry" />
        </Field>
      </div>
    </>
  );
}

function SupervisorFields({ node, doc, set }: { node: DocNode; doc: StudioDoc; set: (k: string, v: string | undefined, c?: boolean) => void }) {
  const attr = (key: string) => nodeAttr(doc, node, key) ?? "";
  const actions = new Set((attr("manager.actions") || "observe,wait").split(",").map((s) => s.trim()));
  const toggleAction = (a: string, on: boolean) => {
    const next = new Set(actions);
    if (on) next.add(a);
    else next.delete(a);
    set("manager.actions", [...next].join(","), false);
  };
  return (
    <>
      <Field label="Child pipeline" hint={doc.graphAttrs["stack.child_dotfile"] && !node.attrs["stack.child_dotfile"] ? `Graph default: ${doc.graphAttrs["stack.child_dotfile"]}` : "A .dot file, relative to this pipeline."}>
        <TextInput value={attr("stack.child_dotfile")} onChange={(v) => set("stack.child_dotfile", v)} placeholder={doc.graphAttrs["stack.child_dotfile"] ?? "worker.dot"} mono />
      </Field>
      <div className="rs-insp__grid">
        <Field label="Poll every">
          <TextInput value={attr("manager.poll_interval")} onChange={(v) => set("manager.poll_interval", v)} placeholder="45s" mono />
        </Field>
        <Field label="Max cycles">
          <TextInput value={attr("manager.max_cycles")} onChange={(v) => set("manager.max_cycles", v)} placeholder="1000" mono />
        </Field>
      </div>
      <Field label="Actions">
        <div className="rs-insp__checks">
          {["observe", "steer", "wait"].map((a) => (
            <Toggle key={a} checked={actions.has(a)} onChange={(v) => toggleAction(a, v)} label={a} />
          ))}
        </div>
      </Field>
      {actions.has("steer") ? (
        <Field label="Steering message" hint="Written to the child's active stage when it runs long.">
          <TextArea value={attr("manager.steer_prompt")} onChange={(v) => set("manager.steer_prompt", v)} minRows={2} />
        </Field>
      ) : null}
    </>
  );
}

function ArtifactFields({ node, doc }: { node: DocNode; doc: StudioDoc }) {
  const inputs = parseInputs(node.attrs.inputs);
  const outputs = parseOutputs(node.attrs.outputs);
  const index = artifactIndex(doc);
  const known = [...index.keys()].filter((name) => !inputs.includes(name));
  const [newOut, setNewOut] = useState("");
  const setInputs = (list: string[]) => applyDoc((d) => setNodeAttr(d, node.id, "inputs", formatInputs(list)));
  const setOutputs = (list: { name: string; path?: string }[]) => applyDoc((d) => setNodeAttr(d, node.id, "outputs", formatOutputs(list)), { coalesce: `outputs:${node.id}` });
  return (
    <Section title="Artifacts" id="insp-artifacts" aside={<span className="faint">data pins</span>}>
      <Field label="Reads" hint="Each input appears in the prompt with where to find it.">
        <div className="rs-pinlist">
          {inputs.map((name) => {
            const info = index.get(name);
            const producers = info?.producers.filter((p) => p.node !== node.id) ?? [];
            return (
              <span key={name} className="rs-pinchip" style={{ ["--pin" as string]: info?.color ?? lineColor(name) }} title={producers.length ? `from ${producers.map((p) => p.node).join(", ")}` : "No stage produces this yet"}>
                <span className="rs-pinchip__dot" />
                {name}
                {producers.length === 0 ? <span className="rs-pinchip__warn">!</span> : null}
                <button type="button" onClick={() => setInputs(inputs.filter((i) => i !== name))} aria-label={`Remove ${name}`}>
                  <Glyph name="close" size={10} />
                </button>
              </span>
            );
          })}
          <Dropdown
            value={undefined}
            options={known.map((name) => ({ value: name, label: <span className="rs-kind-option"><span className="rs-dot" style={{ background: index.get(name)!.color }} /><span className="mono">{name}</span></span>, text: name }))}
            onChange={(v) => v && isArtifactName(v) && setInputs([...inputs, v])}
            allowCustom
            placeholder="+ read an artifact"
            width={240}
          />
        </div>
      </Field>
      <Field label="Produces" hint="A path makes a file artifact the stage must write (checked when it finishes). No path records the stage's final text.">
        <div className="rs-outlist">
          {outputs.map((o, i) => (
            <div key={i} className="rs-outrow" style={{ ["--pin" as string]: index.get(o.name)?.color ?? lineColor(o.name) }}>
              <span className="rs-pinchip__dot" />
              <TextInput value={o.name} mono onCommit={(v) => isArtifactName(v) && setOutputs(outputs.map((x, j) => (j === i ? { ...x, name: v } : x)))} />
              <TextInput value={o.path ?? ""} mono placeholder="text (no file)" onCommit={(v) => setOutputs(outputs.map((x, j) => (j === i ? { name: x.name, path: v.trim() || undefined } : x)))} />
              <Button size="sm" variant="ghost" icon="close" onClick={() => setOutputs(outputs.filter((_, j) => j !== i))} title="Remove output" />
            </div>
          ))}
          <div className="rs-outrow is-new">
            <TextInput value={newOut} mono placeholder="new_artifact" onChange={setNewOut} />
            <Button
              size="sm"
              icon="plus"
              disabled={!isArtifactName(newOut) || outputs.some((o) => o.name === newOut)}
              onClick={() => {
                setOutputs([...outputs, { name: newOut }]);
                setNewOut("");
              }}
            >
              Add
            </Button>
          </div>
        </div>
      </Field>
    </Section>
  );
}

function RouteList({ node, doc }: { node: DocNode; doc: StudioDoc }) {
  const edges = outgoing(doc, node.id);
  const handler = handlerOf(doc, node);
  if (edges.length === 0) {
    return <p className="rs-insp__note">{handler === "exit" ? "The exit has no routes." : "No routes yet. Drag from a port on the card to another stage."}</p>;
  }
  return (
    <div className="rs-routelist">
      {edges.map((e, i) => {
        const target = findNode(doc, e.to);
        return (
          <div key={e.uid} className="rs-routelist__row">
            <button type="button" className="rs-routelist__main" onClick={() => select({ edges: [e.uid] })}>
              <span className={cx("rs-routelist__kind", `is-${routeKeyOf(doc, e, handler).split(":")[0]}`)} />
              <span className="rs-routelist__desc">{describeEdge(doc, e)}</span>
              <span className="rs-routelist__to">
                → {target ? nodeLabel(doc, target) : e.to}
              </span>
            </button>
            {handler === "wait.human" ? (
              <span className="rs-routelist__order">
                <Button size="sm" variant="ghost" icon="chevronUp" disabled={i === 0} onClick={() => applyDoc((d) => moveEdge(d, e.uid, -1))} title="Move up" />
                <Button size="sm" variant="ghost" icon="chevronDown" disabled={i === edges.length - 1} onClick={() => applyDoc((d) => moveEdge(d, e.uid, 1))} title="Move down" />
              </span>
            ) : null}
          </div>
        );
      })}
    </div>
  );
}

// ---------- edge ----------

function EdgeInspector({ edge, doc }: { edge: DocEdge; doc: StudioDoc }) {
  const source = findNode(doc, edge.from);
  const target = findNode(doc, edge.to);
  const handler = source ? handlerOf(doc, source) : "codergen";
  const set = (patch: Record<string, string | undefined>, coalesce?: string) => applyDoc((d) => setEdgeAttrs(d, edge.uid, patch), { coalesce });
  const condition = edgeAttr(doc, edge, "condition") ?? "";
  const label = edgeAttr(doc, edge, "label") ?? "";
  const mode = condition === "outcome=success" ? "success" : condition === "outcome=fail" ? "fail" : condition ? "condition" : label ? "label" : "always";
  const siblings = outgoing(doc, edge.from);
  return (
    <div className="rs-insp">
      <InspectorHead icon="link" kicker="Connection" title={<span className="rs-insp__route">{source ? nodeLabel(doc, source) : edge.from} <span className="faint">→</span> {target ? nodeLabel(doc, target) : edge.to}</span>}>
        <p className="rs-insp__sentence serif">Taken {describeEdge(doc, edge)}.</p>
      </InspectorHead>
      {handler === "wait.human" ? (
        <Section title="Option">
          <Field label="Label" hint="Shown to the person. [K] Label sets the accelerator key.">
            <TextInput value={label} onChange={(v) => set({ label: v || undefined }, `edge:${edge.uid}:label`)} placeholder={target ? `[${(nodeLabel(doc, target)[0] ?? "X").toUpperCase()}] ${nodeLabel(doc, target)}` : ""} />
          </Field>
          {label && !acceleratorOf(label) ? (
            <button type="button" className="rs-linkbtn" onClick={() => set({ label: `[${label.replace(/[^A-Za-z0-9]/g, "")[0]?.toUpperCase() ?? "X"}] ${label}` })}>
              Add an accelerator key
            </button>
          ) : null}
          <div className="rs-insp__row">
            <Button size="sm" variant="quiet" icon="chevronUp" onClick={() => applyDoc((d) => moveEdge(d, edge.uid, -1))} disabled={siblings[0]?.uid === edge.uid}>
              Earlier
            </Button>
            <Button size="sm" variant="quiet" icon="chevronDown" onClick={() => applyDoc((d) => moveEdge(d, edge.uid, 1))} disabled={siblings[siblings.length - 1]?.uid === edge.uid}>
              Later
            </Button>
          </div>
        </Section>
      ) : handler === "parallel" ? (
        <Section title="Branch">
          <p className="rs-insp__note">Every outgoing connection of a fan-out is a branch that runs concurrently on its own copy of the context.</p>
        </Section>
      ) : (
        <Section title="When to take this route">
          <Segmented
            size="sm"
            value={mode}
            options={[
              { value: "always", label: "Always" },
              { value: "success", label: "✓ Success" },
              { value: "fail", label: "✕ Fail" },
              { value: "label", label: "Label" },
              { value: "condition", label: "ƒ Condition" },
            ]}
            onChange={(v) => {
              if (v === "always") set({ condition: undefined, label: undefined });
              else if (v === "success") set({ condition: "outcome=success" });
              else if (v === "fail") set({ condition: "outcome=fail" });
              else if (v === "label") set({ condition: undefined, label: label || "Approve" });
              else set({ condition: condition && mode === "condition" ? condition : "context.ready=true" });
            }}
          />
          {mode === "condition" || mode === "success" || mode === "fail" ? <ConditionBuilder value={condition} onChange={(v) => set({ condition: v }, `edge:${edge.uid}:cond`)} doc={doc} from={edge.from} /> : null}
          <Field label="Label" hint={mode === "label" ? "Taken when the stage reports this as its preferred label (status.json), e.g. a reviewer choosing Approve." : "Optional caption. Agents may also pick a route by its label."}>
            <TextInput value={label} onChange={(v) => set({ label: v || undefined }, `edge:${edge.uid}:label`)} placeholder="e.g. Approve" />
          </Field>
        </Section>
      )}
      <Section title="Advanced" collapsible defaultOpen={false} id="insp-edge-adv">
        <Field label="Weight" inline hint="Higher wins among otherwise equal routes.">
          <Stepper value={edge.attrs.weight ? Number(edge.attrs.weight) : undefined} onChange={(v) => set({ weight: v?.toString() })} min={-10} max={100} placeholder="0" />
        </Field>
        <Field label="Fidelity on arrival">
          <Dropdown value={edge.attrs.fidelity} options={FIDELITIES.map((f) => ({ value: f, label: <span className="mono">{f}</span>, text: f }))} onChange={(v) => set({ fidelity: v })} clearLabel="target's own" placeholder="target's own" />
        </Field>
        <Field label="Thread on arrival">
          <TextInput value={edge.attrs.thread_id ?? ""} onChange={(v) => set({ thread_id: v || undefined }, `edge:${edge.uid}:thread`)} mono placeholder="target's own" />
        </Field>
        <Toggle checked={edge.attrs.loop_restart === "true"} onChange={(v) => set({ loop_restart: v ? "true" : undefined })} label="Restart the run here" hint="Start over from the target with fresh context, in a new run directory." />
        <div style={{ height: 14 }} />
        <RawAttributes attrs={edge.attrs} onSet={(k, v) => applyDoc((d) => setEdgeAttr(d, edge.uid, k, v))} />
      </Section>
      <div className="rs-insp__footer">
        <Button variant="danger" size="sm" icon="trash" onClick={deleteSelection}>
          Delete connection
        </Button>
      </div>
    </div>
  );
}

/**
 * Keys the clause editor suggests for a pipeline: `visits.<stage>` for its work stages, `metrics.<name>` for the metrics
 * its conditions test, its wind tunnel scenario declares, the latest tunnel report saw, or its flight record has, and the
 * context keys its other conditions use.
 */
function useConditionKeys(doc: StudioDoc): KeySuggestion[] {
  const record = useTelemetry((s) => s.byName[doc.name]);
  const scenario = useTunnel((s) => s.settings.nodes);
  const report = useTunnel((s) => s.report);
  return useMemo(() => {
    const conditions = doc.edges.map((e) => edgeAttr(doc, e, "condition"));
    const metrics = metricsInConditions(conditions);
    const add = (names: Iterable<string>) => {
      for (const name of names) if (!metrics.includes(name)) metrics.push(name);
    };
    for (const behavior of Object.values(scenario ?? {})) add(Object.keys(behavior.metrics ?? {}));
    for (const stats of Object.values(report?.nodes ?? {})) add(Object.keys(stats.metrics ?? {}));
    add(Object.keys(report?.landed_metrics ?? {}));
    add(Object.keys(record?.metrics ?? {}));
    for (const stats of Object.values(record?.nodes ?? {})) add(Object.keys(stats.metrics ?? {}));
    const context: string[] = [];
    for (const condition of conditions) {
      for (const clause of parseCondition(condition)) {
        const key = clause.key;
        // Skip what is still being typed (`context.`, `visits.`) and the keys with their own groups.
        if (key.endsWith(".") || key === "outcome" || key === "preferred_label" || visitsNode(key) || metricName(key) || context.includes(key)) continue;
        context.push(key);
      }
    }
    const stages = doc.nodes.filter((n) => !["start", "exit"].includes(handlerOf(doc, n))).map((n) => ({ id: n.id, label: nodeLabel(doc, n) }));
    return keySuggestions(stages, metrics, context);
  }, [doc, record, scenario, report]);
}

function ConditionBuilder({ value, onChange, doc, from }: { value: string; onChange: (value: string | undefined) => void; doc: StudioDoc; from: string }) {
  const clauses = parseCondition(value);
  const suggestions = useConditionKeys(doc);
  const options = useMemo(() => suggestions.map((s) => ({ value: s.key, group: s.group, detail: s.detail })), [suggestions]);
  const update = (next: Clause[]) => onChange(formatCondition(next));
  const patch = (i: number, p: Partial<Clause>) => update(clauses.map((x, j) => (j === i ? { ...x, ...p } : x)));
  const firstMetric = suggestions.find((s) => s.group === "Metrics")?.key ?? "metrics.score";
  return (
    <div className="rs-cond">
      {clauses.map((c, i) => {
        const problem = clauseProblem(c);
        return (
          <div key={i} className="rs-cond__clause">
            <div className="rs-cond__row">
              {i > 0 ? <span className="rs-cond__and mono">and</span> : <span className="rs-cond__and mono">if</span>}
              <ComboInput className="rs-cond__key mono" value={c.key} onChange={(key) => patch(i, { key })} options={options} invalid={!c.key.trim()} placeholder="key" />
              <select
                className="rs-cond__op mono"
                value={c.op}
                onChange={(e) => {
                  const op = e.target.value as Clause["op"];
                  // Numbers need a number: switching to a comparison seeds one; switching away from "is set" seeds a value.
                  const value = isNumericOp(op) && !isNumericOp(c.op) && !/^\d/.test(c.value) ? "3" : c.op === "truthy" && op !== "truthy" && !c.value ? (c.key === "outcome" ? "success" : "") : c.value;
                  patch(i, { op, value });
                }}
              >
                {OPERATORS.map((o) => (
                  <option key={o.op} value={o.op} title={o.title}>
                    {o.symbol}
                  </option>
                ))}
              </select>
              {c.op !== "truthy" ? (
                c.key === "outcome" && (c.op === "=" || c.op === "!=") ? (
                  <select className="rs-cond__value mono" value={c.value} onChange={(e) => patch(i, { value: e.target.value })}>
                    {(OUTCOMES as readonly string[]).includes(c.value) ? null : <option value={c.value}>{c.value || "—"}</option>}
                    {OUTCOMES.map((o) => (
                      <option key={o} value={o}>
                        {o}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    className={cx("rs-cond__value mono", problem && c.key.trim() && "is-invalid")}
                    value={c.value}
                    inputMode={isNumericOp(c.op) || visitsNode(c.key) || metricName(c.key) ? "decimal" : undefined}
                    placeholder={isNumericOp(c.op) ? "number" : "value"}
                    onChange={(e) => patch(i, { value: e.target.value })}
                    onKeyDown={(e) => e.stopPropagation()}
                  />
                )
              ) : (
                <span />
              )}
              <button type="button" className="rs-cond__remove" onClick={() => update(clauses.filter((_, j) => j !== i))} aria-label="Remove clause">
                <Glyph name="close" size={10} />
              </button>
            </div>
            {problem && c.key.trim() ? <div className="rs-cond__problem">{problem}</div> : null}
          </div>
        );
      })}
      <div className="rs-cond__add">
        <button type="button" className="rs-linkbtn" onClick={() => update([...clauses, { key: "outcome", op: "=", value: "success" }])}>
          + outcome
        </button>
        <button type="button" className="rs-linkbtn" title="How many times a stage has run in this run (counts the current visit)" onClick={() => update([...clauses, { key: `visits.${from}`, op: "<", value: "3" }])}>
          + visits
        </button>
        <button type="button" className="rs-linkbtn" title="The latest value of an outcome metric a stage reported" onClick={() => update([...clauses, { key: firstMetric, op: ">=", value: "4" }])}>
          + metric
        </button>
        <button type="button" className="rs-linkbtn" onClick={() => update([...clauses, { key: "context.", op: "=", value: "true" }])}>
          + context value
        </button>
        <button type="button" className="rs-linkbtn" onClick={() => update([...clauses, { key: "preferred_label", op: "=", value: "" }])}>
          + label
        </button>
      </div>
      <div className="rs-cond__raw mono">{value || "always"}</div>
    </div>
  );
}
// ---------- multi / group / note / graph ----------

function MultiInspector({ ids }: { ids: string[] }) {
  const doc = useStudio((s) => s.doc);
  const [label, setLabel] = useState("New group");
  return (
    <div className="rs-insp">
      <InspectorHead icon="outline" kicker="Selection" title={`${ids.length} stages`} />
      <Section title="Group">
        <Field label="Group label" hint="Grouped stages share a stylesheet class derived from the label.">
          <TextInput value={label} onChange={setLabel} />
        </Field>
        <Button size="sm" icon="group" onClick={() => groupSelection(label)}>
          Group selection
        </Button>
      </Section>
      <Section title="Stages">
        {ids.map((id) => {
          const n = findNode(doc, id);
          return n ? (
            <button key={id} type="button" className="rs-outline__row" onClick={() => focusNode(id)}>
              <Glyph name={kindOf(doc, n)} size={13} /> <span className="rs-outline__label">{nodeLabel(doc, n)}</span>
            </button>
          ) : null;
        })}
      </Section>
      <div className="rs-insp__footer">
        <Button variant="danger" size="sm" icon="trash" onClick={deleteSelection}>
          Delete {ids.length} stages
        </Button>
      </div>
    </div>
  );
}

function GroupInspector({ uid }: { uid: string }) {
  const doc = useStudio((s) => s.doc);
  const group = groupById(doc, uid)!;
  const members = doc.nodes.filter((n) => n.group === uid);
  const label = group.attrs.label ?? "";
  const derived = label.trim().toLowerCase().replace(/\s+/g, "-").replace(/[^a-z0-9-]/g, "");
  return (
    <div className="rs-insp">
      <InspectorHead icon="group" kicker="Group · subgraph" title={label || group.name || "Group"} />
      <Section title="Group">
        <Field label="Label" hint={derived ? `Members get the stylesheet class .${derived}` : "A label gives members a stylesheet class."}>
          <TextInput value={label} onChange={(v) => applyDoc((d) => updateGroup(d, uid, { attrs: { ...group.attrs, label: v } }), { coalesce: `group:${uid}` })} />
        </Field>
        <Field label="Subgraph name">
          <TextInput value={group.name ?? ""} mono onCommit={(v) => IDENT.test(v) && applyDoc((d) => ({ ...updateGroup(d, uid, { name: v }) }))} />
        </Field>
      </Section>
      <Section title="Defaults for members" collapsible defaultOpen={false} id="insp-group-defaults">
        <RawAttributes attrs={group.nodeDefaults} onSet={(k, v) => applyDoc((d) => updateGroup(d, uid, { nodeDefaults: withAttr(group.nodeDefaults, k, v) }))} />
      </Section>
      <Section title="Members" aside={<span className="faint num">{members.length}</span>}>
        {members.map((n) => (
          <div key={n.id} className="rs-insp__member">
            <button type="button" className="rs-outline__row" onClick={() => focusNode(n.id)}>
              <Glyph name={kindOf(doc, n)} size={13} /> <span className="rs-outline__label">{nodeLabel(doc, n)}</span>
            </button>
            <Button size="sm" variant="ghost" icon="close" title="Remove from group" onClick={() => applyDoc((d) => setNodeGroup(d, [n.id], group.parent))} />
          </div>
        ))}
      </Section>
      <div className="rs-insp__footer">
        <Button variant="quiet" size="sm" icon="close" onClick={() => applyDoc((d) => removeGroup(d, uid), { select: {} })}>
          Ungroup (keep stages)
        </Button>
      </div>
    </div>
  );
}

const withAttr = (attrs: Record<string, string>, key: string, value: string | undefined) => {
  const next = { ...attrs };
  if (value === undefined) delete next[key];
  else next[key] = value;
  return next;
};

const NOTE_COLORS = ["#e8b84a", "#7ea4f5", "#52c9a3", "#f08a6c", "#b394ec", "#9a958c"];

function NoteInspector({ id }: { id: string }) {
  const note = useStudio((s) => s.doc.layout.notes.find((n) => n.id === id));
  if (!note) return null;
  const patch = (p: Partial<typeof note>) => applyDoc((d) => ({ ...d, layout: { ...d.layout, notes: d.layout.notes.map((n) => (n.id === id ? { ...n, ...p } : n)) } }), { coalesce: `note:${id}` });
  return (
    <div className="rs-insp">
      <InspectorHead icon="note" kicker="Note" title="Annotation" />
      <Section title="Text">
        <TextArea value={note.text} onChange={(text) => patch({ text })} minRows={5} />
        <div className="rs-swatches">
          {NOTE_COLORS.map((c) => (
            <button key={c} type="button" className={cx("rs-swatch", (note.color ?? NOTE_COLORS[0]) === c && "is-on")} style={{ background: c }} onClick={() => patch({ color: c })} aria-label="Note color" />
          ))}
        </div>
        <p className="rs-insp__note">Notes live in the layout file; the engine never sees them.</p>
      </Section>
      <div className="rs-insp__footer">
        <Button variant="danger" size="sm" icon="trash" onClick={deleteSelection}>
          Delete note
        </Button>
      </div>
    </div>
  );
}

function GraphInspector() {
  const doc = useStudio((s) => s.doc);
  const analysis = useStudio((s) => s.analysis);
  const setG = (key: string, value: string | undefined, coalesce = true) => applyDoc((d) => setGraphAttr(d, key, value === "" ? undefined : value), { coalesce: coalesce ? `graph:${key}` : undefined });
  const ids = doc.nodes.map((n) => nodeOption(doc, n));
  const stats = useMemo(
    () => ({
      stages: doc.nodes.length,
      routes: doc.edges.length,
      gates: doc.nodes.filter((n) => nodeAttr(doc, n, "goal_gate") === "true").length,
      artifacts: artifactIndex(doc).size,
    }),
    [doc],
  );
  const errors = analysis.diagnostics.filter((d) => d.severity === "error").length;
  const defaultOnFail = (doc.graphAttrs.default_on_fail ?? "").trim().toLowerCase();
  const maxCost = doc.graphAttrs.max_cost ?? "";
  return (
    <div className="rs-insp">
      <InspectorHead icon="design" kicker="Pipeline" title={doc.graphAttrs.label || doc.name}>
        <div className="rs-insp__stats">
          <span>
            <b className="num">{stats.stages}</b> stages
          </span>
          <span>
            <b className="num">{stats.routes}</b> routes
          </span>
          <span>
            <b className="num">{stats.gates}</b> gates
          </span>
          <span>
            <b className="num">{stats.artifacts}</b> artifacts
          </span>
          <span className={cx(errors ? "rs-fail-text" : analysis.valid ? "rs-ok-text" : "")}>{analysis.parseError ? "syntax error" : errors ? `${errors} errors` : "valid"}</span>
        </div>
      </InspectorHead>
      <Section title="Goal">
        <TextArea value={doc.graphAttrs.goal ?? ""} onChange={(v) => setG("goal", v)} placeholder="What should this factory deliver? Prompts refer to it as $goal." minRows={3} className="rs-goal" />
      </Section>
      <Section title="Identity">
        <Field label="Name" hint="The digraph name; run directories are named after it.">
          <TextInput value={doc.name} mono onCommit={(v) => IDENT.test(v) && applyDoc((d) => ({ ...d, name: v }))} />
        </Field>
        <Field label="Display label">
          <TextInput value={doc.graphAttrs.label ?? ""} onChange={(v) => setG("label", v)} placeholder={doc.name} />
        </Field>
      </Section>
      <Section title="Defaults" id="insp-graph-defaults" collapsible>
        <Field label="Context fidelity">
          <Dropdown value={doc.graphAttrs.default_fidelity} options={FIDELITIES.map((f) => ({ value: f, label: <span className="mono">{f}</span>, text: f }))} onChange={(v) => setG("default_fidelity", v, false)} clearLabel="compact" placeholder="compact" />
        </Field>
        <div className="rs-insp__grid">
          <Field label="Extra attempts">
            <Stepper value={doc.graphAttrs.default_max_retries ? Number(doc.graphAttrs.default_max_retries) : undefined} onChange={(v) => setG("default_max_retries", v?.toString(), false)} max={20} placeholder="0" />
          </Field>
          <Field label="Default thread">
            <TextInput value={doc.graphAttrs.default_thread_id ?? ""} onChange={(v) => setG("default_thread_id", v)} mono placeholder="none" />
          </Field>
        </div>
        <Field label="Retry target" hint="Where to go when a goal gate is unsatisfied at exit, or a failed stage has nowhere to go.">
          <Dropdown value={doc.graphAttrs.retry_target} options={ids} onChange={(v) => setG("retry_target", v, false)} searchable clearLabel="none" placeholder="none" />
        </Field>
        <Field label="Fallback retry target">
          <Dropdown value={doc.graphAttrs.fallback_retry_target} options={ids} onChange={(v) => setG("fallback_retry_target", v, false)} searchable clearLabel="none" placeholder="none" />
        </Field>
      </Section>
      <Section title="Failures and budget" id="insp-graph-flow" collapsible>
        <Field
          label="When a stage fails and no route takes the failure"
          hint={
            defaultOnFail === "stop"
              ? "The run stops with the stage's failure reason. Stages can still say on_fail=continue."
              : defaultOnFail === "continue"
                ? "Carrying on is intended everywhere: an unconditional edge takes a failure on as if the stage had succeeded."
                : "Per the spec, an unconditional edge takes a failure on as if the stage had succeeded — the run can “land” without the stage's work. The validator warns about each such stage."
          }
        >
          <Segmented
            size="sm"
            value={defaultOnFail === "stop" || defaultOnFail === "continue" ? defaultOnFail : "inherit"}
            options={[
              { value: "inherit", label: "carry on (spec)", title: "No default_on_fail" },
              { value: "continue", label: "carry on, intended", title: 'default_on_fail="continue"' },
              { value: "stop", label: "stop the run", title: 'default_on_fail="stop"' },
            ]}
            onChange={(v) => setG("default_on_fail", v === "inherit" ? undefined : v, false)}
          />
        </Field>
        <Field
          label="Budget per run (US dollars)"
          hint={
            maxCost && parseUsd(maxCost) === null
              ? "A non-negative number of US dollars, e.g. 25 or 7.5."
              : "Once a run's recorded cost reaches it, the run stops before its next stage and can be resumed with a higher budget. Stages whose backend reports no cost are not counted."
          }
        >
          <TextInput value={maxCost} onChange={(v) => setG("max_cost", v)} placeholder="no limit" mono invalid={!!maxCost && parseUsd(maxCost) === null} />
        </Field>
      </Section>
      <Section title="Models" id="insp-graph-models" collapsible>
        <p className="rs-insp__note">{doc.graphAttrs.model_stylesheet ? `${doc.graphAttrs.model_stylesheet.split("{").length - 1} stylesheet rules.` : "No stylesheet: stages use the backend's default model."}</p>
        <Button size="sm" icon="models" onClick={() => setLeftPanel("models")}>
          Open model stylesheet
        </Button>
      </Section>
      <Section title="Instructions" id="insp-graph-instructions" collapsible defaultOpen={!!doc.graphAttrs.instructions}>
        <Field label="Pipeline instructions" hint="House rules for every LLM stage, on every backend (stages and stylesheet rules can add their own).">
          <TextArea value={doc.graphAttrs.instructions ?? ""} onChange={(v) => setG("instructions", v)} placeholder="e.g. Use PowerShell syntax. Keep scratch files under .scratch/." minRows={3} />
        </Field>
        <Toggle
          checked={doc.graphAttrs.house_rules !== "false"}
          onChange={(v) => setG("house_rules", v ? undefined : "false", false)}
          label="Built-in house rules"
          hint="Reactor's defaults for agents with tools: write large files in parts, keep scratch files out of the product, assume rather than ask, stop processes you start, and end with a summary."
        />
      </Section>
      <Section title="Tool hooks" id="insp-graph-hooks" collapsible defaultOpen={false}>
        <Field label="Before each agent tool call" hint="A non-zero exit blocks the call. Gets REACTOR_TOOL_NAME etc. and JSON on stdin.">
          <TextInput value={doc.graphAttrs["tool_hooks.pre"] ?? ""} onChange={(v) => setG("tool_hooks.pre", v)} mono placeholder="none" />
        </Field>
        <Field label="After each agent tool call">
          <TextInput value={doc.graphAttrs["tool_hooks.post"] ?? ""} onChange={(v) => setG("tool_hooks.post", v)} mono placeholder="none" />
        </Field>
      </Section>
      <Section title="All graph attributes" id="insp-graph-raw" collapsible defaultOpen={false}>
        <RawAttributes attrs={doc.graphAttrs} onSet={(k, v) => applyDoc((d) => setGraphAttr(d, k, v))} />
      </Section>
    </div>
  );
}

function RawAttributes({ attrs, onSet, highlightUnknown }: { attrs: Record<string, string>; onSet: (key: string, value: string | undefined) => void; highlightUnknown?: boolean }) {
  const [key, setKey] = useState("");
  const [value, setValue] = useState("");
  return (
    <div className="rs-raw">
      {Object.entries(attrs).map(([k, v]) => (
        <div key={k} className={cx("rs-raw__row", highlightUnknown && !KNOWN_NODE_KEYS.has(k) && "is-unknown")} title={highlightUnknown && !KNOWN_NODE_KEYS.has(k) ? "Not a Reactor attribute — kept for Graphviz and other tools" : undefined}>
          <span className="rs-raw__key mono">{k}</span>
          <TextInput value={v} mono onCommit={(nv) => onSet(k, nv)} />
          <Button size="sm" variant="ghost" icon="close" onClick={() => onSet(k, undefined)} title="Remove" />
        </div>
      ))}
      <div className="rs-raw__row is-new">
        <TextInput value={key} mono placeholder="attribute" onChange={setKey} />
        <TextInput value={value} mono placeholder="value" onChange={setValue} />
        <Button
          size="sm"
          variant="ghost"
          icon="plus"
          disabled={!/^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(key)}
          onClick={() => {
            onSet(key, value);
            setKey("");
            setValue("");
          }}
          title="Add attribute"
        />
      </div>
    </div>
  );
}
