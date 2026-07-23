import { api, ApiError, onUnauthorized } from "../api/client";
import { writeDot } from "../model/dot";
import { useStudio } from "./store";

const set = useStudio.setState;
const get = useStudio.getState;

export async function loadInfo() {
  try {
    const info = await api.info();
    set({ info, connected: true, needsToken: false });
  } catch (error) {
    set({ connected: (error as ApiError).status === 401 });
  }
}

export async function loadModels() {
  try {
    const result = await api.models();
    set({ models: result.models, modelsError: result.error ?? null });
  } catch (error) {
    set({ modelsError: (error as Error).message });
  }
}

let analysisTimer: ReturnType<typeof setTimeout> | undefined;
let analysisController: AbortController | undefined;

async function analyze() {
  const dot = writeDot(get().doc);
  if (dot === get().analysis.dot && get().analysis.status === "ok") return;
  analysisController?.abort();
  const controller = new AbortController();
  analysisController = controller;
  set({ analysis: { ...get().analysis, status: "pending" } });
  try {
    const result = await api.parse(dot, controller.signal);
    if (controller.signal.aborted) return;
    if (result.ok) {
      set({
        connected: true,
        analysis: {
          status: "ok",
          diagnostics: result.diagnostics,
          valid: result.valid,
          parseError: null,
          effective: Object.fromEntries(result.graph.nodes.map((n) => [n.id, n.effective])),
          dot,
        },
      });
    } else {
      set({
        analysis: { status: "ok", diagnostics: [], valid: false, parseError: { message: result.message, line: result.line, column: result.column }, effective: {}, dot },
      });
    }
  } catch (error) {
    if ((error as Error).name === "AbortError") return;
    set({ analysis: { ...get().analysis, status: "offline" }, connected: (error as ApiError).status !== 0 });
  }
}

export function scheduleAnalysis(delay = 260) {
  clearTimeout(analysisTimer);
  analysisTimer = setTimeout(() => void analyze(), delay);
}

let started = false;

export function startSession() {
  if (started) return;
  started = true;
  onUnauthorized(() => set({ needsToken: true }));
  useStudio.subscribe((state, previous) => {
    if (state.doc !== previous.doc) scheduleAnalysis();
  });
  void loadInfo().then(() => {
    scheduleAnalysis(0);
    void loadModels();
  });
  setInterval(() => {
    if (!get().connected) void loadInfo();
  }, 5000);
}
