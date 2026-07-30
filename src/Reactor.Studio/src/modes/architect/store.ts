import { create } from "zustand";
import { api, type ApiError } from "../../api/client";
import { diffDocs, type DocDiff } from "../../model/diff";
import { writeDot } from "../../model/dot";
import type { StudioDoc } from "../../model/types";
import { docFromDot } from "../../state/files";
import { applyDoc, loadDoc, requestFit, setMode, toast, undo, useStudio } from "../../state/store";
import { loadTelemetry, useTelemetry } from "../../state/telemetry";
import { refreshRuns, selectRun, setLens } from "../flights/store";
import { useTunnel } from "../tunnel/store";
import { buildEvidence } from "./evidence";
import { compareForecast, type ForecastCompare } from "./forecast";

export type ArchitectMode = "draft" | "revise" | "critique";

export interface ServerForecast {
  happy_path: number;
  /** Clean landing rate under the realistic preset. */
  realistic: number;
  /** Any landing (clean or with failed stages) under the realistic preset. */
  realistic_landed?: number;
  /** Share of realistic flights that land with failed stages behind them. */
  failed_stage_landings?: number;
  failed_stages?: string[];
  step_limit: number;
  mean_steps: number;
  p90_steps: number;
  mean_duration_ms?: number;
  p90_duration_ms?: number;
  stages: number;
  routes: number;
  failures: { kind: string; reason: string; share: number; last_node?: string | null }[];
}

export interface ArchitectState {
  id: string;
  status: string;
  current_node: string | null;
  mode: ArchitectMode;
  attempt: number;
  max_drafts: number;
  rationale: string | null;
  proposal: { dot: string; checked: boolean; valid: boolean; problems: string[]; warnings: string[]; forecast: ServerForecast | null } | null;
  feedback: string | null;
  /** The drafting model's self-review of the checked draft against the brief (`VERDICT: PASS` or `VERDICT: REVISE` with changes). */
  review?: string | null;
  error: string | null;
}

export interface DraftLog {
  attempt: number;
  passed: boolean;
  problems: string[];
}

export interface ArchitectJob {
  id: string;
  mode: ArchitectMode;
  startedAt: number;
  /** The design when the job started (revise and critique compare against it). */
  base: StudioDoc | null;
  state: ArchitectState | null;
  drafts: DraftLog[];
}

interface Settings {
  mode: ArchitectMode;
  model: string | null;
  effort: string;
  evidence: boolean;
}

interface ArchitectStore extends Settings {
  brief: Record<ArchitectMode, string>;
  job: ArchitectJob | null;
  starting: boolean;
  proposalDoc: StudioDoc | null;
  diff: DocDiff | null;
  forecast: ForecastCompare | "loading" | "error" | null;
  preview: boolean;
  /** Second click needed to replace a design with unsaved changes. */
  confirmReplace: boolean;
}

const SETTINGS_KEY = "reactor.architect";

function loadSettings(): Settings {
  try {
    const raw = localStorage.getItem(SETTINGS_KEY);
    if (raw) return { mode: "draft", model: null, effort: "high", evidence: true, ...(JSON.parse(raw) as Partial<Settings>) };
  } catch {
    // defaults
  }
  return { mode: "draft", model: null, effort: "high", evidence: true };
}

export const useArchitect = create<ArchitectStore>()(() => ({
  ...loadSettings(),
  brief: { draft: "", revise: "", critique: "" },
  job: null,
  starting: false,
  proposalDoc: null,
  diff: null,
  forecast: null,
  preview: false,
  confirmReplace: false,
}));

const set = useArchitect.setState;
const get = useArchitect.getState;

export function updateArchitect(patch: Partial<Settings>) {
  set(patch);
  const { mode, model, effort, evidence } = get();
  localStorage.setItem(SETTINGS_KEY, JSON.stringify({ mode, model, effort, evidence }));
}

export const setBrief = (mode: ArchitectMode, text: string) => set({ brief: { ...get().brief, [mode]: text } });

let pollTimer: ReturnType<typeof setTimeout> | undefined;

const TERMINAL = new Set(["success", "fail", "cancelled", "error"]);

/** The evidence the Architect gets for a revision or critique: the flight record and a current wind tunnel report. */
export async function gatherEvidence(doc: StudioDoc): Promise<{ text: string; flights: number; simulated: number }> {
  const record = await loadTelemetry(doc.name, 30000);
  const tunnel = useTunnel.getState();
  const report = tunnel.report && tunnel.reportDot === writeDot(doc) ? tunnel.report : null;
  const scenario = report ? (tunnel.settings.preset === "calibrated" ? "calibrated to the flight record" : `${tunnel.settings.preset} scenario`) : null;
  return { text: buildEvidence(doc, record, report, scenario), flights: record?.runs ?? 0, simulated: report?.runs ?? 0 };
}

export async function startArchitect() {
  const { mode, model, effort, evidence, brief } = get();
  const studio = useStudio.getState();
  const doc = studio.doc;
  set({ starting: true, confirmReplace: false });
  try {
    const models = studio.models.filter((m) => m.available).map((m) => m.id);
    const gathered = mode !== "draft" && evidence ? await gatherEvidence(doc) : null;
    const response = await api.post<{ id: string }>("/studio/api/architect", {
      mode,
      brief: brief[mode].trim(),
      dot: mode === "draft" ? undefined : writeDot(doc),
      evidence: gathered?.text || undefined,
      models,
      model: model ?? undefined,
      reasoning_effort: model ? effort : undefined,
    });
    set({
      starting: false,
      job: { id: response.id, mode, startedAt: Date.now(), base: mode === "draft" ? null : doc, state: null, drafts: [] },
      proposalDoc: null,
      diff: null,
      forecast: null,
      preview: false,
    });
    void refreshRuns();
    poll(response.id);
  } catch (error) {
    set({ starting: false });
    toast((error as Error).message, "error", undefined, 9000);
  }
}

function poll(id: string) {
  clearTimeout(pollTimer);
  pollTimer = setTimeout(async () => {
    const job = get().job;
    if (!job || job.id !== id) return;
    let state: ArchitectState;
    try {
      state = await api.get<ArchitectState>(`/studio/api/architect/${encodeURIComponent(id)}`);
    } catch (error) {
      if ((error as ApiError).status === 404) {
        // The server no longer knows the run (it restarted): stop, keeping whatever the last poll showed.
        const lost: ArchitectState = {
          ...(job.state ?? { id, current_node: null, mode: job.mode, attempt: 0, max_drafts: 3, rationale: null, proposal: null, feedback: null }),
          status: "error",
          error: "The server no longer knows this Architect run (it was restarted). Start it again.",
        };
        set({ job: { ...job, state: lost } });
        return;
      }
      poll(id);
      return;
    }
    const drafts = [...job.drafts];
    if (state.proposal?.checked && !drafts.some((d) => d.attempt === state.attempt)) {
      drafts.push({ attempt: state.attempt, passed: state.proposal.valid, problems: state.proposal.problems });
    }
    set({ job: { ...job, state, drafts } });
    if (TERMINAL.has(state.status)) void finish(job, state);
    else poll(id);
  }, get().job?.state ? 1200 : 400);
}

async function finish(job: ArchitectJob, state: ArchitectState) {
  if (!state.proposal?.dot) return;
  try {
    // Revisions keep the current layout for stages that keep their ids; the rest are placed automatically.
    const proposal = await docFromDot(state.proposal.dot, job.base?.layout);
    const named = job.base && proposal.name !== job.base.name ? { ...proposal, name: job.base.name } : proposal;
    set({ proposalDoc: named, diff: job.base ? diffDocs(job.base, named) : null });
    if (!state.proposal.valid) return;
    set({ forecast: "loading" });
    const record = useTelemetry.getState().byName[(job.base ?? named).name] ?? null;
    const forecast = await compareForecast(job.base, named, record);
    if (get().job?.id === job.id) set({ forecast });
  } catch (error) {
    if (get().job?.id === job.id) set({ forecast: get().proposalDoc ? "error" : null });
    if (!get().proposalDoc) toast(`The proposal could not be loaded: ${(error as Error).message}`, "error");
  }
}

export async function cancelArchitect() {
  const job = get().job;
  if (!job) return;
  await api.cancel(job.id).catch((e) => toast((e as Error).message, "error"));
}

export function watchArchitectFlight() {
  const job = get().job;
  if (!job) return;
  setMode("flights");
  setLens("flight");
  void refreshRuns().then(() => selectRun(job.id));
}

export function togglePreview(on = !get().preview) {
  if (!get().proposalDoc) return;
  set({ preview: on });
  requestFit();
}

export function discardProposal() {
  clearTimeout(pollTimer);
  set({ job: null, proposalDoc: null, diff: null, forecast: null, preview: false, confirmReplace: false });
}

export function acceptProposal() {
  const { proposalDoc, job, confirmReplace } = get();
  if (!proposalDoc || !job) return;
  const studio = useStudio.getState();
  if (job.mode === "draft") {
    if (studio.dirty && !confirmReplace) {
      set({ confirmReplace: true });
      return;
    }
    loadDoc(proposalDoc, {});
    localStorage.removeItem("reactor.lastFile");
    toast("The Architect's pipeline is open as a new, unsaved document.", "success", { label: "Save as…", run: () => useStudio.setState({ saveAsOpen: true }) }, 7000);
  } else {
    applyDoc(() => ({ ...proposalDoc, header: studio.doc.header }));
    toast("Applied the Architect's revision.", "success", { label: "Undo", run: undo }, 7000);
  }
  set({ job: null, proposalDoc: null, diff: null, forecast: null, preview: false, confirmReplace: false });
  requestFit();
}
