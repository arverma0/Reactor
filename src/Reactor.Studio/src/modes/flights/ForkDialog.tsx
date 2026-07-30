import { useMemo, useState } from "react";
import { ApiError, api } from "../../api/client";
import type { ForkRestoreError, ForkWorkdirError, WorkspaceRestore, WorkspaceRestoreErrorCode } from "../../api/types";
import { Button, Dialog, Field, Segmented, TextArea, TextInput, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { describeDiff, diffDocs } from "../../model/diff";
import { findNode, kindOf, nodeLabel } from "../../model/doc";
import { writeDot } from "../../model/dot";
import type { StudioDoc } from "../../model/types";
import { toast, useStudio } from "../../state/store";
import { checkpointBefore, checkpointNode, type RunReplay, type StageExecution } from "./replay";
import { refreshRuns, rememberRunSource, selectRun, useFlights } from "./store";
import type { FlightView } from "./view";
import { describeForkRefusal, isAbsolutePath, planForkWorkdir, samePath } from "./workdir";

interface Candidate {
  exec: StageExecution;
  checkpoint: string;
  visit: number;
}

/** Where the fork runs: the flight's directory, the server's, or another one (servers with --allow-client-workdir). */
type WorkdirChoice = "flown" | "server" | "other";

const NO_CHECKPOINTS: string[] = [];

const NOTE_SPACING = { marginTop: 6 } as const;

const RESTORE_ERRORS: readonly WorkspaceRestoreErrorCode[] = ["rewind_unavailable", "workspace_snapshot_missing", "workspace_mismatch", "rewind_failed", "workspace_busy"];

/** The toast after a fork rewound its working directory: what changed, and how to get the previous files back. */
function describeRestore(restore: WorkspaceRestore, stage: string): string {
  const files = (n: number) => `${n} file${n === 1 ? "" : "s"}`;
  const changed = restore.written + restore.deleted === 0 ? "it already matched" : `${files(restore.written)} written, ${files(restore.deleted)} deleted`;
  return `Rewound the working directory to after ${stage} (${changed}). The previous state is commit ${restore.undo_commit.slice(0, 12)}; to get it back: ${restore.undo_command}`;
}

/** Stage executions a flight can be re-flown from: top-level stages with a snapshot saved before them. */
export function forkCandidates(view: FlightView, replay: RunReplay): Candidate[] {
  const visits = new Map<string, number>();
  const out: Candidate[] = [];
  for (const exec of replay.executions) {
    if (exec.attempt !== 1) continue;
    const key = `${exec.segment}:${exec.node}`;
    const visit = (visits.get(key) ?? 0) + 1;
    visits.set(key, visit);
    if (exec.parent !== undefined) continue;
    const checkpoint = checkpointBefore(exec, view.checkpoints);
    if (checkpoint) out.push({ exec, checkpoint, visit });
  }
  return out;
}

/** The candidate that re-runs `exec`: itself, or for a stage inside a parallel stage, that parallel stage. */
function candidateFor(candidates: Candidate[], exec: StageExecution): number {
  return candidates.findIndex((c) =>
    exec.parent === undefined ? c.exec === exec : c.exec.segment === exec.segment && c.exec.index === exec.parentIndex && c.exec.node === exec.parent,
  );
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
 * stages before it again. The fork continues in the flight's working directory unless the user picks another one (and
 * confirms it, since the flight's files may not be there). When the flight saved git snapshots of its working directory,
 * the fork can first rewind the directory to how it was at that point (the current state is snapshotted, so it can be
 * undone).
 */
export function ForkDialog({ view, replay, doc, initial, onClose }: { view: FlightView; replay: RunReplay; doc: StudioDoc | null; initial?: StageExecution; onClose: () => void }) {
  const design = useStudio((s) => s.doc);
  const designFile = useStudio((s) => s.file.path);
  const designBroken = useStudio((s) => !!s.analysis.parseError || s.analysis.diagnostics.some((d) => d.severity === "error"));
  const serverDir = useStudio((s) => s.info?.working_directory ?? "");
  const allowClient = useStudio((s) => s.info?.allow_client_working_directory ?? false);
  const workspaceCheckpoints = useFlights((s) => (view.dir ? s.history[view.dir]?.workspace_checkpoints : undefined)) ?? NO_CHECKPOINTS;
  const candidates = useMemo(() => forkCandidates(view, replay), [view, replay]);
  const [choice, setChoice] = useState(() => {
    const initialIndex = initial ? candidateFor(candidates, initial) : -1;
    if (initialIndex >= 0) return initialIndex;
    // The last failure is usually the one that decided the flight.
    const failed = candidates.findLastIndex((c) => c.exec.status === "fail" || !!c.exec.error);
    return failed >= 0 ? failed : Math.max(0, candidates.length - 1);
  });
  const [source, setSource] = useState<"flown" | "design">("flown");
  const [contextText, setContextText] = useState("");
  const [busy, setBusy] = useState(false);
  const flownDir = view.workingDirectory;
  const [workdirChoice, setWorkdirChoice] = useState<WorkdirChoice>(flownDir ? "flown" : "server");
  const [otherDir, setOtherDir] = useState("");
  /** The directory the user confirmed re-flying in, though it is not the flight's. */
  const [confirmedFor, setConfirmedFor] = useState<string | null>(null);
  const [workdirError, setWorkdirError] = useState<ForkWorkdirError | null>(null);
  const [restore, setRestore] = useState(false);
  const [restoreError, setRestoreError] = useState<ForkRestoreError | null>(null);
  const picked = candidates[choice];
  const restoredNode = picked ? checkpointNode(picked.checkpoint) : "";
  const restorable = !!picked && workspaceCheckpoints.includes(picked.checkpoint);
  const restoring = restore && restorable;
  const diff = useMemo(() => (doc ? diffDocs(doc, design) : null), [doc, design]);
  const context = parseContext(contextText);
  const designMissing = source === "design" && !!picked && !design.nodes.some((n) => n.id === restoredNode);
  const override = workdirChoice === "server" ? (flownDir ? serverDir : null) : workdirChoice === "other" ? otherDir : null;
  const workdir = planForkWorkdir({ source: flownDir, server: serverDir, allowClient, override });
  const otherInvalid = workdirChoice === "other" && !isAbsolutePath(otherDir);
  // The server has the last word on "another directory" (for example through links the Studio cannot see).
  const needsConfirmation =
    workdir.needsConfirmation || (workdirError?.code === "working_directory_confirmation_required" && samePath(workdirError.working_directory ?? "", workdir.directory));
  const confirmed = confirmedFor !== null && samePath(confirmedFor, workdir.directory);
  const workdirReady = !workdir.blocked && !otherInvalid && (!needsConfirmation || confirmed);
  const workdirOptions: { value: WorkdirChoice; label: string; title?: string }[] = [
    ...(flownDir ? [{ value: "flown" as const, label: "Flight's directory", title: flownDir }] : []),
    ...(!flownDir || !samePath(flownDir, serverDir) ? [{ value: "server" as const, label: "Server's directory", title: serverDir }] : []),
    ...(allowClient ? [{ value: "other" as const, label: "Other…" }] : []),
  ];
  const chooseWorkdir = (value: WorkdirChoice) => {
    setWorkdirChoice(value);
    setWorkdirError(null);
    setRestoreError(null);
  };
  const label = (id: string) => {
    const node = doc ? findNode(doc, id) : undefined;
    return node && doc ? nodeLabel(doc, node) : id;
  };

  const submit = async () => {
    if (!picked || !view.dir) return;
    setBusy(true);
    setWorkdirError(null);
    setRestoreError(null);
    try {
      const dot = source === "design" ? writeDot(design) : undefined;
      const response = await api.fork(view.dir, {
        checkpoint: picked.checkpoint,
        dot,
        context: context.value && Object.keys(context.value).length > 0 ? context.value : undefined,
        working_directory: workdir.origin === "request" ? workdir.directory : undefined,
        confirm_working_directory: needsConfirmation && confirmed ? true : undefined,
        restore_workspace: restoring ? true : undefined,
      });
      if (source === "design") rememberRunSource(response.id, { dot: dot!, layout: design.layout, sourcePath: designFile });
      else if (doc) rememberRunSource(response.id, { dot: writeDot(doc), layout: doc.layout });
      await refreshRuns();
      selectRun(response.id);
      toast(`Re-flying from just before ${label(picked.exec.node)} in ${response.working_directory ?? workdir.directory}.`, "success");
      if (response.workspace_restore) toast(describeRestore(response.workspace_restore, label(restoredNode)), "info", undefined, 20000);
      if (response.working_directory_note) toast(response.working_directory_note, "info", undefined, 9000);
      onClose();
    } catch (error) {
      const body = error instanceof ApiError ? (error.body as Partial<ForkWorkdirError | ForkRestoreError> | null) : null;
      const code = body && typeof body === "object" && typeof body.code === "string" ? body.code : "";
      if (code.startsWith("working_directory_")) setWorkdirError(body as ForkWorkdirError);
      else if ((RESTORE_ERRORS as readonly string[]).includes(code)) setRestoreError(body as ForkRestoreError);
      else toast(`Could not re-fly: ${(error as Error).message}`, "error");
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
          <span className="rs-fork__foot faint">
            {restoring
              ? "The working directory is rewound first; its current state is snapshotted, so the rewind can be undone."
              : "The working directory is not rewound: files written since stay as they are."}
          </span>
          <Button variant="quiet" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            icon="fork"
            disabled={!picked || busy || designMissing || !!context.error || (source === "design" && designBroken) || !workdirReady}
            onClick={() => void submit()}
          >
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
                  <button
                    key={`${c.exec.index}-${c.exec.segment}`}
                    type="button"
                    className={cx("rs-fork__row", i === choice && "is-on")}
                    onClick={() => {
                      setChoice(i);
                      setRestoreError(null);
                    }}
                  >
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
              {initial?.parent !== undefined && choice === candidateFor(candidates, initial) ? (
                <>
                  {" "}
                  <b>{label(initial.node)}</b> ran inside it, so the whole parallel stage runs again.
                </>
              ) : null}
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
          <Field
            label="Working directory"
            hint={
              workdir.origin === "source_run"
                ? "Where this flight worked: its agents and tools carry on there."
                : workdir.origin === "server"
                  ? "The server's working directory."
                  : workdirChoice === "other"
                    ? "A full path on the server. Agents and tools run there."
                    : "Agents and tools run in the server's working directory."
            }
          >
            {workdirOptions.length > 1 ? <Segmented value={workdirChoice} onChange={chooseWorkdir} options={workdirOptions} /> : null}
            {workdirChoice === "other" ? (
              <TextInput
                className="rs-fork__workdir-input"
                value={otherDir}
                onChange={(value) => {
                  setOtherDir(value);
                  setWorkdirError(null);
                }}
                mono
                invalid={otherDir.trim() !== "" && otherInvalid}
                placeholder={flownDir ?? serverDir}
              />
            ) : (
              <div className="rs-fork__workdir mono" title={workdir.directory}>
                <Glyph name="files" size={12} />
                <span>{workdir.directory}</span>
              </div>
            )}
          </Field>
          {workdir.blocked || workdir.note || workdirError || (needsConfirmation && !otherInvalid) ? (
            <div className={cx("rs-fork__diff is-workdir", (workdir.blocked || workdirError) && "is-error")}>
              {workdir.blocked ? <div>{workdir.blocked}</div> : null}
              {workdirError && !workdir.blocked ? <div>{describeForkRefusal(workdirError, { source: flownDir, server: serverDir, allowClient })}</div> : null}
              {workdir.note ? <div>{workdir.note}</div> : null}
              {needsConfirmation && !workdir.blocked && !otherInvalid ? (
                <label className="rs-fork__confirm">
                  <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmedFor(e.target.checked ? workdir.directory : null)} />
                  <span>
                    Re-fly in this directory anyway. The flight worked in <span className="mono">{flownDir ?? workdirError?.source_working_directory}</span>, and the files it
                    worked on may not be here.
                  </span>
                </label>
              ) : null}
            </div>
          ) : null}
          {restorable && picked ? (
            <div className={cx("rs-fork__diff is-workdir", restoreError && "is-error")}>
              <label className="rs-fork__confirm">
                <input
                  type="checkbox"
                  checked={restore}
                  onChange={(e) => {
                    setRestore(e.target.checked);
                    setRestoreError(null);
                  }}
                />
                <span>
                  Restore the working directory to how it was after <b>{label(restoredNode)}</b>
                </span>
              </label>
              {restore ? (
                <div className="faint" style={NOTE_SPACING}>
                  Files git tracks or would track are overwritten to match the snapshot saved with <span className="mono">{picked.checkpoint}</span>, and files it does not have
                  are deleted; ignored files (such as <span className="mono">node_modules</span>) stay. The current state is snapshotted first, and the toast after starting gives
                  its commit id to get it back. Your git index, HEAD, and branches are not touched.
                </div>
              ) : null}
              {restore && workdirChoice !== "flown" && !restoreError ? (
                <div className="faint" style={NOTE_SPACING}>
                  This only works in the flight's repository, at the same subdirectory.
                </div>
              ) : null}
              {restoreError ? <div style={NOTE_SPACING}>{restoreError.error}</div> : null}
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
