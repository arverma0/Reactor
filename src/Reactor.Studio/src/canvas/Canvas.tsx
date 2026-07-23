import {
  Background,
  BackgroundVariant,
  MiniMap,
  ReactFlow,
  useReactFlow,
  useViewport,
  type Connection,
  type Edge,
  type EdgeChange,
  type FinalConnectionState,
  type IsValidConnection,
  type Node,
  type NodeChange,
  type NodeMouseHandler,
  type EdgeMouseHandler,
} from "@xyflow/react";
import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent } from "react";
import { createPortal } from "react-dom";
import { Button, Menu, cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { formatInputs, parseInputs } from "../model/artifacts";
import { addEdge, addNode, findNode, groupChain, handlerOf, kindOf, moveNodes, newNodeOfKind, removeGroup, setNodeAttr, setNodeGroup, newNoteId } from "../model/doc";
import { KINDS, LIBRARY_KINDS, type NodeKind } from "../model/handlers";
import { placeMissing, autoLayout, type Size } from "../model/layout";
import { attrsForRoute } from "../model/routes";
import type { Diagnostic, StudioDoc } from "../model/types";
import { applyDoc, emptySelection, select, toast, useStudio } from "../state/store";
import { canvasActions } from "./actions";
import { buildEdges, buildFrames, buildStageData, buildStageNode, frameAt, type Box, type CanvasOverlay, type NoteData } from "./build";
import { CableEdge, DataEdge } from "./edges";
import { FrameNode } from "./FrameNode";
import { NoteNode } from "./NoteNode";
import { StageNode } from "./StageNode";
import { insertPattern, PATTERNS } from "../model/patterns";
import { deleteSelection, duplicateSelection } from "../state/editing";

const nodeTypes = { stage: StageNode, frame: FrameNode, note: NoteNode };
const edgeTypes = { cable: CableEdge, data: DataEdge };

interface PendingConnection {
  source: string;
  handle: string;
  screen: { x: number; y: number };
  flow: { x: number; y: number };
}

interface ContextMenuState {
  x: number;
  y: number;
  kind: "node" | "edge" | "pane" | "frame";
  id?: string;
  flow?: { x: number; y: number };
}

function membersDeep(doc: StudioDoc, uid: string): Set<string> {
  const ids = new Set<string>();
  for (const node of doc.nodes) {
    if (node.group && groupChain(doc, node.group).some((g) => g.uid === uid)) ids.add(node.id);
  }
  return ids;
}

export interface CanvasProps {
  overlay: CanvasOverlay | null;
  readOnly?: boolean;
  /** A document to show instead of the design document (run and simulation views); implies read-only. */
  doc?: StudioDoc;
  /** Selection for read-only views (the design canvas uses the store's selection). */
  selectedNode?: string | null;
  onSelectNode?: (id: string | null) => void;
}

const EMPTY_DIAGNOSTICS: Diagnostic[] = [];
const EMPTY_EFFECTIVE = {};

export function Canvas({ overlay, readOnly: readOnlyProp = false, doc: docProp, selectedNode, onSelectNode }: CanvasProps) {
  const designDoc = useStudio((s) => s.doc);
  const designAnalysis = useStudio((s) => s.analysis);
  const designSelection = useStudio((s) => s.selection);
  const external = docProp !== undefined;
  const doc = docProp ?? designDoc;
  const readOnly = readOnlyProp || external;
  const diagnostics = external ? EMPTY_DIAGNOSTICS : designAnalysis.diagnostics;
  const effective = external ? EMPTY_EFFECTIVE : designAnalysis.effective;
  const selection = useMemo(
    () => (external ? { ...emptySelection, nodes: selectedNode ? [selectedNode] : [] } : designSelection),
    [external, selectedNode, designSelection],
  );
  const analysis = useMemo(() => ({ diagnostics, effective }), [diagnostics, effective]);
  const layers = useStudio((s) => s.layers);
  const fitRequest = useStudio((s) => s.fitRequest);
  const fitAllRequest = useStudio((s) => s.fitAllRequest);
  const layoutRequest = useStudio((s) => s.layoutRequest);
  const focusRequest = useStudio((s) => s.focusRequest);
  const rf = useReactFlow();
  const wrapper = useRef<HTMLDivElement>(null);

  const [nodes, setNodes] = useState<Node[]>([]);
  const nodesRef = useRef<Node[]>([]);
  const lastBoxes = useRef<Record<string, Box>>({});
  const frozen = useRef<Record<string, Box>>({});
  const [pending, setPending] = useState<PendingConnection | null>(null);
  const [menu, setMenu] = useState<ContextMenuState | null>(null);

  const commitNodes = useCallback((next: Node[]) => {
    nodesRef.current = next;
    setNodes(next);
  }, []);
  const smartFitRef = useRef<() => void>(() => undefined);

  const stageData = useMemo(() => buildStageData(doc, analysis.diagnostics, analysis.effective, overlay), [doc, analysis.diagnostics, analysis.effective, overlay]);

  // Document → canvas nodes (keeping measured sizes and in-flight drags).
  useEffect(() => {
    const previous = new Map(nodesRef.current.map((n) => [n.id, n]));
    const next: Node[] = [];
    for (const node of doc.nodes) {
      const data = stageData.get(node.id);
      if (!data) continue;
      const built = buildStageNode(data, doc.layout.nodes[node.id] ?? { x: 0, y: 0 }, previous.get(node.id));
      next.push({ ...built, selected: selection.nodes.includes(node.id), draggable: !readOnly, connectable: !readOnly, deletable: false });
    }
    if (layers.notes) {
      for (const note of doc.layout.notes) {
        const prev = previous.get(note.id);
        next.push({
          id: note.id,
          type: "note",
          position: prev?.dragging ? prev.position : { x: note.x, y: note.y },
          style: { width: note.w, height: note.h },
          width: note.w,
          height: note.h,
          data: { id: note.id, text: note.text, color: note.color } satisfies NoteData,
          selected: selection.note === note.id,
          measured: prev?.measured,
          dragging: prev?.dragging,
          draggable: !readOnly,
          deletable: false,
          zIndex: -2,
        });
      }
    }
    commitNodes(next);
  }, [doc, stageData, selection, layers.notes, readOnly, commitNodes]);

  // Safety net: place nodes that have no position (e.g. created by an edit that did not place them).
  useEffect(() => {
    if (external) return;
    const missing = placeMissing(doc, measuredSizes(nodesRef.current));
    if (missing) useStudio.setState({ doc: moveNodes(doc, missing) });
  }, [doc, external]);

  // A different document (another run) re-fits the view.
  const lastDocName = useRef<string | null>(null);
  useEffect(() => {
    if (!external) return;
    const key = `${doc.name}:${doc.nodes.length}`;
    if (lastDocName.current === key) return;
    lastDocName.current = key;
    const timer = setTimeout(smartFitRef.current, 120);
    return () => clearTimeout(timer);
  }, [doc, external]);

  const { frames, boxes } = useMemo(() => buildFrames(doc, nodes, frozen.current), [doc, nodes]);
  lastBoxes.current = boxes;

  const allNodes = useMemo(
    () => [...frames.map((f) => ({ ...f, selected: selection.group === f.data.uid, draggable: !readOnly })), ...nodes],
    [frames, nodes, selection.group, readOnly],
  );

  const positions = useMemo(() => Object.fromEntries(nodes.map((n) => [n.id, n.position])), [nodes]);
  const edges = useMemo(
    () =>
      buildEdges(doc, analysis.diagnostics, overlay, layers.data, positions).map((e) => ({
        ...e,
        selected: selection.edges.includes(e.id),
        deletable: false,
      })),
    [doc, analysis.diagnostics, overlay, layers.data, positions, selection.edges],
  );

  // ---------- view requests ----------
  /** Fits the whole graph when that keeps cards legible; otherwise opens at a readable zoom anchored on the start. */
  const smartFit = useCallback(() => {
    const stage = nodesRef.current.filter((n) => n.type === "stage");
    if (stage.length === 0) return;
    const bounds = wrapper.current?.getBoundingClientRect();
    if (!bounds) return;
    const xs = stage.map((n) => n.position.x);
    const ys = stage.map((n) => n.position.y);
    const width = Math.max(...stage.map((n) => n.position.x + (n.measured?.width ?? 284))) - Math.min(...xs);
    const height = Math.max(...stage.map((n) => n.position.y + (n.measured?.height ?? 160))) - Math.min(...ys);
    const fitZoom = Math.min(bounds.width / (width * 1.18), bounds.height / (height * 1.18));
    if (fitZoom >= 0.42) {
      rf.fitView({ padding: 0.18, duration: 380, maxZoom: 1.05 });
      return;
    }
    const zoom = 0.55;
    const start = stage.find((n) => (n.data as { kind?: string }).kind === "start") ?? stage.reduce((a, b) => (a.position.x <= b.position.x ? a : b));
    const centerY = Math.min(...ys) + height / 2;
    rf.setViewport({ x: -(start.position.x - 60) * zoom, y: bounds.height / 2 - centerY * zoom, zoom }, { duration: 380 });
  }, [rf]);
  smartFitRef.current = smartFit;

  useEffect(() => {
    if (fitRequest === 0) return;
    const timer = setTimeout(smartFit, 80);
    return () => clearTimeout(timer);
  }, [fitRequest, smartFit]);

  useEffect(() => {
    if (fitAllRequest === 0) return;
    rf.fitView({ padding: 0.12, duration: 380, maxZoom: 1.1 });
  }, [fitAllRequest, rf]);

  useEffect(() => {
    if (layoutRequest === 0) return;
    const positions = autoLayout(useStudio.getState().doc, measuredSizes(nodesRef.current));
    applyDoc((d) => moveNodes(d, positions));
    const timer = setTimeout(() => rf.fitView({ padding: 0.18, duration: 420, maxZoom: 1.1 }), 80);
    return () => clearTimeout(timer);
  }, [layoutRequest, rf]);

  useEffect(() => {
    if (!focusRequest) return;
    const node = nodesRef.current.find((n) => n.id === focusRequest.id);
    if (!node) return;
    const w = node.measured?.width ?? 260;
    const h = node.measured?.height ?? 140;
    rf.setCenter(node.position.x + w / 2, node.position.y + h / 2, { zoom: Math.max(rf.getZoom(), 0.95), duration: 420 });
  }, [focusRequest, rf]);

  // ---------- changes ----------
  const onNodesChange = useCallback(
    (changes: NodeChange[]) => {
      if (external) {
        let working = nodesRef.current;
        for (const change of changes) {
          if (change.type === "dimensions") working = working.map((n) => (n.id === change.id ? { ...n, measured: change.dimensions ?? n.measured } : n));
          else if (change.type === "select" && change.selected && !change.id.startsWith("frame:")) onSelectNode?.(change.id);
        }
        if (working !== nodesRef.current) commitNodes(working);
        return;
      }
      const state = useStudio.getState();
      const current = state.doc;
      let nextSelection = state.selection;
      let selectionChanged = false;
      let working = nodesRef.current;
      const committed: Record<string, { x: number; y: number }> = {};
      const membership: Record<string, string | null> = {};
      const noteUpdates: Record<string, { x?: number; y?: number; w?: number; h?: number }> = {};

      for (const change of changes) {
        if (change.type === "select") {
          const id = change.id;
          selectionChanged = true;
          if (id.startsWith("frame:")) {
            const uid = id.slice(6);
            nextSelection = change.selected ? { ...emptySelection, group: uid } : nextSelection.group === uid ? { ...nextSelection, group: null } : nextSelection;
          } else if (current.layout.notes.some((n) => n.id === id)) {
            nextSelection = change.selected ? { ...nextSelection, note: id, group: null } : nextSelection.note === id ? { ...nextSelection, note: null } : nextSelection;
          } else {
            const set = new Set(nextSelection.nodes);
            if (change.selected) set.add(id);
            else set.delete(id);
            nextSelection = { ...nextSelection, nodes: [...set], group: change.selected ? null : nextSelection.group };
          }
          continue;
        }
        if (change.type === "position" && change.id.startsWith("frame:")) {
          if (readOnly || !change.position) continue;
          const uid = change.id.slice(6);
          const box = lastBoxes.current[uid];
          if (!box) continue;
          const dx = change.position.x - box.x;
          const dy = change.position.y - box.y;
          const members = membersDeep(current, uid);
          if (dx !== 0 || dy !== 0) {
            working = working.map((n) => (members.has(n.id) ? { ...n, position: { x: n.position.x + dx, y: n.position.y + dy } } : n));
          }
          if (change.dragging === false) {
            for (const n of working) if (members.has(n.id)) committed[n.id] = { x: Math.round(n.position.x), y: Math.round(n.position.y) };
          }
          continue;
        }
        if (change.type === "position") {
          working = working.map((n) => (n.id === change.id ? { ...n, position: change.position ?? n.position, dragging: change.dragging } : n));
          if (change.dragging === false) {
            const moved = working.find((n) => n.id === change.id);
            if (!moved) continue;
            if (moved.type === "note") {
              noteUpdates[moved.id] = { ...noteUpdates[moved.id], x: Math.round(moved.position.x), y: Math.round(moved.position.y) };
              continue;
            }
            committed[moved.id] = { x: Math.round(moved.position.x), y: Math.round(moved.position.y) };
            const w = moved.measured?.width ?? 260;
            const h = moved.measured?.height ?? 140;
            const docNode = findNode(current, moved.id);
            const target = frameAt(current, lastBoxes.current, moved.position.x + w / 2, moved.position.y + Math.min(h / 2, 40));
            if (docNode && target !== docNode.group) membership[moved.id] = target;
          }
          continue;
        }
        if (change.type === "dimensions") {
          working = working.map((n) => (n.id === change.id ? { ...n, measured: change.dimensions ?? n.measured } : n));
          const node = working.find((n) => n.id === change.id);
          if (node?.type === "note" && change.resizing === false && change.dimensions) {
            noteUpdates[node.id] = { ...noteUpdates[node.id], w: Math.round(change.dimensions.width), h: Math.round(change.dimensions.height), x: Math.round(node.position.x), y: Math.round(node.position.y) };
          }
          continue;
        }
      }

      if (working !== nodesRef.current) commitNodes(working);
      if (selectionChanged) select(nextSelection);

      const moved = Object.keys(committed).length > 0;
      const regrouped = Object.keys(membership).length > 0;
      const notes = Object.keys(noteUpdates).length > 0;
      if (moved || regrouped || notes) {
        frozen.current = {};
        applyDoc((d) => {
          let next = moved ? moveNodes(d, committed) : d;
          for (const [id, group] of Object.entries(membership)) next = setNodeGroup(next, [id], group);
          if (notes) {
            next = { ...next, layout: { ...next.layout, notes: next.layout.notes.map((n) => (noteUpdates[n.id] ? { ...n, ...Object.fromEntries(Object.entries(noteUpdates[n.id]).filter(([, v]) => v !== undefined)) } : n)) } };
          }
          return next;
        });
        if (regrouped) {
          const joined = Object.values(membership).filter(Boolean).length;
          toast(joined > 0 ? "Moved into the group — it now shares the group's class." : "Moved out of the group.", "info", undefined, 2200);
        }
      }
    },
    [commitNodes, readOnly, external, onSelectNode],
  );

  const onEdgesChange = useCallback((changes: EdgeChange[]) => {
    if (external) return;
    let next = useStudio.getState().selection;
    let changed = false;
    for (const change of changes) {
      if (change.type !== "select") continue;
      if (change.id.startsWith("data:")) continue;
      changed = true;
      const set = new Set(next.edges);
      if (change.selected) set.add(change.id);
      else set.delete(change.id);
      next = { ...next, edges: [...set], group: null };
    }
    if (changed) select(next);
  }, [external]);

  const onNodeDragStart = useCallback(() => {
    frozen.current = { ...lastBoxes.current };
  }, []);

  // ---------- connections ----------
  const isValidConnection: IsValidConnection = useCallback((connection) => {
    const d = useStudio.getState().doc;
    const sourceHandle = connection.sourceHandle ?? "";
    const targetHandle = connection.targetHandle ?? "";
    const source = findNode(d, connection.source);
    const target = findNode(d, connection.target);
    if (!source || !target) return false;
    if (sourceHandle.startsWith("out:")) {
      return targetHandle === "in" && handlerOf(d, target) !== "start";
    }
    if (sourceHandle.startsWith("aout:")) {
      return targetHandle.startsWith("ain:") && source.id !== target.id && KINDS[kindOf(d, target)].pins;
    }
    return false;
  }, []);

  const connect = useCallback((source: string, handle: string, target: string) => {
    const d = useStudio.getState().doc;
    const sourceNode = findNode(d, source);
    const targetNode = findNode(d, target);
    if (!sourceNode || !targetNode) return;
    if (handle.startsWith("aout:")) {
      const name = handle.slice(5);
      const inputs = parseInputs(targetNode.attrs.inputs);
      if (inputs.includes(name)) return;
      applyDoc((x) => setNodeAttr(x, target, "inputs", formatInputs([...inputs, name])), { select: { nodes: [target] } });
      toast(`“${target}” now reads ${name}.`, "info", undefined, 2000);
      return;
    }
    const routeKey = handle.slice(4);
    const existing = d.edges.filter((e) => e.from === source);
    const attrs = attrsForRoute(d, sourceNode, routeKey, targetNode, existing);
    let created = "";
    applyDoc((x) => {
      const result = addEdge(x, source, target, attrs);
      created = result.edge.uid;
      return result.doc;
    });
    if (routeKey === "choice:+" || routeKey === "cond:+") select({ edges: [created] });
  }, []);

  const onConnect = useCallback((connection: Connection) => connect(connection.source, connection.sourceHandle ?? "out:next", connection.target), [connect]);

  const onConnectEnd = useCallback(
    (event: MouseEvent | TouchEvent, state: FinalConnectionState) => {
      if (readOnly || state.isValid || !state.fromHandle || !state.fromNode) return;
      const handle = state.fromHandle.id ?? "";
      if (!handle.startsWith("out:")) return;
      const target = (event as MouseEvent).target as HTMLElement | null;
      if (!target?.classList.contains("react-flow__pane")) return;
      const { clientX, clientY } = "changedTouches" in event ? event.changedTouches[0] : (event as MouseEvent);
      setPending({ source: state.fromNode.id, handle, screen: { x: clientX, y: clientY }, flow: rf.screenToFlowPosition({ x: clientX, y: clientY }) });
    },
    [readOnly, rf],
  );

  const addConnectedNode = (kind: NodeKind) => {
    if (!pending) return;
    let created = "";
    applyDoc((d) => {
      const node = newNodeOfKind(d, kind);
      created = node.id;
      const placed = addNode(d, node, { x: Math.round(pending.flow.x), y: Math.round(pending.flow.y - 30) });
      const source = findNode(placed, pending.source)!;
      const attrs = attrsForRoute(placed, source, pending.handle.slice(4), node, placed.edges.filter((e) => e.from === pending.source));
      return addEdge(placed, pending.source, node.id, attrs).doc;
    });
    setPending(null);
    select({ nodes: [created] });
  };

  // ---------- drag & drop from the library ----------
  const onDrop = (event: React.DragEvent) => {
    event.preventDefault();
    if (readOnly) return;
    const position = rf.screenToFlowPosition({ x: event.clientX, y: event.clientY });
    const kind = event.dataTransfer.getData("application/x-reactor-kind") as NodeKind;
    const pattern = event.dataTransfer.getData("application/x-reactor-pattern");
    if (kind && KINDS[kind]) {
      let created = "";
      applyDoc((d) => {
        const node = newNodeOfKind(d, kind);
        created = node.id;
        return addNode(d, node, { x: Math.round(position.x - 130), y: Math.round(position.y - 40) });
      });
      select({ nodes: [created] });
    } else if (pattern) {
      const spec = PATTERNS.find((p) => p.id === pattern);
      if (spec) {
        let ids: string[] = [];
        applyDoc((d) => {
          const result = insertPattern(d, spec, position);
          ids = result.ids;
          return result.doc;
        });
        select({ nodes: ids });
        toast(`Inserted “${spec.name}”. Connect it into your flow.`, "info", undefined, 2600);
      }
    }
  };

  // ---------- context menus ----------
  const onNodeContextMenu: NodeMouseHandler = (event, node) => {
    event.preventDefault();
    if (node.type === "frame") {
      setMenu({ x: event.clientX, y: event.clientY, kind: "frame", id: (node.data as { uid: string }).uid });
      return;
    }
    if (node.type === "stage" && !selection.nodes.includes(node.id)) select({ nodes: [node.id] });
    setMenu({ x: event.clientX, y: event.clientY, kind: "node", id: node.id });
  };
  const onEdgeContextMenu: EdgeMouseHandler = (event, edge) => {
    event.preventDefault();
    if (edge.type === "cable") select({ edges: [edge.id] });
    setMenu({ x: event.clientX, y: event.clientY, kind: "edge", id: edge.id });
  };
  const onPaneContextMenu = (event: ReactMouseEvent | MouseEvent) => {
    event.preventDefault();
    setMenu({ x: event.clientX, y: event.clientY, kind: "pane", flow: rf.screenToFlowPosition({ x: event.clientX, y: event.clientY }) });
  };

  useEffect(() => {
    canvasActions.editNote = (id, text) => applyDoc((d) => ({ ...d, layout: { ...d.layout, notes: d.layout.notes.map((n) => (n.id === id ? { ...n, text } : n)) } }));
    canvasActions.openNode = (id) => {
      select({ nodes: [id] });
      useStudio.setState({ inspectorOpen: true });
    };
  }, []);

  const menuItems = (): Parameters<typeof Menu>[0]["items"] => {
    if (!menu) return [];
    if (menu.kind === "pane") {
      const at = menu.flow!;
      return [
        ...LIBRARY_KINDS.filter((k) => k !== "start" && k !== "exit").map((kind) => ({
          label: `Add ${KINDS[kind].name.toLowerCase()}`,
          icon: kind,
          hint: KINDS[kind].hotkey,
          run: () => {
            let created = "";
            applyDoc((d) => {
              const node = newNodeOfKind(d, kind);
              created = node.id;
              return addNode(d, node, { x: Math.round(at.x), y: Math.round(at.y) });
            });
            select({ nodes: [created] });
          },
        })),
        "divider" as const,
        {
          label: "Add note",
          icon: "note",
          run: () => {
            const id = newNoteId();
            applyDoc((d) => ({ ...d, layout: { ...d.layout, notes: [...d.layout.notes, { id, x: Math.round(at.x), y: Math.round(at.y), w: 240, h: 120, text: "" }] } }));
          },
        },
        { label: "Auto-arrange", icon: "layout", hint: "L", run: () => useStudio.setState({ layoutRequest: useStudio.getState().layoutRequest + 1 }) },
        { label: "Fit to screen", icon: "fit", hint: "F", run: () => rf.fitView({ padding: 0.18, duration: 380, maxZoom: 1.1 }) },
      ];
    }
    if (menu.kind === "frame") {
      return [
        { label: "Select group", icon: "group", run: () => select({ group: menu.id! }) },
        { label: "Select members", icon: "outline", run: () => select({ nodes: [...membersDeep(useStudio.getState().doc, menu.id!)] }) },
        "divider" as const,
        {
          label: "Ungroup",
          icon: "close",
          run: () => applyDoc((d) => removeGroup(d, menu.id!)),
        },
      ];
    }
    if (menu.kind === "edge") {
      return [{ label: "Delete connection", icon: "trash", danger: true, hint: "Del", run: () => deleteSelection() }];
    }
    return [
      { label: "Inspect", icon: "sliders", run: () => useStudio.setState({ inspectorOpen: true }) },
      { label: "Duplicate", icon: "copy", hint: "Ctrl D", run: () => duplicateSelection() },
      "divider" as const,
      { label: "Delete", icon: "trash", danger: true, hint: "Del", run: () => deleteSelection() },
    ];
  };

  return (
    <div className={cx("rs-canvas", overlay && `has-overlay overlay-${overlay.kind}`)} ref={wrapper} onDragOver={(e) => e.preventDefault()} onDrop={onDrop}>
      <ZoomClass target={wrapper} />
      <ReactFlow
        nodes={allNodes}
        edges={edges as Edge[]}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onNodeDragStart={onNodeDragStart}
        onConnect={onConnect}
        onConnectEnd={onConnectEnd}
        isValidConnection={isValidConnection}
        onNodeContextMenu={external ? undefined : onNodeContextMenu}
        onEdgeContextMenu={external ? undefined : onEdgeContextMenu}
        onPaneContextMenu={external ? undefined : onPaneContextMenu}
        onPaneClick={() => {
          setPending(null);
          if (external) onSelectNode?.(null);
          else select(emptySelection);
        }}
        deleteKeyCode={null}
        snapToGrid
        snapGrid={[8, 8]}
        minZoom={0.12}
        maxZoom={2.2}
        onInit={() => setTimeout(smartFit, 120)}
        proOptions={{ hideAttribution: true }}
        nodesConnectable={!readOnly}
        elevateEdgesOnSelect
        connectionRadius={28}
        defaultEdgeOptions={{ type: "cable" }}
        connectionLineStyle={{ stroke: "var(--signal)", strokeWidth: 2 }}
        colorMode="dark"
      >
        <Background id="minor" variant={BackgroundVariant.Dots} gap={24} size={1.15} color="rgba(255,255,255,0.075)" />
        <Background id="major" variant={BackgroundVariant.Lines} gap={120} lineWidth={1} color="rgba(255,255,255,0.022)" />
        {layers.minimap ? (
          <MiniMap
            pannable
            zoomable
            className="rs-minimap"
            maskColor="rgba(8,9,10,0.72)"
            nodeColor={(n) => (n.type === "frame" ? "rgba(255,255,255,0.03)" : n.type === "note" ? "rgba(232,184,74,0.25)" : `var(--kind-${(n.data as { kind?: string }).kind ?? "agent"})`)}
            nodeStrokeWidth={0}
            nodeBorderRadius={3}
          />
        ) : null}
        <CanvasControls readOnly={readOnly} />
      </ReactFlow>
      {doc.nodes.length === 0 && !external ? <CanvasEmpty /> : null}
      {pending
        ? createPortal(
            <div className="rs-quickadd" style={{ left: pending.screen.x, top: pending.screen.y }}>
              <div className="rs-quickadd__head label">Connect to a new…</div>
              <Menu
                items={LIBRARY_KINDS.filter((k) => k !== "start").map((kind) => ({ label: KINDS[kind].name, icon: kind, hint: KINDS[kind].hotkey, run: () => addConnectedNode(kind) }))}
                onClose={() => setPending(null)}
              />
            </div>,
            document.body,
          )
        : null}
      {menu ? <ContextMenu x={menu.x} y={menu.y} items={menuItems()} onClose={() => setMenu(null)} /> : null}
    </div>
  );
}

function measuredSizes(nodes: Node[]): Record<string, Size> {
  const sizes: Record<string, Size> = {};
  for (const n of nodes) if (n.type === "stage" && n.measured?.width && n.measured?.height) sizes[n.id] = { width: n.measured.width, height: n.measured.height };
  return sizes;
}

/**
 * Semantic zoom: exposes the zoom as a CSS variable and a coarse class so cards can trade detail for legibility when the
 * whole factory is in view.
 */
function ZoomClass({ target }: { target: React.RefObject<HTMLDivElement | null> }) {
  const { zoom } = useViewport();
  useEffect(() => {
    const el = target.current;
    if (!el) return;
    el.style.setProperty("--zoom", String(zoom));
    el.classList.toggle("zoom-far", zoom < 0.5);
    el.classList.toggle("zoom-mid", zoom >= 0.5 && zoom < 0.75);
  }, [zoom, target]);
  return null;
}

function ContextMenu({ x, y, items, onClose }: { x: number; y: number; items: Parameters<typeof Menu>[0]["items"]; onClose: () => void }) {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState({ x, y });
  useEffect(() => {
    const el = ref.current;
    if (el) setPos({ x: Math.min(x, window.innerWidth - el.offsetWidth - 8), y: Math.min(y, window.innerHeight - el.offsetHeight - 8) });
    const close = (e: MouseEvent) => !ref.current?.contains(e.target as HTMLElement) && onClose();
    const key = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("mousedown", close, true);
    window.addEventListener("keydown", key);
    return () => {
      window.removeEventListener("mousedown", close, true);
      window.removeEventListener("keydown", key);
    };
  }, [x, y, onClose]);
  return createPortal(
    <div ref={ref} className="rs-context" style={{ left: pos.x, top: pos.y }}>
      <Menu items={items} onClose={onClose} />
    </div>,
    document.body,
  );
}

function CanvasControls({ readOnly }: { readOnly: boolean }) {
  const rf = useReactFlow();
  const { zoom } = useViewport();
  const layers = useStudio((s) => s.layers);
  const toggle = (key: keyof typeof layers) => useStudio.setState({ layers: { ...layers, [key]: !layers[key] } });
  return (
    <div className="rs-canvas-controls">
      <Button variant="ghost" size="sm" icon="minus" title="Zoom out" onClick={() => rf.zoomOut({ duration: 160 })} />
      <button type="button" className="rs-zoom num" title="Reset zoom to 100%" onClick={() => rf.zoomTo(1, { duration: 200 })}>
        {Math.round(zoom * 100)}%
      </button>
      <Button variant="ghost" size="sm" icon="plus" title="Zoom in" onClick={() => rf.zoomIn({ duration: 160 })} />
      <span className="rs-canvas-controls__sep" />
      <Button variant="ghost" size="sm" icon="fit" title="Fit to screen (F)" onClick={() => rf.fitView({ padding: 0.12, duration: 380, maxZoom: 1.1 })} />
      {!readOnly ? <Button variant="ghost" size="sm" icon="layout" title="Auto-arrange (L)" onClick={() => useStudio.setState({ layoutRequest: useStudio.getState().layoutRequest + 1 })} /> : null}
      <span className="rs-canvas-controls__sep" />
      <Button variant="ghost" size="sm" icon="data" title="Show artifact wires" active={layers.data} onClick={() => toggle("data")} />
      <Button variant="ghost" size="sm" icon="note" title="Show notes" active={layers.notes} onClick={() => toggle("notes")} />
      <Button variant="ghost" size="sm" icon="library" title="Show minimap" active={layers.minimap} onClick={() => toggle("minimap")} />
    </div>
  );
}

function CanvasEmpty() {
  return (
    <div className="rs-canvas-empty">
      <div className="rs-canvas-empty__title serif">An empty floor.</div>
      <div className="rs-canvas-empty__body">
        Drag stages from the library, describe the factory to the <span className="mono">Architect</span>, or open a pipeline from Files.
      </div>
      <div className="rs-canvas-empty__keys">
        <span>
          <Glyph name="agent" size={12} /> <kbd className="rs-kbd">A</kbd> agent
        </span>
        <span>
          <Glyph name="tool" size={12} /> <kbd className="rs-kbd">T</kbd> tool
        </span>
        <span>
          <Glyph name="human" size={12} /> <kbd className="rs-kbd">H</kbd> human
        </span>
        <span>
          <kbd className="rs-kbd">Ctrl</kbd> <kbd className="rs-kbd">K</kbd> commands
        </span>
      </div>
    </div>
  );
}
