import { ReactFlowProvider, useReactFlow } from "@xyflow/react";
import { useEffect, useMemo, useState } from "react";
import { setToken } from "./api/client";
import { Canvas } from "./canvas/Canvas";
import { CommandPalette, saveOrAsk } from "./components/CommandPalette";
import { Button, Dialog, Field, Kbd, TextInput, cx } from "./components/controls";
import { Glyph, Mark } from "./components/Glyph";
import type { NodeKind } from "./model/handlers";
import { Problems, PromptPreview } from "./panels/Bottom";
import { CodeView } from "./panels/CodeView";
import { Files, SaveAsDialog } from "./panels/Files";
import { Inspector } from "./panels/Inspector";
import { Library } from "./panels/Library";
import { ModelsPanel } from "./panels/Models";
import { Outline } from "./panels/Outline";
import { addNodeOfKind, addNote, copySelection, deleteSelection, duplicateSelection, groupSelection, pasteClipboard, selectAll, setCenterOfView } from "./state/editing";
import { newDocument, openFile } from "./state/files";
import { loadInfo, startSession } from "./state/session";
import { dismissToast, emptySelection, redo, requestFit, requestLayout, select, setBottomPanel, setLeftPanel, setMode, undo, useStudio, type LeftPanel, type Mode } from "./state/store";
import { loadTelemetry, telemetryOverlay, useTelemetry } from "./state/telemetry";
import { ModeStage } from "./modes/ModeStage";
import { RunDialog } from "./modes/RunDialog";

const HOTKEYS: Record<string, NodeKind> = { a: "agent", t: "tool", h: "human", r: "router", p: "fanout", j: "join", s: "supervisor" };

function isTyping(target: EventTarget | null) {
  const el = target as HTMLElement | null;
  return !!el && (el.tagName === "INPUT" || el.tagName === "TEXTAREA" || el.tagName === "SELECT" || el.isContentEditable || !!el.closest(".cm-editor"));
}

function useKeyboard() {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const mod = e.ctrlKey || e.metaKey;
      const key = e.key.toLowerCase();
      if (mod && key === "k") {
        e.preventDefault();
        useStudio.setState({ paletteOpen: !useStudio.getState().paletteOpen });
        return;
      }
      if (mod && key === "s") {
        e.preventDefault();
        void saveOrAsk();
        return;
      }
      if (isTyping(e.target)) return;
      const mode = useStudio.getState().mode;
      if (mod && key === "z") {
        e.preventDefault();
        if (e.shiftKey) redo();
        else undo();
        return;
      }
      if (mod && key === "y") {
        e.preventDefault();
        redo();
        return;
      }
      if (mod && key === "`") {
        e.preventDefault();
        setBottomPanel("code");
        return;
      }
      if (mode !== "design") {
        if (!mod && (key === "1" || key === "2" || key === "3")) setMode((["design", "tunnel", "flights"] as Mode[])[Number(key) - 1]);
        if (key === "f") requestFit();
        return;
      }
      if (mod && key === "c") {
        if (copySelection()) e.preventDefault();
        return;
      }
      if (mod && key === "v") {
        pasteClipboard();
        return;
      }
      if (mod && key === "d") {
        e.preventDefault();
        duplicateSelection();
        return;
      }
      if (mod && key === "a") {
        e.preventDefault();
        selectAll();
        return;
      }
      if (mod && key === "g") {
        e.preventDefault();
        groupSelection();
        return;
      }
      if (mod || e.altKey) return;
      if (key === "delete" || key === "backspace") {
        e.preventDefault();
        deleteSelection();
      } else if (key === "escape") {
        select(emptySelection);
      } else if (key === "l") requestLayout();
      else if (key === "f") requestFit();
      else if (key === "n") addNote();
      else if (key === "1" || key === "2" || key === "3") setMode((["design", "tunnel", "flights"] as Mode[])[Number(key) - 1]);
      else if (HOTKEYS[key]) addNodeOfKind(HOTKEYS[key]);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);
}

const MODES: { value: Mode; label: string; icon: string; hint: string }[] = [
  { value: "design", label: "Design", icon: "design", hint: "Build the factory" },
  { value: "tunnel", label: "Wind tunnel", icon: "tunnel", hint: "Simulate routing at zero token cost" },
  { value: "flights", label: "Flights", icon: "flights", hint: "Run, watch, replay, and measure" },
];

function TopBar() {
  const mode = useStudio((s) => s.mode);
  const file = useStudio((s) => s.file);
  const dirty = useStudio((s) => s.dirty);
  const doc = useStudio((s) => s.doc);
  const analysis = useStudio((s) => s.analysis);
  const connected = useStudio((s) => s.connected);
  const info = useStudio((s) => s.info);
  const errors = analysis.diagnostics.filter((d) => d.severity === "error").length + (analysis.parseError ? 1 : 0);
  const warnings = analysis.diagnostics.filter((d) => d.severity === "warning").length;
  const dir = file.path?.includes("/") ? file.path.slice(0, file.path.lastIndexOf("/")) : "";
  const name = file.path ? file.path.slice(file.path.lastIndexOf("/") + 1) : `${doc.name}.dot`;
  return (
    <header className="rs-topbar">
      <div className="rs-topbar__brand">
        <Mark />
        <span className="rs-topbar__word">Reactor</span>
        <span className="rs-topbar__studio mono">studio</span>
      </div>
      <div className="rs-topbar__file" title={file.path ?? "Not saved yet"}>
        {dir ? <span className="faint">{dir} /</span> : null}
        <span className="rs-topbar__name">{name}</span>
        {!file.path ? <span className="rs-topbar__unsaved mono">unsaved</span> : dirty ? <span className="rs-topbar__dirty" title="Unsaved changes" /> : null}
      </div>
      <nav className="rs-modes" role="tablist">
        {MODES.map((m, i) => (
          <button key={m.value} type="button" role="tab" aria-selected={mode === m.value} className={cx("rs-modes__tab", mode === m.value && "is-on")} onClick={() => setMode(m.value)} title={`${m.hint} (${i + 1})`}>
            <Glyph name={m.icon} size={14} />
            {m.label}
          </button>
        ))}
      </nav>
      <div className="rs-topbar__right">
        <button type="button" className={cx("rs-health", errors ? "is-error" : warnings ? "is-warning" : "is-ok", !connected && "is-offline")} onClick={() => setBottomPanel("problems")} title={connected ? info?.backend.description : "Server unreachable"}>
          <span className="rs-health__dot" />
          {!connected ? "offline" : analysis.parseError ? "syntax error" : errors ? `${errors} error${errors > 1 ? "s" : ""}` : warnings ? `${warnings} warning${warnings > 1 ? "s" : ""}` : "valid"}
        </button>
        <Button variant="ghost" icon="search" title="Command palette (Ctrl K)" onClick={() => useStudio.setState({ paletteOpen: true })} />
        <Button variant="quiet" icon="architect" onClick={() => useStudio.setState({ architectOpen: true })}>
          Architect
        </Button>
        <Button variant="primary" icon="play" onClick={() => useStudio.setState({ runOpen: true })} disabled={!connected}>
          Run
        </Button>
      </div>
    </header>
  );
}

const RAIL: { panel: Exclude<LeftPanel, null>; icon: string; label: string }[] = [
  { panel: "library", icon: "library", label: "Library" },
  { panel: "files", icon: "files", label: "Files" },
  { panel: "outline", icon: "outline", label: "Outline" },
  { panel: "models", icon: "models", label: "Models" },
];

function LeftRail() {
  const left = useStudio((s) => s.leftPanel);
  const bottom = useStudio((s) => s.bottomPanel);
  const mode = useStudio((s) => s.mode);
  return (
    <aside className="rs-rail">
      {RAIL.map((r) => (
        <button key={r.panel} type="button" className={cx("rs-rail__btn", left === r.panel && mode === "design" && "is-on")} onClick={() => (mode === "design" ? setLeftPanel(r.panel) : (setMode("design"), useStudio.setState({ leftPanel: r.panel })))} title={r.label}>
          <Glyph name={r.icon} size={17} />
          <span>{r.label}</span>
        </button>
      ))}
      <div className="rs-rail__spacer" />
      <button type="button" className={cx("rs-rail__btn", bottom === "code" && "is-on")} onClick={() => setBottomPanel("code")} title="DOT code (Ctrl `)">
        <Glyph name="code" size={17} />
        <span>Code</span>
      </button>
      <button type="button" className={cx("rs-rail__btn", bottom === "prompt" && "is-on")} onClick={() => setBottomPanel("prompt")} title="Prompt preview">
        <Glyph name="prompt" size={17} />
        <span>Prompt</span>
      </button>
      <a className="rs-rail__btn" href="/console" target="_blank" rel="noreferrer" title="Classic console">
        <Glyph name="console" size={17} />
        <span>Console</span>
      </a>
    </aside>
  );
}

function LeftPanelHost() {
  const left = useStudio((s) => s.leftPanel);
  if (!left) return null;
  const titles: Record<Exclude<LeftPanel, null>, string> = { library: "Library", files: "Files", outline: "Outline", models: "Models" };
  return (
    <aside className="rs-left">
      <header className="rs-panel-head">
        <span className="label">{titles[left]}</span>
        <Button variant="ghost" size="sm" icon="chevronLeft" title="Collapse" onClick={() => useStudio.setState({ leftPanel: null })} />
      </header>
      {left === "library" ? <Library /> : left === "files" ? <Files /> : left === "outline" ? <Outline /> : <ModelsPanel />}
    </aside>
  );
}

function BottomDrawer() {
  const bottom = useStudio((s) => s.bottomPanel);
  const [height, setHeight] = useState(() => Number(localStorage.getItem("reactor.drawer") ?? 300));
  const problems = useStudio((s) => s.analysis.diagnostics.length + (s.analysis.parseError ? 1 : 0));
  if (!bottom) return null;
  const startResize = (e: React.MouseEvent) => {
    const startY = e.clientY;
    const startH = height;
    const move = (ev: MouseEvent) => setHeight(Math.max(140, Math.min(window.innerHeight - 220, startH - (ev.clientY - startY))));
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
      localStorage.setItem("reactor.drawer", String(height));
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  };
  const tabs: { id: "code" | "problems" | "prompt"; label: string; icon: string; badge?: number }[] = [
    { id: "code", label: "DOT", icon: "code" },
    { id: "prompt", label: "Prompt preview", icon: "prompt" },
    { id: "problems", label: "Problems", icon: "problems", badge: problems },
  ];
  return (
    <section className="rs-drawer" style={{ height }}>
      <div className="rs-drawer__grip" onMouseDown={startResize} />
      <header className="rs-drawer__tabs">
        {tabs.map((t) => (
          <button key={t.id} type="button" className={cx("rs-drawer__tab", bottom === t.id && "is-on")} onClick={() => useStudio.setState({ bottomPanel: t.id })}>
            <Glyph name={t.icon} size={13} />
            {t.label}
            {t.badge ? <span className="rs-drawer__badge num">{t.badge}</span> : null}
          </button>
        ))}
        <span className="rs-drawer__spacer" />
        <Button variant="ghost" size="sm" icon="close" title="Close" onClick={() => useStudio.setState({ bottomPanel: null })} />
      </header>
      <div className="rs-drawer__body">{bottom === "code" ? <CodeView /> : bottom === "prompt" ? <PromptPreview /> : <Problems />}</div>
    </section>
  );
}

function Toasts() {
  const toasts = useStudio((s) => s.toasts);
  return (
    <div className="rs-toasts">
      {toasts.map((t) => (
        <div key={t.id} className={cx("rs-toast", `rs-toast--${t.kind}`)}>
          <span className="rs-toast__mark" />
          <span className="rs-toast__text">{t.text}</span>
          {t.action ? (
            <Button
              size="sm"
              variant="quiet"
              onClick={() => {
                t.action!.run();
                dismissToast(t.id);
              }}
            >
              {t.action.label}
            </Button>
          ) : null}
          <Button size="sm" variant="ghost" icon="close" onClick={() => dismissToast(t.id)} />
        </div>
      ))}
    </div>
  );
}

function TokenDialog() {
  const needsToken = useStudio((s) => s.needsToken);
  const [value, setValue] = useState("");
  if (!needsToken) return null;
  return (
    <Dialog
      kicker="Authentication"
      title="This server needs an API token"
      onClose={() => useStudio.setState({ needsToken: false })}
      footer={
        <Button
          variant="primary"
          onClick={() => {
            setToken(value.trim());
            useStudio.setState({ needsToken: false });
            void loadInfo();
          }}
        >
          Connect
        </Button>
      }
    >
      <Field label="Token" hint="The value passed to reactor serve --token (or REACTOR_SERVER_TOKEN). It is kept in this browser's local storage.">
        <TextInput value={value} onChange={setValue} mono autoFocus />
      </Field>
    </Dialog>
  );
}

/** The editable canvas, optionally wearing the flight record (telemetry of the pipeline's real flights). */
function DesignCanvas() {
  const doc = useStudio((s) => s.doc);
  const info = useStudio((s) => s.info);
  const lens = useTelemetry((s) => s.designLens);
  const record = useTelemetry((s) => s.byName[doc.name]);
  useEffect(() => {
    if (info) void loadTelemetry(doc.name, 60000);
  }, [doc.name, info]);
  const overlay = useMemo(() => (lens && record ? telemetryOverlay(doc, record, { dimUnvisited: false }) : null), [lens, record, doc]);
  return <Canvas overlay={overlay} />;
}

function ViewCenterBridge() {
  const rf = useReactFlow();
  useEffect(() => {
    setCenterOfView(() => {
      const el = document.querySelector(".rs-canvas");
      const rect = el?.getBoundingClientRect();
      if (!rect) return { x: 120, y: 120 };
      const p = rf.screenToFlowPosition({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 });
      return { x: p.x - 140, y: p.y - 70 };
    });
  }, [rf]);
  return null;
}

export function App() {
  const mode = useStudio((s) => s.mode);
  const inspectorOpen = useStudio((s) => s.inspectorOpen);
  const saveAsOpen = useStudio((s) => s.saveAsOpen);
  useKeyboard();
  useEffect(() => {
    startSession();
    const last = localStorage.getItem("reactor.lastFile");
    const wait = setInterval(() => {
      if (!useStudio.getState().connected) return;
      clearInterval(wait);
      if (last) void openFile(last);
      else void newDocument();
    }, 150);
    const beforeUnload = (e: BeforeUnloadEvent) => {
      if (useStudio.getState().dirty) e.preventDefault();
    };
    window.addEventListener("beforeunload", beforeUnload);
    return () => {
      clearInterval(wait);
      window.removeEventListener("beforeunload", beforeUnload);
    };
  }, []);

  return (
    <ReactFlowProvider>
      <ViewCenterBridge />
      <div className={cx("rs-app", `mode-${mode}`)}>
        <TopBar />
        <div className="rs-body">
          <LeftRail />
          {mode === "design" ? <LeftPanelHost /> : null}
          <main className="rs-main">
            <div className="rs-stage">{mode === "design" ? <DesignCanvas /> : <ModeStage mode={mode} />}</div>
            {mode === "design" ? <BottomDrawer /> : null}
          </main>
          {mode === "design" && inspectorOpen ? (
            <aside className="rs-right">
              <Inspector />
            </aside>
          ) : null}
        </div>
        <CommandPalette />
        <RunDialog />
        <Toasts />
        <TokenDialog />
        {saveAsOpen ? <SaveAsDialog onClose={() => useStudio.setState({ saveAsOpen: false })} /> : null}
        <ShortcutHint />
      </div>
    </ReactFlowProvider>
  );
}

function ShortcutHint() {
  const mode = useStudio((s) => s.mode);
  if (mode !== "design") return null;
  return (
    <div className="rs-hint">
      <Kbd>Ctrl</Kbd>
      <Kbd>K</Kbd>
      <span>commands</span>
    </div>
  );
}
