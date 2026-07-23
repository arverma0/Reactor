import { useMemo, useState } from "react";
import { api } from "../../api/client";
import { Button, Dialog, Field, Segmented, TextArea, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { describeDiff, diffDocs } from "../../model/diff";
import { findNode, kindOf, nodeLabel } from "../../model/doc";
import { writeDot } from "../../model/dot";
import type { StudioDoc } from "../../model/types";
import { toast, useStudio } from "../../state/store";
import { checkpointBefore, checkpointNode, type RunReplay, type StageExecution } from "./replay";
import { refreshRuns, rememberRunSource, selectRun } from "./store";
import type { FlightView } from "./view";

interface Candidate {
  exec: StageExecution;
  checkpoint: string;
  visit: number;
}

/** Stage executions a flight can be re-flown from (each needs a snapshot saved before it). */
export function forkCandidates(view: FlightView, replay: RunReplay): Candidate[] {
  const visits = new Map<string, number>();
  const out: Candidate[] = [];
  for (const exec of replay.executions) {
    if (exec.attempt !== 1) continue;
    const visit = (visits.get(exec.node) ?? 0) + 1;
    visits.set(exec.node, visit);
    const checkpoint = checkpointBefore(exec, view.checkpoints);
    if (checkpoint) out.push({ exec, checkpoint, visit });
  }
  return out;
}

function parseContext(text: string): { value?: Record<string, unknown>; error?: string } {
  const value: Record<string, unknown> = {};
  for (const [i, raw] of text.split(/\r?\n/).entries()) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const eq = line.indexOf("=");
    if (eq <= 0) return { error: `Line ${i + 1}: expected key = value` };
    const key = line.slice(0, eq).trim();
    const rest = line.slice(eq + 1).trim();
    try {
      value[key] = /^(-?\d+(\.\d+)?|true|false|null|\{.*\}|\[.*\]|".*")$/.test(rest) ? JSON.parse(rest) : rest;
    } catch {
      value[key] = rest;
    }
  }
  return { value };
}

/**
 * Re-fly: fork a flight from the snapshot saved just before one of its stages, with the pipeline as flown or with the
 * current design — the counterfactual "what if this stage had used another prompt or model" without paying for the
 * stages before it again.
 */
export function ForkDialog({ view, replay, doc, initial, onClose }: { view: FlightView; replay: RunReplay; doc: StudioDoc | null; initial?: StageExecution; onClose: () => void }) {
  const design = useStudio((s) => s.doc);
  const designFile = useStudio((s) => s.file.path);
  const designBroken = useStudio((s) => !!s.analysis.parseError || s.analysis.diagnostics.some((d) => d.severity === "error"));
  const candidates = useMemo(() => forkCandidates(view, replay), [view, replay]);
  const [choice, setChoice] = useState(() => {
    const initialIndex = initial ? candidates.findIndex((c) => c.exec === initial) : -1;
    if (initialIndex >= 0) return initialIndex;
    // The last failure is usually the one that decided the flight.
    const failed = candidates.findLastIndex((c) => c.exec.status === "fail" || !!c.exec.error);
    return failed >= 0 ? failed : Math.max(0, candidates.length - 1);
  });
  const [source, setSource] = useState<"flown" | "design">("flown");
  const [contextText, setContextText] = useState("");
  const [busy, setBusy] = useState(false);
  const picked = candidates[choice];
  const restoredNode = picked ? checkpointNode(picked.checkpoint) : "";
  const diff = useMemo(() => (doc ? diffDocs(doc, design) : null), [doc, design]);
  const context = parseContext(contextText);
  const designMissing = source === "design" && !!picked && !design.nodes.some((n) => n.id === restoredNode);
  const label = (id: string) => {
    const node = doc ? findNode(doc, id) : undefined;
    return node && doc ? nodeLabel(doc, node) : id;
  };

  const submit = async () => {
    if (!picked || !view.dir) return;
    setBusy(true);
    try {
      const dot = source === "design" ? writeDot(design) : undefined;
      const response = await api.fork(view.dir, { checkpoint: picked.checkpoint, dot, context: context.value && Object.keys(context.value).length > 0 ? context.value : undefined });
      if (source === "design") rememberRunSource(response.id, { dot: dot!, layout: design.layout, sourcePath: designFile });
      else if (doc) rememberRunSource(response.id, { dot: writeDot(doc), layout: doc.layout });
      await refreshRuns();
      selectRun(response.id);
      toast(`Re-flying from just before ${label(picked.exec.node)}.`, "success");
      onClose();
    } catch (error) {
      toast(`Could not re-fly: ${(error as Error).message}`, "error");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      kicker={
        <>
          <Glyph name="fork" size={12} /> Re-fly
        </>
      }
      title={`Fork ${view.name} from a stage`}
      width={600}
      onClose={onClose}
      footer={
        <>
          <span className="rs-fork__foot faint">The working directory is not rewound: files written since stay as they are.</span>
          <Button variant="quiet" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" icon="fork" disabled={!picked || busy || designMissing || !!context.error || (source === "design" && designBroken)} onClick={() => void submit()}>
            {busy ? "Starting…" : "Re-fly"}
          </Button>
        </>
      }
    >
      {candidates.length === 0 ? (
        <p className="faint">This flight kept no stage snapshots to fork from. Runs started by this server keep one after every stage.</p>
      ) : (
        <>
          <Field label="Re-run from" hint="Earlier stages are not flown again: their outcomes and context are restored from the snapshot.">
            <div className="rs-fork__list">
              {candidates.map((c, i) => {
                const node = doc ? findNode(doc, c.exec.node) : undefined;
                const status = c.exec.status ?? (c.exec.error ? "fail" : c.exec.end === undefined ? "running" : "success");
                return (
                  <button key={`${c.exec.index}-${c.exec.segment}`} type="button" className={cx("rs-fork__row", i === choice && "is-on")} onClick={() => setChoice(i)}>
                    <span className="rs-fork__index num">#{c.exec.index}</span>
                    {node && doc ? <Glyph name={kindOf(doc, node)} size={13} /> : <span />}
                    <span className="rs-fork__label">{label(c.exec.node)}</span>
                    {c.visit > 1 ? <span className="rs-fork__visit mono">visit {c.visit}</span> : <span />}
                    {c.exec.segment > 0 ? <span className="rs-fork__visit mono">restart {c.exec.segment}</span> : <span />}
                    <span className={cx("rs-led", `is-${status === "retry" ? "retry" : status}`)} title={status} />
                  </button>
                );
              })}
            </div>
          </Field>
          {picked ? (
            <p className="rs-fork__explain">
              Restores the state saved after <b>{label(restoredNode)}</b> <span className="mono faint">({picked.checkpoint})</span> and flies on from there: <b>{label(picked.exec.node)}</b> runs next.
            </p>
          ) : null}
          <Field label="Pipeline">
            <Segmented
              value={source}
              onChange={setSource}
              options={[
                { value: "flown", label: "As flown" },
                { value: "design", label: `Current design${designFile ? ` · ${designFile.split("/").pop()}` : ""}` },
              ]}
            />
          </Field>
          {source === "design" ? (
            <div className={cx("rs-fork__diff", (designMissing || designBroken) && "is-error")}>
              {designBroken
                ? "The current design has errors; fix them in Design first."
                : designMissing
                  ? `The current design has no stage “${restoredNode}”, so it can't continue from this snapshot.`
                  : diff
                    ? diff.total === 0
                      ? "The current design is identical to the flown pipeline."
                      : `Changes against the flown pipeline: ${describeDiff(diff)}.`
                    : null}
              {design.name !== view.name && !designMissing ? <div className="faint">Note: the design is “{design.name}”; this flight flew “{view.name}”.</div> : null}
            </div>
          ) : null}
          <Field label="Context overrides" hint={context.error ?? "Optional, one per line: key = value (JSON values allowed). Applied on top of the restored context."}>
            <TextArea value={contextText} onChange={setContextText} mono minRows={2} maxRows={6} placeholder={"ticket = ABC-123\nstrict = true"} />
          </Field>
        </>
      )}
    </Dialog>
  );
}
