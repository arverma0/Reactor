import { useMemo, useState } from "react";
import { cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { artifactIndex } from "../model/artifacts";
import { kindOf, nodeAttr, nodeLabel } from "../model/doc";
import { lineColor } from "../model/palette";
import type { DocNode } from "../model/types";
import { focusNode, select, useStudio } from "../state/store";

export function Outline() {
  const doc = useStudio((s) => s.doc);
  const selection = useStudio((s) => s.selection);
  const diagnostics = useStudio((s) => s.analysis.diagnostics);
  const [query, setQuery] = useState("");
  const q = query.trim().toLowerCase();
  const match = (n: DocNode) => !q || n.id.toLowerCase().includes(q) || nodeLabel(doc, n).toLowerCase().includes(q) || (nodeAttr(doc, n, "prompt") ?? "").toLowerCase().includes(q);

  const sections = useMemo(() => {
    const root = doc.nodes.filter((n) => !n.group && match(n));
    const groups = doc.groups.map((g) => ({ group: g, nodes: doc.nodes.filter((n) => n.group === g.uid && match(n)) })).filter((s) => s.nodes.length > 0);
    return { root, groups };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [doc, q]);

  const artifacts = useMemo(() => [...artifactIndex(doc).values()].sort((a, b) => a.name.localeCompare(b.name)), [doc]);
  const threads = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const n of doc.nodes) {
      const t = nodeAttr(doc, n, "thread_id");
      if (t) map.set(t, [...(map.get(t) ?? []), n.id]);
    }
    return [...map.entries()];
  }, [doc]);

  const row = (n: DocNode) => {
    const kind = kindOf(doc, n);
    const issues = diagnostics.filter((d) => d.node_id === n.id);
    return (
      <button key={n.id} type="button" className={cx("rs-outline__row", selection.nodes.includes(n.id) && "is-selected")} onClick={() => focusNode(n.id)}>
        <span className="rs-outline__glyph" style={{ color: `var(--kind-${kind})` }}>
          <Glyph name={kind} size={13} />
        </span>
        <span className="rs-outline__label">{nodeLabel(doc, n)}</span>
        {nodeLabel(doc, n) !== n.id ? <span className="rs-outline__id mono">{n.id}</span> : null}
        {issues.length > 0 ? <span className={cx("rs-outline__diag", issues.some((d) => d.severity === "error") ? "is-error" : "is-warning")} /> : null}
      </button>
    );
  };

  return (
    <div className="rs-outline">
      <div className="rs-panel-search">
        <Glyph name="search" size={13} />
        <input value={query} placeholder="Find a stage, id, or prompt text" onChange={(e) => setQuery(e.target.value)} onKeyDown={(e) => e.stopPropagation()} />
      </div>
      <div className="rs-panel-scroll">
        <div className="rs-outline__heading label">
          Stages <span className="num">{doc.nodes.length}</span>
        </div>
        {sections.root.map(row)}
        {sections.groups.map(({ group, nodes }) => (
          <div key={group.uid} className="rs-outline__group">
            <button type="button" className="rs-outline__group-head" onClick={() => select({ group: group.uid })}>
              <Glyph name="group" size={12} />
              {group.attrs.label ?? group.name ?? "group"}
            </button>
            {nodes.map(row)}
          </div>
        ))}
        {artifacts.length > 0 ? (
          <>
            <div className="rs-outline__heading label">
              Artifacts <span className="num">{artifacts.length}</span>
            </div>
            {artifacts.map((a) => (
              <div key={a.name} className="rs-outline__artifact">
                <span className="rs-dot" style={{ background: a.color }} />
                <span className="mono">{a.name}</span>
                <span className="rs-outline__flow faint">
                  {a.producers.map((p) => p.node).join(", ") || <em className="rs-warn-text">no producer</em>} → {a.consumers.join(", ") || "—"}
                </span>
              </div>
            ))}
          </>
        ) : null}
        {threads.length > 0 ? (
          <>
            <div className="rs-outline__heading label">Threads</div>
            {threads.map(([thread, nodes]) => (
              <div key={thread} className="rs-outline__artifact">
                <span className="rs-dot" style={{ background: lineColor(thread, doc.layout.threads?.[thread]?.color) }} />
                <span className="mono">{thread}</span>
                <span className="rs-outline__flow faint">{nodes.join(" · ")}</span>
              </div>
            ))}
          </>
        ) : null}
      </div>
    </div>
  );
}
