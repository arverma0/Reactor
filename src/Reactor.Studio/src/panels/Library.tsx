import { useMemo, useState } from "react";
import { cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { KINDS, LIBRARY_KINDS, type NodeKind } from "../model/handlers";
import { PATTERNS, insertPattern } from "../model/patterns";
import { addNodeOfKind, centerOfView } from "../state/editing";
import { applyDoc, select, toast } from "../state/store";

export function Library() {
  const [query, setQuery] = useState("");
  const q = query.trim().toLowerCase();
  const kinds = useMemo(() => LIBRARY_KINDS.filter((k) => !q || KINDS[k].name.toLowerCase().includes(q) || KINDS[k].blurb.toLowerCase().includes(q)), [q]);
  const patterns = useMemo(() => PATTERNS.filter((p) => !q || p.name.toLowerCase().includes(q) || p.blurb.toLowerCase().includes(q)), [q]);

  const insert = (id: string) => {
    const spec = PATTERNS.find((p) => p.id === id);
    if (!spec) return;
    let ids: string[] = [];
    applyDoc((d) => {
      const result = insertPattern(d, spec, centerOfView());
      ids = result.ids;
      return result.doc;
    });
    select({ nodes: ids });
    toast(`Inserted “${spec.name}”.`, "info", undefined, 2000);
  };

  return (
    <div className="rs-library">
      <div className="rs-panel-search">
        <Glyph name="search" size={13} />
        <input value={query} placeholder="Find a stage or pattern" onChange={(e) => setQuery(e.target.value)} onKeyDown={(e) => e.stopPropagation()} />
      </div>
      <div className="rs-panel-scroll">
        <div className="rs-library__heading label">Stages</div>
        <div className="rs-library__grid">
          {kinds.map((kind) => (
            <ModuleCard key={kind} kind={kind} />
          ))}
        </div>
        <div className="rs-library__heading label">Factory patterns</div>
        <div className="rs-library__patterns">
          {patterns.map((pattern) => (
            <button
              key={pattern.id}
              type="button"
              className="rs-pattern"
              draggable
              onDragStart={(e) => {
                e.dataTransfer.setData("application/x-reactor-pattern", pattern.id);
                e.dataTransfer.effectAllowed = "copy";
              }}
              onClick={() => insert(pattern.id)}
              title="Click to insert, or drag onto the canvas"
            >
              <span className="rs-pattern__icon">
                <Glyph name={pattern.icon} size={14} />
              </span>
              <span className="rs-pattern__body">
                <span className="rs-pattern__name">{pattern.name}</span>
                <span className="rs-pattern__blurb">{pattern.blurb}</span>
                <PatternGlimpse id={pattern.id} />
              </span>
            </button>
          ))}
        </div>
        <p className="rs-library__foot serif">Drag onto the canvas, or click to place next to the selection.</p>
      </div>
    </div>
  );
}

function ModuleCard({ kind }: { kind: NodeKind }) {
  const meta = KINDS[kind];
  return (
    <button
      type="button"
      className={cx("rs-module", `rs-module--${kind}`)}
      style={{ ["--accent" as string]: `var(--kind-${kind})` }}
      draggable
      onDragStart={(e) => {
        e.dataTransfer.setData("application/x-reactor-kind", kind);
        e.dataTransfer.effectAllowed = "copy";
      }}
      onClick={() => addNodeOfKind(kind)}
      title={meta.blurb}
    >
      <span className="rs-module__top">
        <Glyph name={kind} size={16} />
        <span className="rs-module__code">{meta.code}</span>
        {meta.hotkey ? <kbd className="rs-kbd">{meta.hotkey}</kbd> : null}
      </span>
      <span className="rs-module__name">{meta.name}</span>
      <span className="rs-module__blurb">{meta.blurb}</span>
    </button>
  );
}

/** A tiny schematic of the pattern's shape (node kinds as dots in their relative positions). */
function PatternGlimpse({ id }: { id: string }) {
  const spec = PATTERNS.find((p) => p.id === id)!;
  const xs = spec.nodes.map((n) => n.x);
  const ys = spec.nodes.map((n) => n.y);
  const minX = Math.min(...xs);
  const maxX = Math.max(...xs) || 1;
  const minY = Math.min(...ys);
  const maxY = Math.max(...ys) || 1;
  const w = 120;
  const h = 26;
  const px = (x: number) => 6 + ((x - minX) / Math.max(1, maxX - minX)) * (w - 12);
  const py = (y: number) => 5 + ((y - minY) / Math.max(1, maxY - minY)) * (h - 10);
  const pos = Object.fromEntries(spec.nodes.map((n) => [n.key, { x: px(n.x), y: maxY === minY ? h / 2 : py(n.y) }]));
  return (
    <svg className="rs-pattern__glimpse" width={w} height={h} viewBox={`0 0 ${w} ${h}`}>
      {spec.edges.map((e, i) => (
        <line key={i} x1={pos[e.from].x} y1={pos[e.from].y} x2={pos[e.to].x} y2={pos[e.to].y} stroke={e.attrs?.condition?.includes("fail") ? "var(--fail)" : "var(--line-4)"} strokeWidth={1} strokeDasharray={e.attrs?.condition?.includes("fail") ? "2 2" : undefined} />
      ))}
      {spec.nodes.map((n) => (
        <circle key={n.key} cx={pos[n.key].x} cy={pos[n.key].y} r={3} fill={`var(--kind-${n.kind})`} />
      ))}
    </svg>
  );
}
