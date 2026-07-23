import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { api } from "../api/client";
import type { WorkspaceFile } from "../api/types";
import { kindOf, nodeLabel } from "../model/doc";
import { KINDS, LIBRARY_KINDS } from "../model/handlers";
import { PATTERNS, insertPattern } from "../model/patterns";
import { addNodeOfKind, addNote, centerOfView, groupSelection } from "../state/editing";
import { newDocument, openFile, saveDocument } from "../state/files";
import { applyDoc, focusNode, redo, requestFit, requestLayout, select, setBottomPanel, setLeftPanel, setMode, undo, useStudio } from "../state/store";
import { cx } from "./controls";
import { Glyph } from "./Glyph";

interface Command {
  id: string;
  title: string;
  section: string;
  icon?: string;
  hint?: string;
  keywords?: string;
  run: () => void;
}

function score(text: string, query: string): number {
  if (!query) return 1;
  const t = text.toLowerCase();
  const q = query.toLowerCase();
  if (t.startsWith(q)) return 100 - t.length * 0.01;
  const idx = t.indexOf(q);
  if (idx >= 0) return 60 - idx;
  let ti = 0;
  let hits = 0;
  for (const c of q) {
    const found = t.indexOf(c, ti);
    if (found < 0) return 0;
    hits += found === ti ? 2 : 1;
    ti = found + 1;
  }
  return hits;
}

export function CommandPalette() {
  const open = useStudio((s) => s.paletteOpen);
  const doc = useStudio((s) => s.doc);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const [files, setFiles] = useState<WorkspaceFile[]>([]);
  const list = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    setQuery("");
    setActive(0);
    api.workspace().then((r) => setFiles(r.files)).catch(() => setFiles([]));
  }, [open]);

  const close = () => useStudio.setState({ paletteOpen: false });

  const commands = useMemo<Command[]>(() => {
    const items: Command[] = [
      { id: "save", title: "Save", section: "File", icon: "save", hint: "Ctrl S", run: () => void saveOrAsk() },
      { id: "new", title: "New pipeline", section: "File", icon: "plus", run: () => void newDocument() },
      { id: "mode-design", title: "Design", section: "Mode", icon: "design", hint: "1", run: () => setMode("design") },
      { id: "mode-tunnel", title: "Wind tunnel — simulate routing", section: "Mode", icon: "tunnel", hint: "2", run: () => setMode("tunnel") },
      { id: "mode-flights", title: "Flights — runs, replays, telemetry", section: "Mode", icon: "flights", hint: "3", run: () => setMode("flights") },
      { id: "architect", title: "Ask the Architect…", section: "AI", icon: "architect", keywords: "ai generate design", run: () => useStudio.setState({ architectOpen: true }) },
      { id: "undo", title: "Undo", section: "Edit", icon: "undo", hint: "Ctrl Z", run: undo },
      { id: "redo", title: "Redo", section: "Edit", icon: "redo", hint: "Ctrl Shift Z", run: redo },
      { id: "layout", title: "Auto-arrange", section: "View", icon: "layout", hint: "L", run: requestLayout },
      { id: "fit", title: "Fit to screen", section: "View", icon: "fit", hint: "F", run: requestFit },
      { id: "code", title: "Toggle DOT code", section: "View", icon: "code", hint: "Ctrl `", run: () => setBottomPanel("code") },
      { id: "problems", title: "Toggle problems", section: "View", icon: "problems", run: () => setBottomPanel("problems") },
      { id: "prompt", title: "Preview the selected stage's prompt", section: "View", icon: "prompt", run: () => setBottomPanel("prompt") },
      { id: "models", title: "Model stylesheet", section: "View", icon: "models", run: () => setLeftPanel("models") },
      { id: "note", title: "Add note", section: "Insert", icon: "note", hint: "N", run: addNote },
      { id: "group", title: "Group selection", section: "Edit", icon: "group", hint: "Ctrl G", run: () => groupSelection() },
      ...LIBRARY_KINDS.map((k) => ({ id: `add-${k}`, title: `Add ${KINDS[k].name.toLowerCase()}`, section: "Insert", icon: k, hint: KINDS[k].hotkey, keywords: KINDS[k].blurb, run: () => addNodeOfKind(k) })),
      ...PATTERNS.map((p) => ({
        id: `pattern-${p.id}`,
        title: `Insert pattern: ${p.name}`,
        section: "Patterns",
        icon: p.icon,
        keywords: p.blurb,
        run: () => {
          let ids: string[] = [];
          applyDoc((d) => {
            const r = insertPattern(d, p, centerOfView());
            ids = r.ids;
            return r.doc;
          });
          select({ nodes: ids });
        },
      })),
      ...doc.nodes.map((n) => ({ id: `goto-${n.id}`, title: `Go to ${nodeLabel(doc, n)}`, section: "Stages", icon: kindOf(doc, n), hint: n.id, keywords: n.id, run: () => focusNode(n.id) })),
      ...files.map((f) => ({ id: `open-${f.path}`, title: `Open ${f.path}`, section: "Files", icon: "code", keywords: f.goal ?? "", run: () => void openFile(f.path) })),
      { id: "console", title: "Open the classic console", section: "Other", icon: "console", run: () => window.open("/console", "_blank") },
    ];
    return items;
  }, [doc, files]);

  const results = useMemo(() => {
    const q = query.trim();
    return commands
      .map((c) => ({ c, s: Math.max(score(c.title, q), score(c.keywords ?? "", q) * 0.6) }))
      .filter((r) => r.s > 0)
      .sort((a, b) => b.s - a.s)
      .slice(0, 40)
      .map((r) => r.c);
  }, [commands, query]);

  useEffect(() => {
    list.current?.querySelector(".is-active")?.scrollIntoView({ block: "nearest" });
  }, [active]);

  if (!open) return null;
  const run = (c: Command | undefined) => {
    if (!c) return;
    close();
    setTimeout(c.run, 0);
  };
  return createPortal(
    <div className="rs-palette__scrim" onMouseDown={(e) => e.target === e.currentTarget && close()}>
      <div className="rs-palette" onKeyDown={(e) => e.stopPropagation()}>
        <div className="rs-palette__input">
          <Glyph name="search" size={15} />
          <input
            autoFocus
            value={query}
            placeholder="Type a command, a stage, or a file…"
            onChange={(e) => {
              setQuery(e.target.value);
              setActive(0);
            }}
            onKeyDown={(e) => {
              if (e.key === "ArrowDown") {
                e.preventDefault();
                setActive((a) => Math.min(a + 1, results.length - 1));
              } else if (e.key === "ArrowUp") {
                e.preventDefault();
                setActive((a) => Math.max(a - 1, 0));
              } else if (e.key === "Enter") run(results[active]);
              else if (e.key === "Escape") close();
            }}
          />
          <kbd className="rs-kbd">esc</kbd>
        </div>
        <div className="rs-palette__list" ref={list}>
          {results.map((c, i) => (
            <button key={c.id} type="button" className={cx("rs-palette__item", i === active && "is-active")} onMouseEnter={() => setActive(i)} onClick={() => run(c)}>
              <span className="rs-palette__icon">{c.icon ? <Glyph name={c.icon} size={14} /> : null}</span>
              <span className="rs-palette__title">{c.title}</span>
              <span className="rs-palette__section">{c.section}</span>
              {c.hint ? <span className="rs-palette__hint mono">{c.hint}</span> : null}
            </button>
          ))}
          {results.length === 0 ? <div className="rs-palette__empty serif">Nothing matches “{query}”.</div> : null}
        </div>
      </div>
    </div>,
    document.body,
  );
}

export async function saveOrAsk() {
  const path = useStudio.getState().file.path;
  if (path) await saveDocument();
  else useStudio.setState({ saveAsOpen: true });
}
