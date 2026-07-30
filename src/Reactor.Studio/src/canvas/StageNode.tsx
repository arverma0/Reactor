import { Handle, Position, useConnection, useUpdateNodeInternals, type Node, type NodeProps } from "@xyflow/react";
import { memo, useEffect, useMemo, useState } from "react";
import { cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { KINDS } from "../model/handlers";
import type { Route } from "../model/routes";
import type { StageData } from "./build";
import { canvasActions } from "./actions";
import { Elapsed } from "./Elapsed";

const ROUTE_GLYPH: Record<Route["kind"], string> = {
  next: "→",
  success: "✓",
  partial: "◐",
  fail: "✕",
  label: "◆",
  cond: "ƒ",
  choice: "",
  branch: "⋔",
};

function RouteRow({ route, nodeId }: { route: Route; nodeId: string }) {
  return (
    <div className={cx("rs-route", `rs-route--${route.kind}`, route.ghost && "is-ghost")} title={route.ghost ? `Drag to connect: ${route.text}` : route.text}>
      {route.kind === "choice" && route.detail ? <span className="rs-route__key">{route.detail}</span> : <span className="rs-route__glyph">{ROUTE_GLYPH[route.kind]}</span>}
      <span className="rs-route__text">{route.text}</span>
      {route.edges.length > 1 && route.kind !== "branch" ? <span className="rs-route__count">×{route.edges.length}</span> : null}
      <Handle type="source" position={Position.Right} id={`out:${route.key}`} className={cx("rs-port", `rs-port--${route.kind}`)} data-node={nodeId} />
    </div>
  );
}

/** The port of a stage's visit limit: where the run goes once `max_visits` is used up. Drag it to a stage to set `on_max_visits`. */
function RerouteRow({ reroute, nodeId }: { reroute: NonNullable<StageData["reroute"]>; nodeId: string }) {
  const text = `after ${reroute.max} visit${reroute.max === 1 ? "" : "s"}`;
  return (
    <div
      className={cx("rs-route rs-route--visits", !reroute.target && "is-dead")}
      title={reroute.target ? `${text}, the run goes to “${reroute.targetLabel ?? reroute.target}” (on_max_visits)` : `${text}, the run fails — drag to the stage that should take over (on_max_visits)`}
    >
      <span className="rs-route__glyph">⟲</span>
      <span className="rs-route__text">{text}</span>
      {!reroute.target ? <span className="rs-route__count">fail</span> : null}
      <Handle type="source" position={Position.Right} id="out:max_visits" className="rs-port rs-port--visits" data-node={nodeId} />
    </div>
  );
}

function ModelChip({ data }: { data: StageData }) {
  if (!data.model && !data.backend) {
    return data.kind === "agent" ? <span className="rs-chip rs-chip--model is-unset" title="No model set: the backend's default model is used">default model</span> : null;
  }
  const source = data.model?.source;
  const title = data.model
    ? source === "stylesheet"
      ? `${data.model.value} — from stylesheet rule ${data.model.selector}`
      : source === "default"
        ? `${data.model.value} — from a node default block`
        : `${data.model.value} — set on this stage`
    : undefined;
  return (
    <>
      {data.model ? (
        <span className={cx("rs-chip rs-chip--model", `is-${source}`)} title={title}>
          {data.model.value}
          {data.effort ? <span className="rs-chip__suffix">{data.effort.value}</span> : null}
        </span>
      ) : null}
      {data.backend ? (
        <span className="rs-chip" title={`Routed to the ${data.backend} backend`}>
          via {data.backend}
        </span>
      ) : null}
    </>
  );
}

function Status({ data }: { data: StageData }) {
  const errors = data.diagnostics.filter((d) => d.severity === "error").length;
  const warnings = data.diagnostics.length - errors;
  const overlay = data.overlay;
  return (
    <span className="rs-node__status">
      {data.diagnostics.length > 0 ? (
        <span className={cx("rs-node__diag", errors > 0 ? "is-error" : "is-warning")} title={data.diagnostics.map((d) => `${d.severity}: ${d.message}`).join("\n")}>
          {errors > 0 ? errors : warnings}
        </span>
      ) : null}
      {overlay?.badge ? <span className="rs-node__badge num">{overlay.badge}</span> : null}
      {overlay?.state === "running" && overlay.startedAt ? <Elapsed since={overlay.startedAt} /> : null}
      {overlay?.state && overlay.state !== "idle" && overlay.state !== "dim" ? <span className={cx("rs-led", `is-${overlay.state}`)} /> : null}
    </span>
  );
}

function Question({ data }: { data: StageData }) {
  const question = data.overlay?.question;
  const [text, setText] = useState("");
  if (!question) return null;
  return (
    <div className="rs-node__question nodrag nowheel" onPointerDown={(e) => e.stopPropagation()}>
      <div className="rs-node__question-head">
        <span className="rs-led is-waiting" /> {question.type === "freeform" ? "Waiting for your input" : "Waiting for your decision"}
      </div>
      {question.text !== data.label ? <div className="rs-node__question-text">{question.text}</div> : null}
      {question.type === "freeform" ? (
        <div className="rs-node__question-free">
          <input value={text} placeholder="Type an answer…" onChange={(e) => setText(e.target.value)} onKeyDown={(e) => e.key === "Enter" && canvasActions.answer(question.id, undefined, text)} />
          <button type="button" onClick={() => canvasActions.answer(question.id, undefined, text)}>
            Send
          </button>
        </div>
      ) : (
        <div className="rs-node__question-options">
          {(question.options.length > 0 ? question.options : [{ key: "yes", label: "Yes" }, { key: "no", label: "No" }]).map((o) => (
            <button key={o.key} type="button" onClick={() => canvasActions.answer(question.id, o.key)}>
              <span className="rs-route__key">{o.key}</span>
              {o.label.replace(/^\s*\[[A-Za-z0-9]\]\s*/, "")}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

function StageNodeImpl({ data, selected }: NodeProps<Node<StageData>>) {
  const [hover, setHover] = useState(false);
  const updateNodeInternals = useUpdateNodeInternals();
  const draggingArtifact = useConnection((c) => (c.inProgress && c.fromHandle?.id?.startsWith("aout:") && c.fromHandle.nodeId !== data.id ? c.fromHandle.id.slice(5) : null));
  const meta = KINDS[data.kind];
  const terminal = data.kind === "start" || data.kind === "exit";
  const overlay = data.overlay;
  const state = overlay?.state;
  const showGhosts = (hover || selected) && !data.overlayKind;
  const routes = showGhosts ? [...data.routes, ...data.ghosts] : data.routes.length > 0 || data.kind === "exit" ? data.routes : data.ghosts.slice(0, 1).map((g) => ({ ...g, ghost: false }));
  const acceptsArtifact = !!draggingArtifact && meta.pins && !data.inputs.some((p) => p.name === draggingArtifact);
  const handleSignature = useMemo(
    () => [routes.map((r) => r.key).join("|"), data.inputs.map((p) => p.name).join("|"), data.outputs.map((p) => p.name).join("|"), acceptsArtifact, !!overlay?.question, !!data.reroute].join("#"),
    [routes, data.inputs, data.outputs, acceptsArtifact, overlay?.question, data.reroute],
  );
  useEffect(() => updateNodeInternals(data.id), [handleSignature, data.id, updateNodeInternals]);

  return (
    <div
      className={cx(
        "rs-node",
        `rs-node--${data.kind}`,
        terminal && "rs-node--terminal",
        selected && "is-selected",
        state && `is-${state}`,
        data.overlayKind && `has-overlay overlay-${data.overlayKind}`,
        data.diagnostics.some((d) => d.severity === "error") && "has-error",
      )}
      style={{ ["--accent" as string]: `var(--kind-${data.kind})`, ["--heat" as string]: overlay?.heat ?? 0 }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      onDoubleClick={() => canvasActions.openNode(data.id)}
    >
      {data.kind !== "start" ? <Handle type="target" position={Position.Left} id="in" className="rs-port rs-port--in" /> : null}
      <header className="rs-node__head">
        <span className="rs-node__glyph">
          <Glyph name={data.kind} size={14} />
        </span>
        <span className="rs-node__code">{meta.code}</span>
        {data.label !== data.id ? (
          <span className="rs-node__id" title={data.id}>
            {data.id}
          </span>
        ) : null}
        <Status data={data} />
      </header>

      <div className="rs-node__title">{data.label}</div>

      {data.kind === "agent" ? (
        data.prompt ? <p className="rs-node__prompt">{data.prompt}</p> : <p className="rs-node__prompt is-empty">No prompt — the label is the task.</p>
      ) : null}
      {data.kind === "join" && data.prompt ? <p className="rs-node__prompt">{data.prompt}</p> : null}
      {data.kind === "tool" ? (
        <div className={cx("rs-node__command mono", !data.command && "is-empty")}>
          <span className="rs-node__prompt-char">$</span>
          {data.command || "no command"}
        </div>
      ) : null}
      {data.kind === "exit" && data.gateCount ? (
        <div className="rs-node__gates">
          <Glyph name="flag" size={11} /> {data.gateCount} goal {data.gateCount === 1 ? "gate" : "gates"}
        </div>
      ) : null}

      {!terminal ? (
        <div className="rs-node__chips">
          <ModelChip data={data} />
          {data.thread ? (
            <span className="rs-chip" title={`Shares a conversation on thread “${data.thread.id}” (fidelity=full)`}>
              <span className="rs-dot" style={{ background: data.thread.color }} />
              {data.thread.id}
            </span>
          ) : data.fidelity && data.fidelity !== "compact" ? (
            <span className="rs-chip" title="Context fidelity">
              {data.fidelity}
            </span>
          ) : null}
          {data.goalGate ? (
            <span className="rs-chip rs-chip--gate" title="Goal gate: the run may only exit once this stage has succeeded">
              <Glyph name="flag" size={10} />
              gate
            </span>
          ) : null}
          {data.retries ? (
            <span className="rs-chip" title={`Up to ${data.retries} extra attempts`}>
              ↻{data.retries}
            </span>
          ) : null}
          {data.timeout ? (
            <span className="rs-chip" title="Stage time limit">
              ⏱{data.timeout}
            </span>
          ) : null}
          {data.maxVisits ? (
            <span
              className="rs-chip rs-chip--bound"
              title={`Loop bound: runs at most ${data.maxVisits} time${data.maxVisits === 1 ? "" : "s"} per run; then ${data.reroute?.targetLabel ? `the run goes to “${data.reroute.targetLabel}”` : "the run fails"} (max_visits)`}
            >
              ⟲≤{data.maxVisits}
            </span>
          ) : null}
          {data.details.map((d) => (
            <span key={d.label} className="rs-chip" title={`${d.label}: ${d.value}`}>
              <span className="rs-chip__key">{d.label}</span>
              {d.value}
            </span>
          ))}
        </div>
      ) : null}

      {data.inputs.length > 0 || data.outputs.length > 0 || acceptsArtifact ? (
        <div className="rs-node__pins">
          <div className="rs-pins rs-pins--in">
            {data.inputs.map((pin) => (
              <div key={pin.name} className={cx("rs-pin", pin.dangling && "is-dangling")} style={{ ["--pin" as string]: pin.color }} title={pin.dangling ? `No stage produces “${pin.name}”` : `Input: ${pin.name}`}>
                <Handle type="target" position={Position.Left} id={`ain:${pin.name}`} className="rs-port rs-port--pin" />
                <span className="rs-pin__name">{pin.name}</span>
              </div>
            ))}
            {acceptsArtifact ? (
              <div className="rs-pin is-ghost" style={{ ["--pin" as string]: "var(--signal)" }}>
                <Handle type="target" position={Position.Left} id={`ain:${draggingArtifact}`} className="rs-port rs-port--pin" />
                <span className="rs-pin__name">+ {draggingArtifact}</span>
              </div>
            ) : null}
          </div>
          <div className="rs-pins rs-pins--out">
            {data.outputs.map((pin) => (
              <div
                key={pin.name}
                className={cx("rs-pin", overlay?.produced?.includes(pin.name) && "is-produced")}
                style={{ ["--pin" as string]: pin.color }}
                title={pin.path ? `Output: ${pin.name} → ${pin.path}` : `Output: ${pin.name} (response text)`}
              >
                <span className="rs-pin__name">
                  {pin.name}
                  {pin.path ? <span className="rs-pin__path">{pin.path.split(/[\\/]/).pop()}</span> : null}
                </span>
                <Handle type="source" position={Position.Right} id={`aout:${pin.name}`} className="rs-port rs-port--pin" />
              </div>
            ))}
          </div>
        </div>
      ) : null}

      {routes.length > 0 || data.reroute ? (
        <div className="rs-node__routes">
          {routes.map((route) => (
            <RouteRow key={route.key + (route.ghost ? "~" : "")} route={route} nodeId={data.id} />
          ))}
          {data.reroute ? <RerouteRow reroute={data.reroute} nodeId={data.id} /> : null}
        </div>
      ) : null}

      {overlay?.readouts && overlay.readouts.length > 0 ? (
        <div className="rs-node__readouts">
          {overlay.readouts.map((r) => (
            <span key={r.label} className={cx("rs-readout", r.tone && `is-${r.tone}`)}>
              <span className="rs-readout__label">{r.label}</span>
              <span className="rs-readout__value num">{r.value}</span>
            </span>
          ))}
        </div>
      ) : null}

      {overlay?.heat !== undefined ? (
        <div className="rs-node__heat" title={overlay.heatLabel}>
          <span style={{ width: `${Math.round(Math.max(0.02, Math.min(1, overlay.heat)) * 100)}%` }} />
        </div>
      ) : null}

      {overlay?.lines && overlay.lines.length > 0 ? (
        <div className="rs-node__log mono">
          {overlay.lines.slice(-3).map((line, i) => (
            <div key={i} className={cx("rs-node__log-line", line.level && `is-${line.level}`)}>
              {line.text}
            </div>
          ))}
        </div>
      ) : null}

      <Question data={data} />
    </div>
  );
}

export const StageNode = memo(StageNodeImpl);
