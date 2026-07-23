import { create } from "zustand";
import { api } from "../../api/client";
import type { PipelineEventJson, Telemetry } from "../../api/types";
import { writeDot } from "../../model/dot";
import { toast, useStudio } from "../../state/store";
import { buildScenario, calibrate, defaultSettings, type SimReport, type TunnelSettings } from "./scenario";

export interface TraceState {
  label: string;
  events: PipelineEventJson[];
  /** How many events are currently "played". */
  cursor: number;
  playing: boolean;
  status?: string;
  kind?: string;
  failure?: string;
  path?: string[];
}

interface TunnelState {
  settings: TunnelSettings;
  report: SimReport | null;
  /** DOT the report was computed for (to flag a stale report). */
  reportDot: string | null;
  running: boolean;
  error: string | null;
  trace: TraceState | null;
  focus: string | null;
}

const key = () => `reactor.tunnel.${useStudio.getState().doc.name}`;

function load(): TunnelSettings {
  try {
    const raw = localStorage.getItem(key());
    if (raw) return { ...defaultSettings(), ...(JSON.parse(raw) as Partial<TunnelSettings>) };
  } catch {
    // fall through
  }
  return defaultSettings();
}

export const useTunnel = create<TunnelState>()(() => ({
  settings: defaultSettings(),
  report: null,
  reportDot: null,
  running: false,
  error: null,
  trace: null,
  focus: null,
}));

const set = useTunnel.setState;
const get = useTunnel.getState;

export function loadSettingsForDoc() {
  set({ settings: load(), report: null, reportDot: null, trace: null, error: null });
}

export function updateSettings(patch: Partial<TunnelSettings>) {
  const settings = { ...get().settings, ...patch };
  set({ settings });
  try {
    localStorage.setItem(key(), JSON.stringify(settings));
  } catch {
    // ignore
  }
}

/** Fits the scenario to the open pipeline's real flights. */
export function applyCalibration(telemetry: Telemetry) {
  updateSettings(calibrate(useStudio.getState().doc, telemetry, get().settings));
  set({ report: null, reportDot: null, trace: null });
}

export async function runTunnel() {
  const doc = useStudio.getState().doc;
  const dot = writeDot(doc);
  set({ running: true, error: null, trace: null });
  try {
    const { settings } = get();
    const report = await api.post<SimReport>("/studio/api/simulate", { dot, scenario: buildScenario(doc, settings), runs: settings.runs, max_steps: settings.maxSteps });
    set({ report, reportDot: dot, running: false });
  } catch (error) {
    set({ running: false, error: (error as Error).message });
  }
}

let traceTimer: ReturnType<typeof setInterval> | undefined;

/** Fetches one simulated sample and plays it on the canvas, stage by stage. */
export async function playTrace(label: string, seed?: number, runIndex = 0) {
  const doc = useStudio.getState().doc;
  const { settings } = get();
  try {
    const trace = await api.post<{ events: PipelineEventJson[]; status: string; kind: string; failure_reason?: string; path: string[] }>("/studio/api/simulate/trace", {
      dot: writeDot(doc),
      scenario: buildScenario(doc, settings),
      seed: seed ?? settings.seed ?? undefined,
      run_index: runIndex,
      max_steps: settings.maxSteps,
    });
    set({ trace: { label, events: trace.events, cursor: 0, playing: true, status: trace.status, kind: trace.kind, failure: trace.failure_reason, path: trace.path } });
    startTracePlayback();
  } catch (error) {
    toast(`Could not replay: ${(error as Error).message}`, "error");
  }
}

const STEP_EVENTS = new Set(["StageStarted", "StageCompleted", "EdgeSelected", "PipelineCompleted", "PipelineFailed", "InterviewStarted"]);

export function startTracePlayback() {
  clearInterval(traceTimer);
  traceTimer = setInterval(() => {
    const trace = get().trace;
    if (!trace || !trace.playing) return;
    let cursor = trace.cursor;
    // Advance to the next "visible" event so each tick shows one meaningful step.
    while (cursor < trace.events.length) {
      cursor++;
      if (STEP_EVENTS.has(trace.events[cursor - 1].type)) break;
    }
    const done = cursor >= trace.events.length;
    set({ trace: { ...trace, cursor, playing: !done } });
    if (done) clearInterval(traceTimer);
  }, 260);
}

export function setTraceCursor(cursor: number) {
  const trace = get().trace;
  if (!trace) return;
  clearInterval(traceTimer);
  set({ trace: { ...trace, cursor: Math.max(0, Math.min(trace.events.length, cursor)), playing: false } });
}

export function toggleTracePlaying() {
  const trace = get().trace;
  if (!trace) return;
  const restart = trace.cursor >= trace.events.length;
  set({ trace: { ...trace, playing: !trace.playing, cursor: restart ? 0 : trace.cursor } });
  if (!trace.playing) startTracePlayback();
}

export function closeTrace() {
  clearInterval(traceTimer);
  set({ trace: null });
}

export const focusTunnelNode = (focus: string | null) => set({ focus });
