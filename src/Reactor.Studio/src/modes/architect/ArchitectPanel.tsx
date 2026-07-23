import { useEffect, useMemo, useState } from "react";
import { Elapsed } from "../../canvas/Elapsed";
import { Button, Dropdown, Segmented, Spinner, TextArea, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { Markdown } from "../../components/Markdown";
import type { DocDiff } from "../../model/diff";
import { findNode, nodeLabel } from "../../model/doc";
import type { StudioDoc } from "../../model/types";
import { useStudio } from "../../state/store";
import { pct, useTelemetry } from "../../state/telemetry";
import type { ForecastCompare, ForecastSide } from "./forecast";
import {
  acceptProposal,
  cancelArchitect,
  discardProposal,
  gatherEvidence,
  setBrief,
  startArchitect,
  togglePreview,
  updateArchitect,
  useArchitect,
  watchArchitectFlight,
  type ArchitectMode,
} from "./store";

const MODES: { value: ArchitectMode; label: string; title: string }[] = [
  { value: "draft", label: "Draft new", title: "Design a new pipeline from a description" },
  { value: "revise", label: "Revise", title: "Change the open pipeline as you describe" },
  { value: "critique", label: "Critique", title: "Find what to fix using real flights and the wind tunnel, and propose it" },
];

const PLACEHOLDER: Record<ArchitectMode, string> = {
  draft: "Describe the factory: what goes in, what comes out, and what “done” means.\n\ne.g. Take a GitHub issue, plan the fix, implement it with tests, and have a reviewer approve before a person merges.",
  revise: "What should change?\n\ne.g. Add a security review in parallel with the code review, and use a faster model for fixes.",
  critique: "Optional focus.\n\ne.g. Why do so many flights stall at the tests, and how do we cut cost per flight?",
};

const ACTION: Record<ArchitectMode, string> = { draft: "Draft the pipeline", revise: "Revise the pipeline", critique: "Critique and propose" };

const NO_MODELS: never[] = [];

function Evidence({ doc }: { doc: StudioDoc }) {
  const record = useTelemetry((s) => s.byName[doc.name]);
  const [preview, setPreview] = useState<{ text: string; flights: number; simulated: number } | null>(null);
  const evidence = useArchitect((s) => s.evidence);
  useEffect(() => {
    let cancelled = false;
    void gatherEvidence(doc).then((g) => !cancelled && setPreview(g));
    return () => {
      cancelled = true;
    };
  }, [doc, record]);
  const nothing = preview !== null && preview.flights === 0 && preview.simulated === 0;
  return (
    <div className={cx("rs-architect__evidence", !evidence && "is-off")}>
      <label className="rs-architect__check">
        <input type="checkbox" checked={evidence && !nothing} disabled={nothing} onChange={(e) => updateArchitect({ evidence: e.target.checked })} />
        <span>Give it the evidence</span>
      </label>
      <div className="rs-architect__sources">
        <span className={cx("rs-chip", preview?.flights ? "is-on" : "is-off")} title="Telemetry of the pipeline's finished real flights">
          <Glyph name="gauge" size={11} /> {preview?.flights ? `${preview.flights} real flights` : "no flight record"}
        </span>
        <span className={cx("rs-chip", preview?.simulated ? "is-on" : "is-off")} title="The latest wind tunnel report for this exact design">
          <Glyph name="tunnel" size={11} /> {preview?.simulated ? `wind tunnel · ${preview.simulated.toLocaleString()}` : "no current wind tunnel run"}
        </span>
      </div>
    </div>
  );
}

function Forecast({ forecast }: { forecast: ForecastCompare }) {
  const rows: { label: string; now?: string; next: string; delta?: number; better?: "up" | "down"; format: (d: number) => string }[] = [];
  const side = (s: ForecastSide | null | undefined, f: (x: ForecastSide) => number | undefined) => (s ? f(s) : undefined);
  const landed = { now: side(forecast.current, (s) => s.rate), next: forecast.proposal.rate };
  rows.push({ label: "lands", now: landed.now !== undefined ? pct(landed.now) : undefined, next: pct(landed.next), delta: landed.now !== undefined ? landed.next - landed.now : undefined, better: "up", format: (d) => `${d > 0 ? "+" : "−"}${Math.abs(d * 100).toFixed(0)} pts` });
  if (forecast.proposal.cost !== undefined) {
    const now = side(forecast.current, (s) => s.cost);
    rows.push({ label: "AIU / flight", now: now?.toFixed(1), next: forecast.proposal.cost.toFixed(1), delta: now !== undefined ? forecast.proposal.cost - now : undefined, better: "down", format: (d) => `${d > 0 ? "+" : "−"}${Math.abs(d).toFixed(1)}` });
  }
  const steps = side(forecast.current, (s) => s.steps);
  rows.push({ label: "stages / flight", now: steps?.toFixed(1), next: forecast.proposal.steps.toFixed(1), delta: steps !== undefined ? forecast.proposal.steps - steps : undefined, better: "down", format: (d) => `${d > 0 ? "+" : "−"}${Math.abs(d).toFixed(1)}` });
  return (
    <div className="rs-forecast">
      <div className="rs-forecast__head label">
        <Glyph name="tunnel" size={11} /> Forecast · {forecast.basis === "calibrated" ? `calibrated to ${forecast.flights} real flight${forecast.flights === 1 ? "" : "s"}` : "realistic scenario"}
      </div>
      <div className="rs-forecast__grid">
        <span />
        <span className="rs-forecast__col">{forecast.current ? "now" : ""}</span>
        <span />
        <span className="rs-forecast__col">proposal</span>
        <span />
        {rows.map((r) => {
          const good = r.delta === undefined || Math.abs(r.delta) < 1e-9 ? null : (r.delta > 0) === (r.better === "up");
          return (
            <div key={r.label} className="rs-forecast__row">
              <span className="rs-forecast__label">{r.label}</span>
              <span className="num faint">{r.now ?? ""}</span>
              <span className="rs-forecast__arrow">{r.now !== undefined ? "→" : ""}</span>
              <span className="num">{r.next}</span>
              <span className={cx("num rs-forecast__delta", good === true && "is-good", good === false && "is-bad")}>{r.delta !== undefined && Math.abs(r.delta) > 1e-9 ? r.format(r.delta) : r.now !== undefined ? "±0" : ""}</span>
            </div>
          );
        })}
      </div>
      <p className="faint">
        {forecast.currentError ? `The current design could not be flown (${forecast.currentError}), so there is nothing to compare against. ` : ""}
        {forecast.runs.toLocaleString()} simulated flights each{forecast.current ? ", same dice for both designs, so the difference is the design" : ""}.
      </p>
    </div>
  );
}

function Changes({ diff, base, proposal }: { diff: DocDiff; base: StudioDoc; proposal: StudioDoc }) {
  const label = (doc: StudioDoc, id: string) => {
    const node = findNode(doc, id);
    return node ? nodeLabel(doc, node) : id;
  };
  if (diff.total === 0) return <p className="faint rs-proposal__nochange">No changes to the pipeline.</p>;
  const routes = diff.edgesAdded.length + diff.edgesRemoved.length + diff.edgesChanged.length;
  return (
    <div className="rs-changes">
      <div className="label">Changes</div>
      {diff.added.map((id) => (
        <div key={`a${id}`} className="rs-change is-added">
          <span className="rs-change__mark">+</span>
          <span>{label(proposal, id)}</span>
          <span className="mono faint">{id}</span>
        </div>
      ))}
      {diff.changed.map((c) => (
        <div key={`c${c.id}`} className="rs-change is-changed">
          <span className="rs-change__mark">~</span>
          <span>{label(proposal, c.id)}</span>
          <span className="mono faint">{c.changes.map((x) => x.key).join(", ")}</span>
        </div>
      ))}
      {diff.removed.map((id) => (
        <div key={`r${id}`} className="rs-change is-removed">
          <span className="rs-change__mark">−</span>
          <span>{label(base, id)}</span>
          <span className="mono faint">{id}</span>
        </div>
      ))}
      {routes > 0 || diff.graph.length > 0 ? (
        <div className="rs-change is-routes faint">
          {routes > 0 ? `${routes} route${routes === 1 ? "" : "s"} changed` : ""}
          {routes > 0 && diff.graph.length > 0 ? " · " : ""}
          {diff.graph.length > 0 ? `graph: ${diff.graph.map((g) => g.key).join(", ")}` : ""}
        </div>
      ) : null}
    </div>
  );
}

function Job() {
  const job = useArchitect((s) => s.job)!;
  const proposalDoc = useArchitect((s) => s.proposalDoc);
  const diff = useArchitect((s) => s.diff);
  const forecast = useArchitect((s) => s.forecast);
  const preview = useArchitect((s) => s.preview);
  const confirmReplace = useArchitect((s) => s.confirmReplace);
  const fileName = useStudio((s) => s.file.path?.split("/").pop() ?? `${s.doc.name}.dot`);
  const state = job.state;
  const running = !state || !["success", "fail", "cancelled", "error"].includes(state.status);
  const attempt = state ? (running ? state.attempt + 1 : state.attempt) : 1;
  const proposal = state?.proposal;
  const serverForecast = proposal?.forecast;
  return (
    <>
      <section className="rs-architect__job">
        <div className="rs-architect__jobhead">
          <span className="label">{running ? "Working" : state?.status === "success" ? "Done" : state?.status === "cancelled" ? "Cancelled" : "Stopped"}</span>
          <button type="button" className="rs-linkbtn" onClick={watchArchitectFlight} title="The Architect is itself a Reactor pipeline: watch its flight">
            watch its flight →
          </button>
        </div>
        <ol className="rs-drafts">
          {job.drafts.map((d) => (
            <li key={d.attempt} className={cx("rs-draft", d.passed ? "is-passed" : "is-failed")}>
              <span className="rs-draft__mark">{d.passed ? <Glyph name="check" size={12} /> : <Glyph name="cross" size={12} />}</span>
              <span className="rs-draft__title">Draft {d.attempt}</span>
              <span className="rs-draft__text">{d.passed ? "passed the checks" : `${d.problems.length} finding${d.problems.length === 1 ? "" : "s"}: ${d.problems[0] ?? ""}`}</span>
            </li>
          ))}
          {running ? (
            <li className="rs-draft is-active">
              <span className="rs-draft__mark">
                <Spinner size={11} />
              </span>
              <span className="rs-draft__title">Draft {Math.max(1, attempt)}</span>
              <span className="rs-draft__text">{state?.current_node === "check" ? "flying it through the wind tunnel…" : job.drafts.length > 0 ? "repairing from the findings…" : "designing…"}</span>
              <Elapsed since={new Date(job.startedAt).toISOString()} className="num rs-draft__time" />
            </li>
          ) : null}
        </ol>
        {running ? (
          <Button size="sm" variant="quiet" icon="stop" onClick={() => void cancelArchitect()}>
            Stop
          </Button>
        ) : null}
        {!running && state?.error && !proposal?.valid ? <div className="rs-architect__error">{state.error}</div> : null}
      </section>

      {proposalDoc && proposal ? (
        <section className={cx("rs-proposal", !proposal.valid && "is-invalid")}>
          <header className="rs-proposal__head">
            <span className="label">{job.mode === "critique" ? "Critique & proposal" : "Proposal"}</span>
            <span className={cx("rs-proposal__verdict", proposal.valid ? "is-valid" : "is-invalid")}>
              <Glyph name={proposal.valid ? "check" : "cross"} size={11} /> {proposal.valid ? "passed the checks" : "failed the checks"}
            </span>
          </header>
          <div className="rs-proposal__facts num">
            {proposalDoc.nodes.length} stages · {proposalDoc.edges.length} routes
            {serverForecast ? ` · lands ${pct(serverForecast.happy_path)} when every stage succeeds` : ""}
          </div>
          {forecast === "loading" ? (
            <div className="rs-forecast is-loading">
              <Spinner size={12} /> <span className="faint">forecasting in the wind tunnel…</span>
            </div>
          ) : forecast && forecast !== "error" ? (
            <Forecast forecast={forecast} />
          ) : null}
          {diff && job.base ? <Changes diff={diff} base={job.base} proposal={proposalDoc} /> : null}
          {state?.rationale ? <Markdown text={state.rationale} className="rs-proposal__rationale" /> : null}
          {proposal.problems.length > 0 ? (
            <div className="rs-proposal__issues is-problems">
              {proposal.problems.map((p, i) => (
                <div key={i}>{p}</div>
              ))}
            </div>
          ) : null}
          {proposal.warnings.length > 0 ? (
            <div className="rs-proposal__issues">
              {proposal.warnings.map((w, i) => (
                <div key={i}>{w}</div>
              ))}
            </div>
          ) : null}
          <div className="rs-proposal__actions">
            <Button icon="eye" active={preview} onClick={() => togglePreview()}>
              {preview ? "Back to design" : "Preview"}
            </Button>
            <Button variant="primary" icon="check" disabled={!proposal.valid} onClick={acceptProposal}>
              {job.mode === "draft" ? (confirmReplace ? `Discard changes to ${fileName}?` : "Open as new pipeline") : "Apply to design"}
            </Button>
            <Button variant="quiet" onClick={discardProposal}>
              Discard
            </Button>
          </div>
        </section>
      ) : null}
    </>
  );
}

/** The Architect: a brief in, a pipeline out — checked by the parser, the validator, and the wind tunnel before you see it. */
export function ArchitectPanel() {
  const mode = useArchitect((s) => s.mode);
  const brief = useArchitect((s) => s.brief[s.mode]);
  const model = useArchitect((s) => s.model);
  const effort = useArchitect((s) => s.effort);
  const job = useArchitect((s) => s.job);
  const starting = useArchitect((s) => s.starting);
  const doc = useStudio((s) => s.doc);
  const info = useStudio((s) => s.info);
  const models = useStudio((s) => s.models) ?? NO_MODELS;
  const simulateOnly = info?.backend.kind === "simulate";
  const running = !!job && (!job.state || !["success", "fail", "cancelled", "error"].includes(job.state.status));
  const options = useMemo(
    () =>
      models
        .filter((m) => m.available)
        .map((m) => ({ value: m.id, label: m.display_name ?? m.id, text: `${m.id} ${m.display_name ?? ""}`, detail: m.billing_multiplier != null ? `${m.billing_multiplier}×` : undefined, group: m.provider })),
    [models],
  );
  const empty = doc.nodes.length <= 2;
  const canStart = !running && !starting && !simulateOnly && (mode === "critique" || brief.trim().length > 0) && (mode === "draft" || !empty);

  return (
    <div className="rs-architect">
      <header className="rs-architect__head">
        <div className="rs-architect__mark">
          <Glyph name="architect" size={18} />
        </div>
        <div>
          <div className="rs-architect__title">Architect</div>
          <div className="rs-architect__sub">Designs the factory, then proves it in the wind tunnel.</div>
        </div>
        <Button variant="ghost" size="sm" icon="close" title="Close" onClick={() => useStudio.setState({ architectOpen: false })} />
      </header>
      <div className="rs-panel-scroll">
        <section className="rs-architect__compose">
          <Segmented value={mode} onChange={(m) => updateArchitect({ mode: m })} options={MODES} />
          <TextArea value={brief} onChange={(t) => setBrief(mode, t)} placeholder={PLACEHOLDER[mode]} minRows={5} maxRows={14} />
          {mode !== "draft" ? <Evidence doc={doc} /> : null}
          <div className="rs-architect__model">
            <span className="label">Model</span>
            <Dropdown
              value={model ?? undefined}
              options={options}
              onChange={(v) => updateArchitect({ model: v ?? null })}
              placeholder="backend default"
              clearLabel="backend default"
              searchable
              width={300}
            />
            {model ? (
              <Segmented
                size="sm"
                value={effort}
                onChange={(e) => updateArchitect({ effort: e })}
                options={["low", "medium", "high"].map((e) => ({ value: e, label: e }))}
              />
            ) : null}
          </div>
          {simulateOnly ? (
            <div className="rs-architect__note">
              This server simulates stages, so there is no model to design with. Restart it with a real backend, e.g. <span className="mono">reactor studio --backend copilot</span>.
            </div>
          ) : mode !== "draft" && empty ? (
            <div className="rs-architect__note">Open or build a pipeline first; there is nothing to {mode} yet.</div>
          ) : null}
          <Button variant="primary" icon="architect" disabled={!canStart} onClick={() => void startArchitect()}>
            {starting ? "Starting…" : ACTION[mode]}
          </Button>
        </section>
        {job ? (
          <Job />
        ) : (
          <section className="rs-architect__intro">
            <p className="serif">Every draft is flown before you see it.</p>
            <p>
              The Architect is itself a Reactor pipeline. It drafts with your backend, then Reactor's own parser, validator, and wind tunnel check the
              draft; findings go back for repair, up to three drafts. You get a design that lands, with a forecast against what you fly today.
            </p>
          </section>
        )}
      </div>
    </div>
  );
}
