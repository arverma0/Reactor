import { useMemo } from "react";
import { cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { quickFixes, type QuickFix } from "../model/quickfix";
import type { Diagnostic } from "../model/types";
import { applyDoc, select, toast, undo, useStudio } from "../state/store";

/** Applies a quick fix as one undoable edit and says what changed (with Undo). */
export function runQuickFix(fix: QuickFix) {
  const before = useStudio.getState().doc;
  applyDoc(fix.apply);
  if (useStudio.getState().doc === before) {
    toast("Nothing to change: the pipeline already has this.", "info", undefined, 2500);
    return;
  }
  if (fix.focus) select({ nodes: [fix.focus] });
  toast(fix.done, "success", { label: "Undo", run: undo }, 7000);
}

/**
 * One-click fixes for a lint finding (flow-control rules): single fixes as buttons, alternatives — which edge becomes the
 * default, which stage bounds a loop — as a choice, the recommended one first.
 */
export function QuickFixes({ diagnostic, className }: { diagnostic: Diagnostic; className?: string }) {
  const doc = useStudio((s) => s.doc);
  const fixes = useMemo(() => quickFixes(doc, diagnostic), [doc, diagnostic]);
  if (fixes.length === 0) return null;
  const singles = fixes.filter((f) => !f.group);
  const groups = new Map<string, QuickFix[]>();
  for (const f of fixes) if (f.group) groups.set(f.group, [...(groups.get(f.group) ?? []), f]);
  return (
    <div className={cx("rs-fixes", className)} onClick={(e) => e.stopPropagation()}>
      <Glyph name="spark" size={11} />
      {singles.map((f) => (
        <button key={f.id} type="button" className="rs-fix" title={f.title} onClick={() => runQuickFix(f)}>
          {f.label}
        </button>
      ))}
      {[...groups.entries()].map(([group, list]) => (
        <span key={group} className="rs-fixes__group">
          <span className="rs-fixes__label">{group}:</span>
          {list.map((f) => (
            <button key={f.id} type="button" className={cx("rs-fix", f.recommended && "is-recommended")} title={f.title + (f.recommended ? " (suggested)" : "")} onClick={() => runQuickFix(f)}>
              {f.label}
            </button>
          ))}
        </span>
      ))}
    </div>
  );
}
