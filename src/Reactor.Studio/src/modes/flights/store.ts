import { create } from "zustand";
import { api, streamEvents } from "../../api/client";
import type { HistoryDetail, HistorySummary, PipelineEventJson, RunDetail, RunSummary } from "../../api/types";
import type { LayoutSidecar, StudioDoc } from "../../model/types";
import { docFromDot } from "../../state/files";
import { useStudio } from "../../state/store";
import { mergeFlights, type Flight } from "./view";

export type { Flight } from "./view";

export interface RunSource {
  dot: string;
  layout?: LayoutSidecar;
  sourcePath?: string | null;
}

export type Lens = "flight" | "record";

interface FlightsState {
  flights: Flight[];
  loaded: boolean;
  /** "pipeline": only flights of the pipeline open in Design. */
  filter: "pipeline" | "all";
  selected: string | null;
  live: Record<string, RunDetail>;
  history: Record<string, HistoryDetail>;
  events: Record<string, PipelineEventJson[]>;
  docs: Record<string, StudioDoc | "loading" | "missing">;
  lens: Lens;
  stage: string | null;
  /** Scrub instant (ms epoch); null follows the live edge. */
  at: number | null;
  playing: boolean;
  speed: number;
  /** Re-render tick for live timers. */
  now: number;
}

export const useFlights = create<FlightsState>()(() => ({
  flights: [],
  loaded: false,
  filter: (localStorage.getItem("reactor.flights.filter") as "pipeline" | "all" | null) ?? "all",
  selected: null,
  live: {},
  history: {},
  events: {},
  docs: {},
  lens: "flight",
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

const historySupported = () => useStudio.getState().info?.features.includes("history") ?? false;

let lastLive: RunSummary[] = [];
let lastHistory: HistorySummary[] = [];

export async function refreshRuns() {
  const [live, history] = await Promise.all([api.runs().catch(() => null), historySupported() ? api.history().catch(() => null) : Promise.resolve(null)]);
  if (live) lastLive = live;
  if (history) lastHistory = history;
  if (!live && !history) return;
  set({ flights: mergeFlights(lastLive, lastHistory), loaded: true });
  // A run selected right after it started gains its directory once the history lists it.
  const flight = currentFlight();
  if (flight?.dir && !get().history[flight.dir]) void loadHistory(flight.dir);
}

export function flightByKey(key: string | null | undefined): Flight | undefined {
  return key ? get().flights.find((f) => f.key === key) : undefined;
}

export const currentFlight = () => flightByKey(get().selected);

/** Live id and run directory for a flight key (keys of runs not listed yet are live ids). */
export function flightIds(key: string): { liveId: string | null; dir: string | null } {
  const flight = flightByKey(key);
  if (flight) return { liveId: flight.liveId, dir: flight.dir };
  return key.startsWith("dir:") ? { liveId: null, dir: key.slice(4) } : { liveId: key, dir: null };
}

const streams = new Map<string, () => void>();
const buffers = new Map<string, PipelineEventJson[]>();
let flushScheduled = false;
let detailTimer: ReturnType<typeof setTimeout> | undefined;
let historyTimer: ReturnType<typeof setTimeout> | undefined;

function flush() {
  flushScheduled = false;
  const events = { ...get().events };
  for (const [key, buffer] of buffers) {
    if (buffer.length === 0) continue;
    events[key] = [...(events[key] ?? []), ...buffer];
    buffer.length = 0;
  }
  set({ events });
}

function scheduleDetail(key: string, delay = 250, withHistory = false) {
  const { liveId, dir } = flightIds(key);
  clearTimeout(detailTimer);
  if (liveId) detailTimer = setTimeout(() => void loadLive(liveId), delay);
  if (withHistory && dir) {
    clearTimeout(historyTimer);
    historyTimer = setTimeout(() => void loadHistory(dir), delay + 150);
  }
}

export async function loadLive(id: string) {
  try {
    const detail = await api.run(id);
    set({ live: { ...get().live, [id]: detail } });
  } catch {
    // ignore
  }
}

export async function loadHistory(dir: string) {
  try {
    const detail = await api.historyRun(dir);
    set({ history: { ...get().history, [dir]: detail } });
  } catch {
    // ignore
  }
}

/** Refreshes whatever the selected flight is backed by. */
export function loadDetail(key: string) {
  const { liveId, dir } = flightIds(key);
  if (liveId) void loadLive(liveId);
  if (dir) void loadHistory(dir);
}

function openStream(key: string, liveId: string) {
  if (streams.has(key)) return;
  buffers.set(key, []);
  set({ events: { ...get().events, [key]: [] } });
  const close = streamEvents(`/pipelines/${encodeURIComponent(liveId)}/events`, {
    onEvent: (event) => {
      buffers.get(key)?.push(event);
      if (!flushScheduled) {
        flushScheduled = true;
        requestAnimationFrame(flush);
      }
      if (event.type !== "StageLog" && event.type !== "CheckpointSaved") scheduleDetail(key, 250, event.type === "StageCompleted" || event.type.startsWith("Pipeline"));
    },
    onEnd: () => {
      streams.delete(key);
      scheduleDetail(key, 50, true);
      void refreshRuns();
    },
    onError: () => {
      // The server forgot the run (restart): fall back to its directory on disk.
      streams.delete(key);
      const { dir } = flightIds(key);
      if (dir) void tailHistoryEvents(key, dir, true);
    },
  });
  streams.set(key, close);
}

const tails = new Map<string, ReturnType<typeof setTimeout>>();

/** Reads a run's events.jsonl page by page; keeps tailing while the run is still being written (e.g. `reactor run`). */
async function tailHistoryEvents(key: string, dir: string, reset = false) {
  if (reset) set({ events: { ...get().events, [key]: [] } });
  let since = Math.max(0, ...(get().events[key] ?? []).map((e) => e.seq ?? 0));
  for (let page = 0; page < 40; page++) {
    let result;
    try {
      result = await api.historyEvents(dir, since);
    } catch {
      return;
    }
    if (get().selected !== key) return;
    if (result.events.length > 0) set({ events: { ...get().events, [key]: [...(get().events[key] ?? []), ...result.events] } });
    since = result.next;
    if (result.done) {
      void loadHistory(dir);
      return;
    }
    if (result.events.length === 0) break;
  }
  clearTimeout(tails.get(key));
  tails.set(
    key,
    setTimeout(() => {
      if (get().selected === key) {
        void loadHistory(dir);
        void tailHistoryEvents(key, dir);
      }
    }, 1500),
  );
}

async function docFrom(dot: string, layout: Partial<LayoutSidecar> | null | undefined, name: string | undefined) {
  const design = useStudio.getState().doc;
  return docFromDot(dot, layout ?? (name && design.name === name ? design.layout : undefined));
}

async function loadDoc(key: string) {
  if (get().docs[key] && get().docs[key] !== "missing") return;
  set({ docs: { ...get().docs, [key]: "loading" } });
  const { liveId, dir } = flightIds(key);
  const done = (doc: StudioDoc | "missing") => set({ docs: { ...get().docs, [key]: doc } });
  try {
    const source = liveId ? recallRunSource(liveId) : null;
    if (source) return done(await docFromDot(source.dot, source.layout));
    if (dir) {
      const history = get().history[dir] ?? (await api.historyRun(dir).catch(() => null));
      if (history) set({ history: { ...get().history, [dir]: history } });
      if (history?.graph_dot) return done(await docFrom(history.graph_dot, history.studio?.layout, history.summary.name));
    }
    if (liveId) {
      // Live runs: the server keeps each run's DOT source (and the Studio metadata it was started with).
      const live = await api.get<{ dot?: string; name?: string; studio?: { layout?: LayoutSidecar } | null }>(`/studio/api/pipelines/${encodeURIComponent(liveId)}/source`).catch(() => null);
      if (live?.dot) return done(await docFrom(live.dot, live.studio?.layout, live.name));
    }
    done("missing");
  } catch {
    done("missing");
  }
}

export function selectRun(key: string | null) {
  set({ selected: key, stage: null, at: null, playing: false });
  for (const [other, close] of streams) {
    if (other !== key) {
      close();
      streams.delete(other);
    }
  }
  for (const [other, timer] of tails) {
    if (other !== key) {
      clearTimeout(timer);
      tails.delete(other);
    }
  }
  if (!key) return;
  const { liveId, dir } = flightIds(key);
  if (liveId) {
    openStream(key, liveId);
    void loadLive(liveId);
  } else if (dir && !(get().events[key]?.length && isFinished(flightByKey(key)?.status))) {
    void tailHistoryEvents(key, dir, true);
  }
  if (dir) void loadHistory(dir);
  void loadDoc(key);
}

const isFinished = (status?: string) => !!status && status !== "running" && status !== "queued";

export const selectStage = (stage: string | null) => set({ stage });
export const scrubTo = (at: number | null) => set({ at, playing: at === null ? false : get().playing });
export const setPlaying = (playing: boolean) => set({ playing });
export const setSpeed = (speed: number) => set({ speed });
export const setLens = (lens: Lens) => set({ lens });
export function setFilter(filter: "pipeline" | "all") {
  localStorage.setItem("reactor.flights.filter", filter);
  set({ filter });
}

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
