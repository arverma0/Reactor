import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { HighlightStyle, StreamLanguage, bracketMatching, syntaxHighlighting } from "@codemirror/language";
import { searchKeymap, highlightSelectionMatches } from "@codemirror/search";
import { EditorState, StateEffect, StateField } from "@codemirror/state";
import { Decoration, EditorView, drawSelection, highlightActiveLine, keymap, lineNumbers, type DecorationSet } from "@codemirror/view";
import { tags } from "@lezer/highlight";
import { useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import { cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { writeDot } from "../model/dot";
import { docFromParse } from "../model/importer";
import { placeMissing } from "../model/layout";
import { applyDoc, useStudio } from "../state/store";

/** A small DOT grammar: keywords, attribute keys, strings, numbers/durations, comments, arrows. */
const dotLanguage = StreamLanguage.define<{ inComment: boolean }>({
  startState: () => ({ inComment: false }),
  token(stream, state) {
    if (state.inComment) {
      if (stream.match(/.*?\*\//)) state.inComment = false;
      else stream.skipToEnd();
      return "comment";
    }
    if (stream.eatSpace()) return null;
    if (stream.match("//") || (stream.sol() && stream.match("#"))) {
      stream.skipToEnd();
      return "comment";
    }
    if (stream.match("/*")) {
      state.inComment = true;
      return "comment";
    }
    if (stream.match(/^"(?:[^"\\]|\\.)*"?/)) return "string";
    if (stream.match("->")) return "operator";
    if (stream.match(/^(digraph|subgraph|graph|node|edge|strict)\b/i)) return "keyword";
    if (stream.match(/^[A-Za-z_][\w.]*(?=\s*=)/)) return "propertyName";
    if (stream.match(/^\d+(?:\.\d+)?(?:ms|s|m|h|d)?\b/)) return "number";
    if (stream.match(/^(true|false)\b/)) return "bool";
    if (stream.match(/^[A-Za-z_][\w.:-]*/)) return "variableName";
    stream.next();
    return null;
  },
});

const highlight = HighlightStyle.define([
  { tag: tags.keyword, color: "#ff7a3d", fontWeight: "500" },
  { tag: tags.propertyName, color: "#8fb4ff" },
  { tag: tags.string, color: "#d9c79a" },
  { tag: tags.number, color: "#7ccfb0" },
  { tag: tags.bool, color: "#7ccfb0" },
  { tag: tags.comment, color: "#6c6862", fontStyle: "italic" },
  { tag: tags.operator, color: "#9a958c" },
  { tag: tags.variableName, color: "#efebe4" },
]);

const theme = EditorView.theme(
  {
    "&": { height: "100%", background: "transparent", color: "#efebe4", fontSize: "12.5px" },
    ".cm-content": { fontFamily: "var(--font-mono)", padding: "10px 0", caretColor: "#ff5b14" },
    ".cm-gutters": { background: "transparent", border: "none", color: "#47443f" },
    ".cm-activeLine": { background: "rgba(255,255,255,0.025)" },
    ".cm-activeLineGutter": { background: "transparent", color: "#9a958c" },
    ".cm-selectionBackground, &.cm-focused .cm-selectionBackground": { background: "rgba(255,91,20,0.22) !important" },
    ".cm-cursor": { borderLeftColor: "#ff5b14" },
    ".cm-scroller": { lineHeight: "1.6" },
    ".rs-cm-error": { textDecoration: "underline wavy #f0544f", textUnderlineOffset: "3px" },
    ".rs-cm-error-line": { background: "rgba(240,84,79,0.08)" },
  },
  { dark: true },
);

const setError = StateEffect.define<{ line: number; column: number } | null>();
const errorField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(value, tr) {
    for (const effect of tr.effects) {
      if (effect.is(setError)) {
        if (!effect.value) return Decoration.none;
        const lineCount = tr.state.doc.lines;
        const line = tr.state.doc.line(Math.min(Math.max(1, effect.value.line), lineCount));
        const from = Math.min(line.to, line.from + Math.max(0, effect.value.column - 1));
        const to = Math.min(line.to, from + 1) > from ? Math.min(line.to, from + 1) : from;
        const decorations = [Decoration.line({ class: "rs-cm-error-line" }).range(line.from)];
        if (to > from) decorations.push(Decoration.mark({ class: "rs-cm-error" }).range(from, to));
        return Decoration.set(decorations, true);
      }
    }
    return value.map(tr.changes);
  },
  provide: (f) => EditorView.decorations.from(f),
});

/**
 * Two-way DOT view. Edits on the canvas rewrite the text (unless you are typing here); edits here are parsed by the server
 * and, when valid, replace the document (positions are kept by stage id).
 */
export function CodeView() {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const typing = useRef(false);
  const doc = useStudio((s) => s.doc);
  const hadComments = useStudio((s) => s.file.hadBodyComments);
  const [status, setStatus] = useState<{ kind: "synced" | "pending" | "error"; message?: string }>({ kind: "synced" });
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);

  useEffect(() => {
    if (!host.current) return;
    const apply = async (text: string) => {
      setStatus({ kind: "pending" });
      try {
        const result = await api.parse(text);
        if (!result.ok) {
          setStatus({ kind: "error", message: `${result.error} (line ${result.line}, column ${result.column})` });
          view.current?.dispatch({ effects: setError.of({ line: result.line, column: result.column }) });
          return;
        }
        view.current?.dispatch({ effects: setError.of(null) });
        const current = useStudio.getState().doc;
        const next = docFromParse(result.graph, text, current.layout);
        // Keep presentation-only state from the current document.
        next.layout = { ...current.layout, nodes: { ...next.layout.nodes } };
        for (const n of next.nodes) if (current.layout.nodes[n.id]) next.layout.nodes[n.id] = current.layout.nodes[n.id];
        const missing = placeMissing(next);
        if (missing) next.layout.nodes = { ...next.layout.nodes, ...missing };
        applyDoc(() => next, { coalesce: "code" });
        setStatus({ kind: "synced" });
      } catch (error) {
        setStatus({ kind: "error", message: (error as Error).message });
      }
    };
    const editor = new EditorView({
      parent: host.current,
      state: EditorState.create({
        doc: writeDot(useStudio.getState().doc),
        extensions: [
          lineNumbers(),
          history(),
          drawSelection(),
          highlightActiveLine(),
          bracketMatching(),
          highlightSelectionMatches(),
          dotLanguage,
          syntaxHighlighting(highlight),
          theme,
          errorField,
          EditorView.lineWrapping,
          keymap.of([indentWithTab, ...defaultKeymap, ...historyKeymap, ...searchKeymap]),
          EditorView.domEventHandlers({
            focus: () => {
              typing.current = true;
            },
            blur: () => {
              typing.current = false;
            },
            keydown: (e) => {
              e.stopPropagation();
            },
          }),
          EditorView.updateListener.of((update) => {
            if (!update.docChanged || !typing.current) return;
            clearTimeout(timer.current);
            const text = update.state.doc.toString();
            setStatus({ kind: "pending" });
            timer.current = setTimeout(() => void apply(text), 550);
          }),
        ],
      }),
    });
    view.current = editor;
    return () => {
      clearTimeout(timer.current);
      editor.destroy();
      view.current = null;
    };
  }, []);

  // Canvas → text, unless the user is typing in the editor.
  useEffect(() => {
    const editor = view.current;
    if (!editor || typing.current) return;
    const text = writeDot(doc);
    if (editor.state.doc.toString() !== text) {
      editor.dispatch({ changes: { from: 0, to: editor.state.doc.length, insert: text }, effects: setError.of(null) });
    }
  }, [doc]);

  return (
    <div className="rs-code">
      <div className="rs-code__bar">
        <span className={cx("rs-code__status", `is-${status.kind}`)}>
          <span className="rs-dot" />
          {status.kind === "synced" ? "In sync with the canvas" : status.kind === "pending" ? "Parsing…" : status.message}
        </span>
        {hadComments ? (
          <span className="rs-code__note" title="DOT comments inside the graph body are not kept when the Studio rewrites the file. The header comment is kept.">
            <Glyph name="problems" size={12} /> body comments are dropped on save
          </span>
        ) : null}
      </div>
      <div className="rs-code__editor" ref={host} />
    </div>
  );
}
