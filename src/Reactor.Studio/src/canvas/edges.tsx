import { BaseEdge, EdgeLabelRenderer, useInternalNode, type Edge, type EdgeProps } from "@xyflow/react";
import { memo } from "react";
import { cx } from "../components/controls";
import type { CableData, DataEdgeData, RerouteData } from "./build";

interface Geometry {
  path: string;
  labelX: number;
  labelY: number;
}

function forwardPath(sx: number, sy: number, tx: number, ty: number): Geometry {
  const dx = Math.max(36, Math.abs(tx - sx) * 0.42);
  const path = `M ${sx},${sy} C ${sx + dx},${sy} ${tx - dx},${ty} ${tx},${ty}`;
  // Midpoint of a cubic Bézier at t = 0.5.
  const labelX = 0.125 * sx + 0.375 * (sx + dx) + 0.375 * (tx - dx) + 0.125 * tx;
  const labelY = 0.5 * sy + 0.5 * ty;
  return { path, labelX, labelY };
}

/** Back edges run underneath the cards: out to the right, down, back left, and up into the target from its left. */
function backPath(sx: number, sy: number, tx: number, ty: number, bottom: number): Geometry {
  const r = 14;
  const outX = sx + 26;
  const inX = tx - 26;
  const path = [
    `M ${sx},${sy}`,
    `H ${outX - r}`,
    `Q ${outX},${sy} ${outX},${sy + r}`,
    `V ${bottom - r}`,
    `Q ${outX},${bottom} ${outX - r},${bottom}`,
    `H ${inX + r}`,
    `Q ${inX},${bottom} ${inX},${bottom - r}`,
    `V ${ty + r}`,
    `Q ${inX},${ty} ${inX + r},${ty}`,
    `H ${tx}`,
  ].join(" ");
  return { path, labelX: (outX + inX) / 2, labelY: bottom };
}

function useGeometry(source: string, target: string, sx: number, sy: number, tx: number, ty: number, lane: number): Geometry & { back: boolean } {
  const sourceNode = useInternalNode(source);
  const targetNode = useInternalNode(target);
  const back = tx < sx + 24;
  if (!back) return { ...forwardPath(sx, sy, tx, ty), back };
  const bottomOf = (n: typeof sourceNode) => (n ? n.internals.positionAbsolute.y + (n.measured.height ?? 120) : Math.max(sy, ty));
  const bottom = Math.max(bottomOf(sourceNode), bottomOf(targetNode)) + 30 + (lane % 6) * 12;
  return { ...backPath(sx, sy, tx, ty, bottom), back };
}

function CableEdgeImpl({ id, source, target, sourceX, sourceY, targetX, targetY, data, selected }: EdgeProps<Edge<CableData>>) {
  const geometry = useGeometry(source, target, sourceX, sourceY, targetX, targetY, data?.lane ?? 0);
  if (!data) return null;
  const overlay = data.overlay;
  const flowWidth = overlay?.flow !== undefined ? 1.25 + Math.sqrt(Math.max(0, overlay.flow)) * 7 : undefined;
  const chip =
    data.routeKind === "label" ? data.label
    : data.routeKind === "cond" ? data.conditionText
    : undefined;
  const hasDiagnostics = data.diagnostics.length > 0;
  return (
    <>
      <BaseEdge
        id={id}
        path={geometry.path}
        interactionWidth={18}
        className={cx(
          "rs-cable",
          `rs-cable--${data.routeKind}`,
          selected && "is-selected",
          overlay?.state && `is-${overlay.state}`,
          data.overlayKind && `overlay-${data.overlayKind}`,
          data.overlayKind && !overlay && "is-untraveled",
          data.loopRestart && "is-restart",
        )}
        style={flowWidth ? { strokeWidth: flowWidth } : undefined}
      />
      {overlay?.pulse ? (
        <circle key={overlay.pulse} r={4} className="rs-comet">
          <animateMotion dur="0.85s" path={geometry.path} fill="freeze" calcMode="spline" keyTimes="0;1" keySplines="0.3 0 0.2 1" />
        </circle>
      ) : null}
      {chip || data.loopRestart || overlay?.text || hasDiagnostics || (data.weight && data.weight !== "0") ? (
        <EdgeLabelRenderer>
          <div
            className={cx("rs-edge-chip", `rs-edge-chip--${data.routeKind}`, selected && "is-selected", hasDiagnostics && "has-error", overlay?.state && `is-${overlay.state}`)}
            style={{ transform: `translate(-50%, -50%) translate(${geometry.labelX}px, ${geometry.labelY}px)` }}
          >
            {hasDiagnostics ? <span className="rs-edge-chip__diag" title={data.diagnostics.map((d) => d.message).join("\n")} /> : null}
            {chip ? <span className="rs-edge-chip__text">{data.routeKind === "cond" ? <span className="rs-edge-chip__fn">ƒ</span> : null}{chip}</span> : null}
            {data.loopRestart ? <span className="rs-edge-chip__tag">restart</span> : null}
            {data.weight && data.weight !== "0" ? <span className="rs-edge-chip__tag">w{data.weight}</span> : null}
            {overlay?.text ? <span className="rs-edge-chip__metric num">{overlay.text}</span> : null}
          </div>
        </EdgeLabelRenderer>
      ) : null}
    </>
  );
}

function DataEdgeImpl({ id, source, target, sourceX, sourceY, targetX, targetY, data, selected }: EdgeProps<Edge<DataEdgeData>>) {
  const geometry = useGeometry(source, target, sourceX, sourceY, targetX, targetY, 3);
  if (!data) return null;
  return (
    <BaseEdge
      id={id}
      path={geometry.path}
      interactionWidth={12}
      className={cx("rs-wire", selected && "is-selected", data.active && "is-active")}
      style={{ stroke: data.color, ["--wire" as string]: data.color }}
    />
  );
}

/**
 * A stage's visit-limit reroute (`on_max_visits`): not a route the stage picks, but where the run goes instead once the
 * stage has run `max_visits` times — drawn dashed, from the stage's "after N visits" port.
 */
function RerouteEdgeImpl({ id, source, target, sourceX, sourceY, targetX, targetY, data, selected }: EdgeProps<Edge<RerouteData>>) {
  const geometry = useGeometry(source, target, sourceX, sourceY, targetX, targetY, data?.lane ?? 0);
  if (!data) return null;
  const overlay = data.overlay;
  const flowWidth = overlay?.flow !== undefined ? 1.25 + Math.sqrt(Math.max(0, overlay.flow)) * 7 : undefined;
  return (
    <>
      <BaseEdge
        id={id}
        path={geometry.path}
        interactionWidth={18}
        className={cx(
          "rs-cable rs-cable--reroute",
          selected && "is-selected",
          overlay?.state && `is-${overlay.state}`,
          data.overlayKind && `overlay-${data.overlayKind}`,
          data.overlayKind && !overlay && "is-untraveled",
        )}
        style={flowWidth ? { strokeWidth: flowWidth } : undefined}
      />
      {overlay?.pulse ? (
        <circle key={overlay.pulse} r={4} className="rs-comet">
          <animateMotion dur="0.85s" path={geometry.path} fill="freeze" calcMode="spline" keyTimes="0;1" keySplines="0.3 0 0.2 1" />
        </circle>
      ) : null}
      <EdgeLabelRenderer>
        <div
          className={cx("rs-edge-chip rs-edge-chip--reroute", selected && "is-selected", overlay?.state && `is-${overlay.state}`)}
          style={{ transform: `translate(-50%, -50%) translate(${geometry.labelX}px, ${geometry.labelY}px)` }}
          title={`After ${data.max} visit${data.max === 1 ? "" : "s"} of ${data.node}, the run goes here instead (max_visits / on_max_visits)`}
        >
          <span className="rs-edge-chip__text">⟲ after {data.max} visit{data.max === 1 ? "" : "s"}</span>
          {overlay?.text ? <span className="rs-edge-chip__metric num">{overlay.text}</span> : null}
        </div>
      </EdgeLabelRenderer>
    </>
  );
}

export const CableEdge = memo(CableEdgeImpl);
export const DataEdge = memo(DataEdgeImpl);
export const RerouteEdge = memo(RerouteEdgeImpl);
