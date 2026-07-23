import { useEffect, useMemo } from "react";
import { Canvas } from "../../canvas/Canvas";
import { edgeKey, type CanvasOverlay, type EdgeOverlay, type NodeOverlay } from "../../canvas/build";
import { formatDuration } from "../../canvas/Elapsed";
import { Button, Empty, Segmented, Spinner, Stepper, cx } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { findNode, handlerOf, kindOf, nodeLabel } from "../../model/doc";
import { writeDot } from "../../model/dot";
import { KINDS } from "../../model/handlers";
import type { DocNode, StudioDoc } from "../../model/types";
import { api } from "../../api/client";
import { setMode, toast, useStudio } from "../../state/store";
import { refreshRuns, rememberRunSource, selectRun } from "../flights/store";
import { digest, overlayAt } from "../flights/replay";
import { OUTCOME_KINDS, PRESETS, buildScenario, gateChoices, predictedCost, routeLabels, type Behavior, type Calibration, type Preset, type SimReport } from "./scenario";
import { applyCalibration, closeTrace, focusTunnelNode, loadSettingsForDoc, playTrace, runTunnel, setTraceCursor, toggleTracePlaying, updateSettings, useTunnel } from "./store";
import { loadTelemetry, useTelemetry } from "../../state/telemetry";

const pct = (n: number, digits = 0) => `${(n * 100).toFixed(digits)}%`;

function reportOverlay(doc: StudioDoc, report: SimReport): CanvasOverlay {
  const nodes: Record<string, NodeOverlay> = {};
  const maxVisits = Math.max(1, ...Object.values(report.nodes).map((n) => n.mean_visits));
  for (const node of doc.nodes) {
    const stats = report.nodes[node.id];
    if (!stats || stats.visits === 0) {
      nodes[node.id] = { state: "dim" };
      continue;
    }
    const fails = stats.outcomes.fail ?? 0;
    const failRate = fails / stats.visits;
    const reach = stats.runs / report.runs;
    const readouts: NonNullable<NodeOverlay["readouts"]> = [{ label: "reached", value: pct(reach) }];
    if (stats.mean_visits >= 1.05) readouts.push({ label: "visits/run", value: stats.mean_visits.toFixed(1), tone: stats.mean_visits > 3 ? "warn" : undefined });
    if (failRate > 0) readouts.push({ label: "fails", value: pct(failRate), tone: "fail" });
    if (stats.retries > 0) readouts.push({ label: "retries", value: String(stats.retries), tone: "warn" });
    nodes[node.id] = {
      state: "visited",
      badge: stats.mean_visits >= 1.05 ? `×${stats.mean_visits.toFixed(1)}` : undefined,
      heat: stats.mean_visits / maxVisits,
      heatLabel: `${stats.mean_visits.toFixed(2)} visits per flight`,
      readouts,
    };
  }
  const edges: Record<string, EdgeOverlay> = {};
  for (const edge of report.edges) {
    if (edge.kind !== "edge") continue;
    const share = edge.runs / report.runs;
    const perRun = edge.count / Math.max(1, edge.runs);
    edges[edgeKey(edge.from, edge.to)] = { state: "taken", flow: share, text: `${pct(share)}${perRun >= 1.1 ? ` ·×${perRun.toFixed(1)}` : ""}` };
  }
  return { kind: "sim", nodes, edges };
}

export function TunnelMode() {
  const doc = useStudio((s) => s.doc);
  const features = useStudio((s) => s.info?.features ?? []);
  const report = useTunnel((s) => s.report);
  const reportDot = useTunnel((s) => s.reportDot);
  const trace = useTunnel((s) => s.trace);
  const running = useTunnel((s) => s.running);
  const focus = useTunnel((s) => s.focus);
  const supported = features.includes("simulation");

  useEffect(() => {
    loadSettingsForDoc();
  }, [doc.name]);

  const stale = report !== null && reportDot !== writeDot(doc);

  const overlay = useMemo<CanvasOverlay | null>(() => {
    if (trace) {
      const played = trace.events.slice(0, trace.cursor);
      const replay = digest(played);
      const last = played[played.length - 1];
      const now = last ? Date.parse(last.timestamp) + 1 : Date.now();
      return { ...overlayAt(replay, { at: null, now, allNodes: doc.nodes.map((n) => n.id) }), kind: "live" };
    }
    return report ? reportOverlay(doc, report) : { kind: "sim", nodes: {}, edges: {} };
  }, [trace, report, doc]);

  return (
    <div className="rs-tunnel">
      <aside className="rs-tunnel__scenario">
        <ScenarioPanel doc={doc} focus={focus} />
      </aside>
      <section className="rs-tunnel__main">
        <header className="rs-tunnel__bar">
          <span className="rs-tunnel__title">
            <Glyph name="tunnel" size={15} /> Wind tunnel
          </span>
          <span className="rs-tunnel__sub faint">real routing · simulated stages · zero tokens</span>
          <span className="rs-flights__spacer" />
          {stale ? <span className="rs-tunnel__stale">pipeline changed since this run</span> : null}
          <Button variant="quiet" icon="human" disabled={!supported} onClick={() => void startWalkthrough(doc)} title="You decide every stage's outcome, on the canvas">
            Walk through
          </Button>
          <Button variant="quiet" icon="play" disabled={!supported} onClick={() => void playTrace("A sample flight", Math.floor(Math.random() * 1e9))} title="Animate one simulated flight">
            Animate one
          </Button>
          <Button variant="primary" icon="tunnel" disabled={!supported || running} onClick={() => void runTunnel()}>
            {running ? "Flying…" : `Fly ${useTunnel.getState().settings.runs.toLocaleString()}×`}
          </Button>
        </header>
        <div className="rs-tunnel__canvas">
          {!supported ? (
            <div className="rs-tunnel__unsupported">
              <Empty title="This server can't simulate yet." icon="tunnel">
                Update Reactor: the wind tunnel needs the simulation engine on the server.
              </Empty>
            </div>
          ) : null}
          <Canvas doc={doc} overlay={overlay} selectedNode={focus} onSelectNode={focusTunnelNode} />
          {trace ? <TraceBar /> : null}
        </div>
      </section>
      <aside className="rs-tunnel__results">
        <ResultsPanel doc={doc} />
      </aside>
    </div>
  );
}

async function startWalkthrough(doc: StudioDoc) {
  const settings = useTunnel.getState().settings;
  try {
    const dot = writeDot(doc);
    const started = await api.startRun({ dot, studio: { source_path: useStudio.getState().file.path, layout: doc.layout }, simulation: { mode: "interactive", scenario: buildScenario(doc, settings) } });
    rememberRunSource(started.id, { dot, layout: doc.layout });
    setMode("flights");
    await refreshRuns();
    selectRun(started.id);
    toast("Walkthrough: you are the backend. Pick each stage's outcome on its card.", "info", undefined, 4200);
  } catch (error) {
    toast(`Could not start the walkthrough: ${(error as Error).message}`, "error");
  }
}

function TraceBar() {
  const trace = useTunnel((s) => s.trace)!;
  const steps = trace.events.length;
  const done = trace.cursor >= steps;
  return (
    <div className="rs-tracebar">
      <Button size="sm" variant="ghost" icon={trace.playing ? "pause" : "play"} onClick={toggleTracePlaying} />
      <span className="rs-tracebar__label">{trace.label}</span>
      <input className="rs-tracebar__range" type="range" min={0} max={steps} value={trace.cursor} onChange={(e) => setTraceCursor(Number(e.target.value))} />
      <span className={cx("rs-tracebar__result", done && `is-${trace.kind}`)}>{done ? (trace.kind === "success" ? "landed" : trace.failure ?? trace.kind) : `${trace.path?.length ?? 0} stages`}</span>
      <Button size="sm" variant="ghost" icon="close" onClick={closeTrace} title="Back to the aggregate view" />
    </div>
  );
}

function Slider({ value, onChange, tone }: { value: number; onChange: (v: number) => void; tone?: string }) {
  return (
    <input
      className="rs-slider"
      type="range"
      min={0}
      max={100}
      step={1}
      value={Math.round(value * 100)}
      style={{ ["--fill" as string]: `${Math.round(value * 100)}%`, ["--tone" as string]: tone ?? "var(--signal)" }}
      onChange={(e) => onChange(Number(e.target.value) / 100)}
      onKeyDown={(e) => e.stopPropagation()}
    />
  );
}

function ScenarioPanel({ doc, focus }: { doc: StudioDoc; focus: string | null }) {
  const settings = useTunnel((s) => s.settings);
  const record = useTelemetry((s) => s.byName[doc.name]);
  useEffect(() => {
    void loadTelemetry(doc.name);
  }, [doc.name]);
  const choose = (preset: Exclude<Preset, "custom" | "calibrated">) =>
    updateSettings({ preset, agentSuccess: PRESETS[preset].agentSuccess, agentRetry: PRESETS[preset].agentRetry, toolSuccess: PRESETS[preset].toolSuccess, nodes: settings.preset === "calibrated" ? {} : settings.nodes, calibration: null });
  const tunable = doc.nodes.filter((n) => {
    const kind = kindOf(doc, n);
    return KINDS[kind].llm || kind === "tool" || kind === "human" || kind === "supervisor";
  });
  return (
    <div className="rs-scenario">
      <header className="rs-panel-head">
        <span className="label">Scenario</span>
      </header>
      <div className="rs-panel-scroll">
        <div className="rs-presets">
          {record ? (
            <button
              type="button"
              className={cx("rs-preset rs-preset--calibrated", settings.preset === "calibrated" && "is-on")}
              onClick={() => {
                applyCalibration(record);
                toast(`Calibrated to ${record.runs} real flight${record.runs === 1 ? "" : "s"}.`, "success", undefined, 2200);
              }}
            >
              <span className="rs-preset__name">
                <Glyph name="gauge" size={12} /> Calibrated
              </span>
              <span className="rs-preset__blurb">
                Fitted to {record.runs} real flight{record.runs === 1 ? "" : "s"}: every stage fails, retries, and picks routes the way it really has.
                {settings.preset === "calibrated" && settings.calibration ? ` Fitted ${new Date(settings.calibration.at).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" })}; click to refit.` : ""}
              </span>
            </button>
          ) : null}
          {(Object.keys(PRESETS) as (keyof typeof PRESETS)[]).map((p) => (
            <button key={p} type="button" className={cx("rs-preset", settings.preset === p && "is-on")} onClick={() => choose(p)}>
              <span className="rs-preset__name">{PRESETS[p].label}</span>
              <span className="rs-preset__blurb">{PRESETS[p].blurb}</span>
            </button>
          ))}
        </div>
        <div className="rs-scenario__dials">
          <div className="rs-dial">
            <div className="rs-dial__head">
              <span>Agent stages succeed</span>
              <span className="num">{pct(settings.agentSuccess)}</span>
            </div>
            <Slider value={settings.agentSuccess} tone="var(--kind-agent)" onChange={(v) => updateSettings({ preset: "custom", agentSuccess: Math.min(v, 1 - settings.agentRetry) })} />
          </div>
          <div className="rs-dial">
            <div className="rs-dial__head">
              <span>…ask for a retry</span>
              <span className="num">{pct(settings.agentRetry)}</span>
            </div>
            <Slider value={settings.agentRetry} tone="var(--warn)" onChange={(v) => updateSettings({ preset: "custom", agentRetry: Math.min(v, 1 - settings.agentSuccess) })} />
          </div>
          <div className="rs-dial">
            <div className="rs-dial__head">
              <span>Tool commands pass</span>
              <span className="num">{pct(settings.toolSuccess)}</span>
            </div>
            <Slider value={settings.toolSuccess} tone="var(--kind-tool)" onChange={(v) => updateSettings({ preset: "custom", toolSuccess: v })} />
          </div>
        </div>

        <div className="rs-scenario__heading label">Stages</div>
        {tunable.map((node) => (
          <StageBehavior key={node.id} doc={doc} node={node} open={focus === node.id} />
        ))}

        <div className="rs-scenario__heading label">Flight plan</div>
        <div className="rs-scenario__plan">
          <div className="rs-dial__head">
            <span>Flights</span>
          </div>
          <Segmented size="sm" value={String(settings.runs)} options={["100", "500", "2000"].map((v) => ({ value: v, label: Number(v).toLocaleString() }))} onChange={(v) => updateSettings({ runs: Number(v) })} />
          <div className="rs-dial__head" style={{ marginTop: 12 }}>
            <span>Give up after</span>
            <span className="faint">stage executions</span>
          </div>
          <Stepper value={settings.maxSteps} onChange={(v) => updateSettings({ maxSteps: v ?? 200 })} min={10} max={5000} />
          <div className="rs-dial__head" style={{ marginTop: 12 }}>
            <span>Seed</span>
            <button type="button" className="rs-linkbtn" onClick={() => updateSettings({ seed: settings.seed === null ? Math.floor(Math.random() * 1e9) : null })}>
              {settings.seed === null ? "random each time" : `fixed · ${settings.seed}`}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function StageBehavior({ doc, node, open }: { doc: StudioDoc; node: DocNode; open: boolean }) {
  const settings = useTunnel((s) => s.settings);
  const override = settings.nodes[node.id];
  const kind = kindOf(doc, node);
  const handler = handlerOf(doc, node);
  const labels = routeLabels(doc, node);
  const choices = gateChoices(doc, node);
  const set = (behavior: Behavior | undefined) => {
    const nodes = { ...settings.nodes };
    if (behavior && Object.keys(behavior).length > 0) nodes[node.id] = behavior;
    else delete nodes[node.id];
    updateSettings({ nodes });
  };
  const base = handler === "tool" ? settings.toolSuccess : settings.agentSuccess;
  const success = override?.outcomes ? (override.outcomes.success ?? 0) / Math.max(1e-9, Object.values(override.outcomes).reduce((a, b) => a + b, 0)) : kind === "human" ? 1 : base;
  return (
    <div className={cx("rs-behavior", open && "is-open", override && "is-custom")}>
      <button type="button" className="rs-behavior__head" onClick={() => focusTunnelNode(open ? null : node.id)}>
        <span style={{ color: `var(--kind-${kind})` }}>
          <Glyph name={kind} size={13} />
        </span>
        <span className="rs-behavior__name">{nodeLabel(doc, node)}</span>
        {kind !== "human" ? (
          <span className="rs-behavior__bar" title={`${pct(success)} succeed`}>
            <span style={{ width: pct(success) }} />
          </span>
        ) : (
          <span className="rs-behavior__meta faint">{choices.length} options</span>
        )}
        {override ? <span className="rs-behavior__dot" title="Customized" /> : null}
      </button>
      {open ? (
        <div className="rs-behavior__body">
          {kind !== "human" ? (
            <>
              <div className="rs-dial">
                <div className="rs-dial__head">
                  <span>Succeeds</span>
                  <span className="num">{pct(success)}</span>
                </div>
                <Slider value={success} tone="var(--ok)" onChange={(v) => set({ ...(override ?? {}), outcomes: { success: v, fail: Math.max(0, 1 - v) } })} />
              </div>
              <div className="rs-behavior__script">
                <div className="rs-dial__head">
                  <span>Script</span>
                  <span className="faint">exact outcomes per execution, then the odds</span>
                </div>
                <div className="rs-script">
                  {(override?.script ?? []).map((step, i) => (
                    <span key={i} className={cx("rs-script__step", `is-${step.split(":")[0]}`)}>
                      {step.startsWith("success") ? "✓" : step.startsWith("fail") ? "✕" : step.startsWith("retry") ? "↻" : "◐"}
                    </span>
                  ))}
                  {["success", "fail", "retry"].map((o) => (
                    <button key={o} type="button" className={cx("rs-script__add", `is-${o}`)} onClick={() => set({ ...(override ?? {}), script: [...(override?.script ?? []), o] })} title={`Append ${o}`}>
                      +{o === "success" ? "✓" : o === "fail" ? "✕" : "↻"}
                    </button>
                  ))}
                  {override?.script?.length ? (
                    <button type="button" className="rs-linkbtn" onClick={() => set({ ...(override ?? {}), script: undefined })}>
                      clear
                    </button>
                  ) : null}
                </div>
              </div>
            </>
          ) : null}
          {labels.length > 0 ? (
            <WeightList title="Chooses route" names={labels} weights={override?.labels} onChange={(labelsWeights) => set({ ...(override ?? {}), labels: labelsWeights })} />
          ) : null}
          {choices.length > 0 ? <WeightList title="Person picks" names={choices} weights={override?.choices} onChange={(w) => set({ ...(override ?? {}), choices: w })} /> : null}
          <div className="rs-dial">
            <div className="rs-dial__head">
              <span>Takes</span>
              <span className="faint">for time estimates</span>
            </div>
            <div className="rs-behavior__duration">
              <input
                className="rs-input mono"
                value={override?.duration_ms !== undefined ? String(override.duration_ms / 1000) : ""}
                placeholder="seconds"
                onChange={(e) => {
                  const seconds = Number(e.target.value);
                  set({ ...(override ?? {}), duration_ms: e.target.value.trim() && Number.isFinite(seconds) ? Math.round(seconds * 1000) : undefined });
                }}
                onKeyDown={(e) => e.stopPropagation()}
              />
              <span className="faint">s</span>
            </div>
          </div>
          {override ? (
            <button type="button" className="rs-linkbtn" onClick={() => set(undefined)}>
              Reset to scenario defaults
            </button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function WeightList({ title, names, weights, onChange }: { title: string; names: string[]; weights?: Record<string, number>; onChange: (w: Record<string, number>) => void }) {
  const current = weights ?? Object.fromEntries(names.map((n) => [n, 1 / names.length]));
  const total = Object.values(current).reduce((a, b) => a + b, 0) || 1;
  return (
    <div className="rs-dial">
      <div className="rs-dial__head">
        <span>{title}</span>
      </div>
      {names.map((name) => (
        <div key={name} className="rs-weight">
          <span className="rs-weight__name">{name}</span>
          <Slider value={(current[name] ?? 0) / total} tone="var(--kind-human)" onChange={(v) => onChange({ ...current, [name]: v })} />
          <span className="num rs-weight__pct">{pct((current[name] ?? 0) / total)}</span>
        </div>
      ))}
    </div>
  );
}

function Gauge({ value, low, high }: { value: number; low: number; high: number }) {
  const angle = (v: number) => Math.PI * (1 - v);
  const point = (v: number, r: number) => ({ x: 100 + r * Math.cos(angle(v)), y: 100 - r * Math.sin(angle(v)) });
  const arc = (a: number, b: number, r: number) => {
    const p1 = point(a, r);
    const p2 = point(b, r);
    return `M ${p1.x} ${p1.y} A ${r} ${r} 0 ${b - a > 0.5 ? 1 : 0} 1 ${p2.x} ${p2.y}`;
  };
  const tone = value >= 0.95 ? "var(--ok)" : value >= 0.8 ? "var(--warn)" : "var(--fail)";
  const needle = point(value, 70);
  return (
    <svg className="rs-gauge" viewBox="0 0 200 116">
      <path d={arc(0, 0.8, 84)} stroke="rgba(240,84,79,0.35)" strokeWidth="6" fill="none" />
      <path d={arc(0.8, 0.95, 84)} stroke="rgba(232,184,74,0.45)" strokeWidth="6" fill="none" />
      <path d={arc(0.95, 1, 84)} stroke="rgba(76,195,138,0.55)" strokeWidth="6" fill="none" />
      <path d={arc(Math.max(0.0001, low), Math.min(0.9999, Math.max(low + 0.002, high)), 76)} stroke={tone} strokeOpacity="0.35" strokeWidth="5" fill="none" />
      {[0, 0.25, 0.5, 0.75, 1].map((t) => {
        const a = point(t, 92);
        const b = point(t, 98);
        return <line key={t} x1={a.x} y1={a.y} x2={b.x} y2={b.y} stroke="var(--ink-3)" strokeWidth="1.2" />;
      })}
      <line x1="100" y1="100" x2={needle.x} y2={needle.y} stroke={tone} strokeWidth="2.4" strokeLinecap="round" />
      <circle cx="100" cy="100" r="5" fill="var(--bg-1)" stroke={tone} strokeWidth="2" />
    </svg>
  );
}

/**
 * With a calibrated scenario the tunnel is a forecast: the same stage behavior real flights showed, applied to the
 * pipeline as it is now. Compares the forecast with the record (success, and cost from real per-visit costs).
 */
function RealityCheck({ report, calibration }: { report: SimReport; calibration: Calibration }) {
  const cost = predictedCost(report, calibration);
  const delta = report.success_rate - calibration.successRate;
  const costDelta = cost && calibration.meanCost !== undefined ? cost.mean - calibration.meanCost : undefined;
  return (
    <section className="rs-results__section rs-reality">
      <div className="label">
        <Glyph name="gauge" size={11} /> Forecast vs. record
      </div>
      <div className="rs-reality__grid">
        <span className="faint">landed</span>
        <span className="num">{pct(calibration.successRate)}</span>
        <span className="rs-reality__arrow">→</span>
        <span className="num">{pct(report.success_rate)}</span>
        <span className={cx("num rs-reality__delta", Math.abs(delta) < 0.02 ? "" : delta > 0 ? "is-up" : "is-down")}>
          {Math.abs(delta) < 0.005 ? "±0" : `${delta > 0 ? "+" : "−"}${(Math.abs(delta) * 100).toFixed(0)} pts`}
        </span>
        {cost ? (
          <>
            <span className="faint">AIU / flight</span>
            <span className="num">{calibration.meanCost !== undefined ? calibration.meanCost.toFixed(1) : "—"}</span>
            <span className="rs-reality__arrow">→</span>
            <span className="num">{cost.mean.toFixed(1)}</span>
            <span className={cx("num rs-reality__delta", costDelta === undefined || Math.abs(costDelta) < 0.05 ? "" : costDelta < 0 ? "is-up" : "is-down")}>
              {costDelta === undefined ? "" : `${costDelta > 0 ? "+" : "−"}${Math.abs(costDelta).toFixed(1)}`}
            </span>
          </>
        ) : null}
      </div>
      <p className="faint">
        Record: {calibration.runs} real flight{calibration.runs === 1 ? "" : "s"}. Forecast: this design, flown {report.runs.toLocaleString()}× with the stage behavior those flights showed.
      </p>
    </section>
  );
}

function ResultsPanel({ doc }: { doc: StudioDoc }) {
  const report = useTunnel((s) => s.report);
  const running = useTunnel((s) => s.running);
  const error = useTunnel((s) => s.error);
  const settings = useTunnel((s) => s.settings);
  if (running && !report) {
    return (
      <div className="rs-results__empty">
        <Spinner size={18} />
        <span className="faint">Flying the factory…</span>
      </div>
    );
  }
  if (!report) {
    return (
      <div className="rs-results__intro">
        <div className="label">Flight readiness</div>
        <p className="serif">Before you spend a single token, fly the factory a few hundred times.</p>
        <p>The wind tunnel runs the real Reactor engine — routing, retries, goal gates, fan-out, human gates — with simulated stage outcomes. It finds the loops that never converge, the dead ends, and the gates that can't be satisfied, and shows where the traffic goes.</p>
        {error ? <p className="rs-fail-text">{error}</p> : null}
      </div>
    );
  }
  const landed = report.outcomes.success ?? 0;
  const verdict = report.success_rate >= 0.95 ? "Flight-ready under this scenario" : report.success_rate >= 0.8 ? "Lands most of the time" : "Not ready: most flights don't land";
  const hot = Object.entries(report.nodes)
    .filter(([, n]) => n.visits > 0)
    .sort((a, b) => b[1].mean_visits - a[1].mean_visits)
    .slice(0, 6);
  const maxBucket = Math.max(1, ...report.steps.histogram.map((b) => b.count));
  const label = (id?: string) => (id ? (findNode(doc, id) ? nodeLabel(doc, findNode(doc, id)!) : id) : "");
  return (
    <div className={cx("rs-results", running && "is-running")}>
      <section className="rs-results__gauge">
        <Gauge value={report.success_rate} low={report.success_ci[0]} high={report.success_ci[1]} />
        <div className="rs-results__rate num">{pct(report.success_rate, 1)}</div>
        <div className="rs-results__verdict">{verdict}</div>
        <div className="rs-results__ci faint num">
          {landed.toLocaleString()} of {report.runs.toLocaleString()} landed · 95% CI {pct(report.success_ci[0], 1)}–{pct(report.success_ci[1], 1)}
        </div>
      </section>

      {settings.calibration ? <RealityCheck report={report} calibration={settings.calibration} /> : null}

      <section className="rs-results__section">
        <div className="label">Outcomes</div>
        <div className="rs-stack">
          {OUTCOME_KINDS.filter((k) => (report.outcomes[k.key] ?? 0) > 0).map((k) => (
            <span key={k.key} style={{ width: pct((report.outcomes[k.key] ?? 0) / report.runs, 2), background: k.tone }} title={`${k.label}: ${report.outcomes[k.key]}`} />
          ))}
        </div>
        <div className="rs-legend">
          {OUTCOME_KINDS.filter((k) => (report.outcomes[k.key] ?? 0) > 0).map((k) => (
            <span key={k.key}>
              <span className="rs-dot" style={{ background: k.tone }} /> {k.label} <span className="num faint">{report.outcomes[k.key]}</span>
            </span>
          ))}
        </div>
      </section>

      <section className="rs-results__section">
        <div className="label">Stage executions per flight</div>
        <div className="rs-hist">
          {report.steps.histogram.map((b) => (
            <span key={b.steps} style={{ height: `${Math.max(3, (b.count / maxBucket) * 100)}%` }} title={`${b.steps}–${b.steps + report.steps.bucket_size - 1} steps: ${b.count} flights`} />
          ))}
        </div>
        <div className="rs-results__stats num">
          <span>
            min <b>{report.steps.min}</b>
          </span>
          <span>
            mean <b>{report.steps.mean.toFixed(1)}</b>
          </span>
          {report.steps.p90 !== undefined ? (
            <span>
              p90 <b>{report.steps.p90}</b>
            </span>
          ) : null}
          <span>
            max <b>{report.steps.max}</b>
          </span>
        </div>
        {report.simulated_duration_ms && report.simulated_duration_ms.mean > 0 ? (
          <div className="rs-results__stats num">
            <span>
              time · mean <b>{formatDuration(report.simulated_duration_ms.mean)}</b>
            </span>
            <span>
              p90 <b>{formatDuration(report.simulated_duration_ms.p90)}</b>
            </span>
          </div>
        ) : null}
      </section>

      {report.failures.length > 0 ? (
        <section className="rs-results__section">
          <div className="label">How flights go down</div>
          {report.failures.map((f, i) => (
            <div key={i} className="rs-failure">
              <div className="rs-failure__head">
                <span className={cx("rs-failure__kind", `is-${f.kind}`)}>{OUTCOME_KINDS.find((k) => k.key === f.kind)?.label ?? f.kind}</span>
                <span className="num">{pct(f.count / report.runs, 1)}</span>
                <Button size="sm" variant="ghost" icon="play" title="Replay one of these flights on the canvas" onClick={() => void playTrace(`Replay: ${f.kind.replace("_", " ")}`, f.sample.seed, f.sample.run_index)} />
              </div>
              <div className="rs-failure__reason">{f.reason}</div>
              {f.last_node ? <div className="rs-failure__where faint">last stage: {label(f.last_node)}</div> : null}
            </div>
          ))}
        </section>
      ) : null}

      <section className="rs-results__section">
        <div className="label">Hot spots</div>
        {hot.map(([id, n]) => (
          <button key={id} type="button" className="rs-hot" onClick={() => focusTunnelNode(id)}>
            <span className="rs-hot__name">{label(id)}</span>
            <span className="rs-hot__bar">
              <span style={{ width: pct(n.mean_visits / Math.max(1, hot[0][1].mean_visits)) }} />
            </span>
            <span className="num rs-hot__value">×{n.mean_visits.toFixed(1)}</span>
          </button>
        ))}
      </section>

      {report.warnings.length > 0 ? (
        <section className="rs-results__section">
          <div className="label">Notes</div>
          {report.warnings.map((w, i) => (
            <p key={i} className="rs-results__warning">
              {w}
            </p>
          ))}
        </section>
      ) : null}

      <footer className="rs-results__foot faint num">
        {report.runs.toLocaleString()} flights in {formatDuration(report.duration_ms)} · seed {report.seed} ·{" "}
        <button type="button" className="rs-linkbtn" onClick={() => void playTrace("Typical flight", report.samples.typical.seed, report.samples.typical.run_index)}>
          replay a typical flight
        </button>
      </footer>
    </div>
  );
}
