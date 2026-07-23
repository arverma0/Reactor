import { create } from "zustand";
import { api, streamEvents } from "../../api/client";
import type { PipelineEventJson, RunDetail, RunSummary } from "../../api/types";
import type { LayoutSidecar, StudioDoc } from "../../model/types";
import { docFromDot } from "../../state/files";
import { useStudio } from "../../state/store";

export interface RunSource {
  dot: string;
  layout?: LayoutSidecar;
  sourcePath?: string | null;
}

interface FlightsState {
  runs: RunSummary[];
  selected: string | null;
  details: Record<string, RunDetail>;
  events: Record<string, PipelineEventJson[]>;
  docs: Record<string, StudioDoc | "loading" | "missing">;
  stage: string | null;
  /** Scrub instant (ms epoch); null follows the live edge. */
  at: number | null;
  playing: boolean;
  speed: number;
  /** Re-render tick for live timers. */
  now: number;
}

export const useFlights = create<FlightsState>()(() => ({
  runs: [],
  selected: null,
  details: {},
  events: {},
  docs: {},
  stage: null,
  at: null,
  playing: false,
  speed: 8,
  now: Date.now(),
}));

const set = useFlights.setState;
const get = useFlights.getState;
const SOURCE_KEY = (id: string) => `reactor.run.${id}`;

export function rememberRunSource(id: string, source: RunSource) {
  try {
    localStorage.setItem(SOURCE_KEY(id), JSON.stringify(source));
  } catch {
    // Storage full: the run can still be shown when the server provides its graph.
  }
}

function recallRunSource(id: string): RunSource | null {
  try {
    const raw = localStorage.getItem(SOURCE_KEY(id));
    return raw ? (JSON.parse(raw) as RunSource) : null;
  } catch {
    return null;
  }
}

export async function refreshRuns() {
  try {
    const runs = await api.runs();
    set({ runs });
  } catch {
    // offline: keep the last list
  }
}

const streams = new Map<string, () => void>();
const buffers = new Map<string, PipelineEventJson[]>();
let flushScheduled = false;
let detailTimer: ReturnType<typeof setTimeout> | undefined;

function flush() {
  flushScheduled = false;
  const events = { ...get().events };
  for (const [id, buffer] of buffers) {
    if (buffer.length === 0) continue;
    events[id] = [...(events[id] ?? []), ...buffer];
    buffer.length = 0;
  }
  set({ events });
}

function scheduleDetail(id: string, delay = 250) {
  clearTimeout(detailTimer);
  detailTimer = setTimeout(() => void loadDetail(id), delay);
}

export async function loadDetail(id: string) {
  try {
    const detail = await api.run(id);
    set({ details: { ...get().details, [id]: detail } });
  } catch {
    // ignore
  }
}

function openStream(id: string) {
  if (streams.has(id)) return;
  buffers.set(id, []);
  set({ events: { ...get().events, [id]: [] } });
  const close = streamEvents(`/pipelines/${encodeURIComponent(id)}/events`, {
    onEvent: (event) => {
      buffers.get(id)?.push(event);
      if (!flushScheduled) {
        flushScheduled = true;
        requestAnimationFrame(flush);
      }
      if (event.type !== "StageLog" && event.type !== "CheckpointSaved") scheduleDetail(id);
    },
    onEnd: () => {
      streams.delete(id);
      scheduleDetail(id, 50);
      void refreshRuns();
    },
  });
  streams.set(id, close);
}

async function loadDoc(id: string) {
  if (get().docs[id] && get().docs[id] !== "missing") return;
  set({ docs: { ...get().docs, [id]: "loading" } });
  const source = recallRunSource(id);
  try {
    if (source) {
      const doc = await docFromDot(source.dot, source.layout);
      set({ docs: { ...get().docs, [id]: doc } });
      return;
    }
    // Live runs: the server keeps each run's DOT source (and the Studio metadata it was started with).
    const live = await api.get<{ dot?: string; name?: string; studio?: { layout?: LayoutSidecar } | null }>(`/studio/api/pipelines/${encodeURIComponent(id)}/source`).catch(() => null);
    if (live?.dot) {
      const design = useStudio.getState().doc;
      const layout = live.studio?.layout ?? (design.name === live.name ? design.layout : undefined);
      const doc = await docFromDot(live.dot, layout);
      set({ docs: { ...get().docs, [id]: doc } });
      return;
    }
    // Runs started elsewhere: the history API (when available) serves the graph and Studio metadata by run directory.
    const detail = get().details[id] ?? (await api.run(id).catch(() => null));
    const dir = detail?.logs_root?.split(/[\\/]/).filter(Boolean).pop();
    const history = dir ? await api.get<{ graph_dot?: string; studio?: { layout?: LayoutSidecar } | null }>(`/studio/api/runs/${encodeURIComponent(dir)}`).catch(() => null) : null;
    if (history?.graph_dot) {
      const doc = await docFromDot(history.graph_dot, history.studio?.layout);
      set({ docs: { ...get().docs, [id]: doc } });
      return;
    }
    set({ docs: { ...get().docs, [id]: "missing" } });
  } catch {
    set({ docs: { ...get().docs, [id]: "missing" } });
  }
}

export function selectRun(id: string | null) {
  set({ selected: id, stage: null, at: null, playing: false });
  for (const [other, close] of streams) {
    if (other !== id) {
      close();
      streams.delete(other);
    }
  }
  if (!id) return;
  openStream(id);
  void loadDetail(id);
  void loadDoc(id);
}

export const selectStage = (stage: string | null) => set({ stage });
export const scrubTo = (at: number | null) => set({ at, playing: at === null ? false : get().playing });
export const setPlaying = (playing: boolean) => set({ playing });
export const setSpeed = (speed: number) => set({ speed });

let ticker: ReturnType<typeof setInterval> | undefined;
let lastFrame = 0;

/** Drives live timers and replay playback while the Flights view is mounted. */
export function startClock(range: () => { start: number; end: number } | null) {
  stopClock();
  lastFrame = performance.now();
  ticker = setInterval(() => {
    const frame = performance.now();
    const dt = frame - lastFrame;
    lastFrame = frame;
    const state = get();
    const patch: Partial<FlightsState> = { now: Date.now() };
    if (state.playing && state.at !== null) {
      const bounds = range();
      if (bounds) {
        const next = state.at + dt * state.speed;
        if (next >= bounds.end) {
          patch.at = null;
          patch.playing = false;
        } else patch.at = next;
      }
    }
    set(patch);
  }, 100);
}

export function stopClock() {
  if (ticker) clearInterval(ticker);
  ticker = undefined;
}
