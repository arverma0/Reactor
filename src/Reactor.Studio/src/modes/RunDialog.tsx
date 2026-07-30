import { useMemo, useState } from "react";
import { api } from "../api/client";
import { Button, Dialog, Field, Segmented, TextArea, TextInput, cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { parseUsd } from "../model/attributes";
import { writeDot } from "../model/dot";
import { setMode, toast, useStudio } from "../state/store";
import { refreshRuns, rememberRunSource, selectRun } from "./flights/store";

type LaunchMode = "real" | "stochastic" | "interactive";

/** Launches the open pipeline: a real run on the server's backend, or a simulated one. */
export function RunDialog() {
  const open = useStudio((s) => s.runOpen);
  if (!open) return null;
  return <RunDialogBody onClose={() => useStudio.setState({ runOpen: false })} />;
}

function RunDialogBody({ onClose }: { onClose: () => void }) {
  const doc = useStudio((s) => s.doc);
  const info = useStudio((s) => s.info);
  const file = useStudio((s) => s.file);
  const analysis = useStudio((s) => s.analysis);
  const [goal, setGoal] = useState(doc.graphAttrs.goal ?? "");
  const [rows, setRows] = useState<{ key: string; value: string }[]>([]);
  const simulation = info?.features.includes("simulation") ?? false;
  const [mode, setLaunchMode] = useState<LaunchMode>("real");
  const [busy, setBusy] = useState(false);
  const [budget, setBudget] = useState("");
  const budgetUsd = budget.trim() ? parseUsd(budget) : null;
  const budgetInvalid = mode === "real" && !!budget.trim() && budgetUsd === null;
  const errors = analysis.diagnostics.filter((d) => d.severity === "error");
  const blocked = !!analysis.parseError || errors.length > 0;

  const context = useMemo(() => {
    const result: Record<string, unknown> = {};
    for (const row of rows) {
      if (!row.key.trim()) continue;
      const v = row.value.trim();
      try {
        result[row.key.trim()] = /^[[{"\d-]|^(true|false|null)$/.test(v) ? JSON.parse(v) : v;
      } catch {
        result[row.key.trim()] = v;
      }
    }
    return result;
  }, [rows]);

  const launch = async () => {
    setBusy(true);
    try {
      const runDoc = goal !== (doc.graphAttrs.goal ?? "") ? { ...doc, graphAttrs: { ...doc.graphAttrs, goal } } : doc;
      const dot = writeDot(runDoc);
      const started = await api.startRun({
        dot,
        context: Object.keys(context).length ? context : undefined,
        studio: { source_path: file.path, layout: runDoc.layout },
        simulation: mode === "real" ? undefined : { mode },
        max_cost: mode === "real" && budgetUsd !== null ? budgetUsd : undefined,
      });
      rememberRunSource(started.id, { dot, layout: runDoc.layout, sourcePath: file.path });
      onClose();
      setMode("flights");
      await refreshRuns();
      selectRun(started.id);
      toast(mode === "real" ? "Launched. Watching it fly." : mode === "interactive" ? "Walkthrough started — choose each stage's outcome on the canvas." : "Simulated flight launched.", "success", undefined, 2600);
    } catch (error) {
      toast(`Could not start: ${(error as Error).message}`, "error");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      kicker="Launch"
      title={doc.graphAttrs.label || doc.name}
      onClose={onClose}
      width={600}
      footer={
        <>
          <span className="rs-run__backend">
            <Glyph name="bolt" size={13} />
            {mode === "real" ? info?.backend.description ?? "server backend" : "simulated stages — no LLM calls, no tool commands"}
          </span>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" icon="play" onClick={() => void launch()} disabled={busy || blocked || budgetInvalid}>
            {mode === "real" ? "Run" : mode === "interactive" ? "Walk through" : "Simulate"}
          </Button>
        </>
      }
    >
      {blocked ? (
        <div className="rs-run__blocked">
          <Glyph name="problems" size={14} /> {analysis.parseError ? "The pipeline has a syntax error." : `${errors.length} validation error${errors.length > 1 ? "s" : ""} must be fixed first: ${errors[0].message}`}
        </div>
      ) : null}
      <Field label="Mode">
        <Segmented
          value={mode}
          onChange={setLaunchMode}
          options={[
            { value: "real", label: "Real run" },
            { value: "stochastic", label: "Simulated flight", title: simulation ? "Stages succeed or fail at simulated odds; animated live" : "Needs a server with simulation support" },
            { value: "interactive", label: "Walkthrough", title: simulation ? "You choose every stage's outcome" : "Needs a server with simulation support" },
          ]}
        />
      </Field>
      {mode !== "real" && !simulation ? <p className="rs-run__note">This server does not support simulated runs yet.</p> : null}
      <div style={{ height: 14 }} />
      <Field label="Goal for this run" hint="$goal in prompts expands to this. Changing it here does not edit the pipeline.">
        <TextArea value={goal} onChange={setGoal} minRows={2} />
      </Field>
      {mode === "real" ? (
        <Field
          label="Budget for this run (US dollars)"
          hint={
            budget && budgetUsd === null
              ? "A non-negative number of US dollars, e.g. 25 or 7.5."
              : "The run stops, resumably, before its next stage once its recorded cost reaches this; resume it with a higher budget to go on. Empty: the server's default, else the pipeline's max_cost."
          }
        >
          <TextInput value={budget} onChange={setBudget} mono placeholder={doc.graphAttrs.max_cost ? `pipeline · $${doc.graphAttrs.max_cost.replace(/^\$/, "")}` : "no limit"} invalid={!!budget && budgetUsd === null} />
        </Field>
      ) : null}
      <Field label="Context" hint="Seed values into the run context (like reactor run --set key=value). JSON-looking values are parsed.">
        <div className="rs-run__ctx">
          {rows.map((row, i) => (
            <div key={i} className="rs-run__ctxrow">
              <TextInput value={row.key} mono placeholder="key" onChange={(v) => setRows(rows.map((r, j) => (j === i ? { ...r, key: v } : r)))} />
              <TextInput value={row.value} mono placeholder="value" onChange={(v) => setRows(rows.map((r, j) => (j === i ? { ...r, value: v } : r)))} />
              <Button size="sm" variant="ghost" icon="close" onClick={() => setRows(rows.filter((_, j) => j !== i))} />
            </div>
          ))}
          <button type="button" className={cx("rs-linkbtn")} onClick={() => setRows([...rows, { key: "", value: "" }])}>
            + add a context value
          </button>
        </div>
      </Field>
    </Dialog>
  );
}
