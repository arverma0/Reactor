import type {
  ForkRequest,
  ForkResponse,
  HistoryDetail,
  HistorySummary,
  ModelDescriptor,
  ParseResult,
  PendingQuestion,
  PipelineEventJson,
  PromptPreview,
  RunDetail,
  RunSummary,
  ServerInfo,
  StageVisits,
  Telemetry,
  WorkspaceDocument,
  WorkspaceFile,
} from "./types";

const TOKEN_KEY = "reactor.token";

export const getToken = () => localStorage.getItem(TOKEN_KEY) ?? "";
export const setToken = (token: string) => (token ? localStorage.setItem(TOKEN_KEY, token) : localStorage.removeItem(TOKEN_KEY));

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly body: unknown,
  ) {
    super(message);
  }
}

let unauthorizedHandler: (() => void) | null = null;
export const onUnauthorized = (handler: () => void) => (unauthorizedHandler = handler);

function headers(json: boolean): HeadersInit {
  const h: Record<string, string> = {};
  if (json) h["Content-Type"] = "application/json";
  const token = getToken();
  if (token) h.Authorization = `Bearer ${token}`;
  return h;
}

async function request<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, { method, headers: headers(body !== undefined), body: body === undefined ? undefined : JSON.stringify(body), signal });
  } catch (error) {
    if ((error as Error).name === "AbortError") throw error;
    throw new ApiError("The Reactor server is not reachable.", 0, null);
  }
  if (response.status === 401) {
    unauthorizedHandler?.();
    throw new ApiError("This server requires an API token.", 401, null);
  }
  const text = await response.text();
  let parsed: unknown = text;
  if ((response.headers.get("content-type") ?? "").includes("json") && text) {
    try {
      parsed = JSON.parse(text);
    } catch {
      parsed = text;
    }
  }
  if (!response.ok) {
    const message = typeof parsed === "object" && parsed && "error" in parsed ? String((parsed as { error: unknown }).error) : `${response.status} ${response.statusText}`;
    throw new ApiError(message, response.status, parsed);
  }
  return parsed as T;
}

export interface StartRunBody {
  dot: string;
  context?: Record<string, unknown>;
  studio?: Record<string, unknown>;
  simulation?: Record<string, unknown>;
  /** The run's cost budget in US dollars (default: the server's, else the pipeline's `max_cost`). */
  max_cost?: number;
}

export interface StartRunResponse {
  id: string;
  status: string;
  name: string;
  logs_root: string;
}

export const api = {
  info: () => request<ServerInfo>("GET", "/studio/api/info"),
  parse: (dot: string, signal?: AbortSignal) => request<ParseResult>("POST", "/studio/api/graph/parse", { dot }, signal),
  promptPreview: (dot: string, node: string, history: string[], fidelity?: string) =>
    request<PromptPreview>("POST", "/studio/api/graph/prompt-preview", { dot, node, history, fidelity }),
  models: () => request<{ models: ModelDescriptor[]; error?: string }>("GET", "/studio/api/models"),
  workspace: () => request<{ root: string; files: WorkspaceFile[] }>("GET", "/studio/api/workspace"),
  readFile: (path: string) => request<WorkspaceDocument>("GET", `/studio/api/workspace/file?path=${encodeURIComponent(path)}`),
  saveFile: (path: string, dot: string, layout: unknown, baseModified?: string | null) =>
    request<{ path: string; modified: string }>("PUT", "/studio/api/workspace/file", { path, dot, layout, base_modified: baseModified ?? undefined }),
  startRun: (body: StartRunBody) => request<StartRunResponse>("POST", "/pipelines", body),
  runs: () => request<RunSummary[]>("GET", "/pipelines"),
  run: (id: string) => request<RunDetail>("GET", `/pipelines/${encodeURIComponent(id)}`),
  cancel: (id: string) => request<unknown>("POST", `/pipelines/${encodeURIComponent(id)}/cancel`),
  /** Resumes a stopped run from its checkpoint; `maxCost` sets a new budget (to go on after a budget stop). */
  resume: (id: string, maxCost?: number) =>
    request<RunDetail>("POST", `/pipelines/${encodeURIComponent(id)}/resume`, maxCost !== undefined ? { max_cost: maxCost } : undefined),
  questions: (id: string) => request<PendingQuestion[]>("GET", `/pipelines/${encodeURIComponent(id)}/questions`),
  answer: (id: string, questionId: string, value?: string, text?: string) =>
    request<unknown>("POST", `/pipelines/${encodeURIComponent(id)}/questions/${encodeURIComponent(questionId)}/answer`, { value, text }),
  stage: (id: string, node: string) =>
    request<{ node: string; status?: string; outcome: unknown; files: { name: string; size: number; modified: string }[] }>(
      "GET",
      `/pipelines/${encodeURIComponent(id)}/stages/${encodeURIComponent(node)}`,
    ),
  stageFile: (id: string, node: string, name: string) =>
    request<string | object>("GET", `/pipelines/${encodeURIComponent(id)}/stages/${encodeURIComponent(node)}/files/${encodeURIComponent(name)}`),
  history: (name?: string, limit = 200) =>
    request<{ runs: HistorySummary[] }>("GET", `/studio/api/runs?limit=${limit}${name ? `&name=${encodeURIComponent(name)}` : ""}`).then((r) => r.runs),
  historyRun: (dir: string) => request<HistoryDetail>("GET", `/studio/api/runs/${encodeURIComponent(dir)}`),
  historyEvents: (dir: string, since = 0, limit = 20000) =>
    request<{ events: PipelineEventJson[]; next: number; done: boolean }>("GET", `/studio/api/runs/${encodeURIComponent(dir)}/events?since=${since}&limit=${limit}`),
  historyFile: (dir: string, node: string, name: string, visit?: number, attempt?: number) => {
    const query = [visit !== undefined ? `visit=${visit}` : "", attempt !== undefined ? `attempt=${attempt}` : ""].filter(Boolean).join("&");
    return request<string | object>(
      "GET",
      `/studio/api/runs/${encodeURIComponent(dir)}/stages/${encodeURIComponent(node)}/files/${encodeURIComponent(name)}${query ? `?${query}` : ""}`,
    );
  },
  /** Every visit of a stage in a run directory, with its files and its retried attempts. */
  stageVisits: (dir: string, node: string) => request<StageVisits>("GET", `/studio/api/runs/${encodeURIComponent(dir)}/stages/${encodeURIComponent(node)}/visits`),
  telemetry: (name: string, limit = 50) => request<Telemetry>("GET", `/studio/api/telemetry?name=${encodeURIComponent(name)}&limit=${limit}`),
  fork: (dir: string, body: ForkRequest) => request<ForkResponse>("POST", `/studio/api/runs/${encodeURIComponent(dir)}/fork`, body),
  steer: (id: string, message: string, node?: string) =>
    request<{ status: string; node: string; path: string }>("POST", `/studio/api/pipelines/${encodeURIComponent(id)}/steer`, { message, node }),
  get: <T>(path: string) => request<T>("GET", path),
  post: <T>(path: string, body?: unknown) => request<T>("POST", path, body ?? {}),
};

export interface EventStreamHandlers {
  onEvent: (event: PipelineEventJson) => void;
  onEnd?: (status: string) => void;
  onError?: (error: Error) => void;
}

/**
 * Streams server-sent events with fetch (so the bearer token travels in a header and every event name is handled).
 * Reconnects with Last-Event-ID after transient failures. Returns a function that closes the stream.
 */
export function streamEvents(path: string, handlers: EventStreamHandlers, since = 0): () => void {
  const controller = new AbortController();
  let lastId = since;
  let closed = false;

  const run = async (attempt: number): Promise<void> => {
    try {
      const h: Record<string, string> = { Accept: "text/event-stream" };
      const token = getToken();
      if (token) h.Authorization = `Bearer ${token}`;
      if (lastId > 0) h["Last-Event-ID"] = String(lastId);
      const response = await fetch(path, { headers: h, signal: controller.signal });
      if (response.status === 401) {
        unauthorizedHandler?.();
        throw new ApiError("unauthorized", 401, null);
      }
      if (!response.ok || !response.body) throw new ApiError(`${response.status}`, response.status, null);
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";
      let ended = false;
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        let boundary: number;
        while ((boundary = buffer.indexOf("\n\n")) >= 0) {
          const frame = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary + 2);
          let event = "message";
          let data = "";
          let id: number | null = null;
          for (const line of frame.split("\n")) {
            if (line.startsWith(":")) continue;
            const colon = line.indexOf(":");
            const field = colon < 0 ? line : line.slice(0, colon);
            const val = colon < 0 ? "" : line.slice(colon + 1).replace(/^ /, "");
            if (field === "event") event = val;
            else if (field === "data") data += (data ? "\n" : "") + val;
            else if (field === "id") id = Number(val);
          }
          if (!data) continue;
          if (id !== null && Number.isFinite(id)) lastId = id;
          if (event === "end") {
            ended = true;
            try {
              handlers.onEnd?.(JSON.parse(data).status);
            } catch {
              handlers.onEnd?.("unknown");
            }
            continue;
          }
          try {
            const parsed = JSON.parse(data) as PipelineEventJson;
            handlers.onEvent(parsed);
          } catch {
            // ignore malformed frames
          }
        }
      }
      if (!ended && !closed) {
        await new Promise((r) => setTimeout(r, 1000));
        return run(0);
      }
    } catch (error) {
      if (closed || (error as Error).name === "AbortError") return;
      if ((error as ApiError).status === 401 || (error as ApiError).status === 404) {
        handlers.onError?.(error as Error);
        return;
      }
      if (attempt < 20) {
        await new Promise((r) => setTimeout(r, Math.min(5000, 500 * 2 ** attempt)));
        return run(attempt + 1);
      }
      handlers.onError?.(error as Error);
    }
  };

  void run(0);
  return () => {
    closed = true;
    controller.abort();
  };
}
