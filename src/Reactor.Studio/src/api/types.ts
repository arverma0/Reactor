import type { Attrs, Diagnostic, Effective, HandlerType, LayoutSidecar } from "../model/types";

export interface ParsedNode {
  id: string;
  attributes: Attrs;
  explicit: string[];
  classes: string[];
  subgraph: number | null;
  handler: HandlerType;
  effective: Effective;
}

export interface ParsedEdge {
  from: string;
  to: string;
  attributes: Attrs;
}

export interface ParsedSubgraph {
  index: number;
  name: string | null;
  parent: number;
  label: string | null;
  class: string | null;
  attributes: Attrs;
  node_defaults: Attrs;
  edge_defaults: Attrs;
  nodes: string[];
}

export interface ParsedGraph {
  name: string;
  attributes: Attrs;
  node_defaults: Attrs;
  edge_defaults: Attrs;
  nodes: ParsedNode[];
  edges: ParsedEdge[];
  subgraphs: ParsedSubgraph[];
}

export type ParseResult =
  | { ok: true; valid: boolean; graph: ParsedGraph; diagnostics: Diagnostic[] }
  | { ok: false; error: string; message: string; line: number; column: number };

export interface ServerInfo {
  name: string;
  version: string;
  working_directory: string;
  runs_directory: string;
  backend: { kind: string; description: string; routes: string[]; status_file: boolean };
  handler_types: string[];
  fidelity_modes: string[];
  auth: boolean;
  features: string[];
}

export interface ModelDescriptor {
  id: string;
  provider: string;
  source: "catalog" | "copilot" | string;
  display_name?: string | null;
  context_window?: number | null;
  reasoning_efforts: string[];
  supports_tools?: boolean | null;
  supports_reasoning?: boolean | null;
  billing_multiplier?: number | null;
  policy_state?: string | null;
  available: boolean;
}

export interface WorkspaceFile {
  path: string;
  name: string;
  size: number;
  modified: string;
  has_layout: boolean;
  graph_name?: string | null;
  goal?: string | null;
  node_count?: number | null;
  error?: string | null;
}

export interface WorkspaceDocument {
  path: string;
  dot: string;
  layout: Partial<LayoutSidecar> | null;
  modified: string;
}

export interface PromptPreview {
  node: string;
  handler: string;
  fidelity: string;
  status_protocol: boolean;
  prompt: string;
}

export interface QuestionOption {
  key: string;
  label: string;
}

export interface PendingQuestion {
  id: string;
  text: string;
  type: "multiple_choice" | "yes_no" | "confirmation" | "freeform";
  options: QuestionOption[];
  stage: string;
  timeout_seconds?: number | null;
  default?: string | null;
  asked_at: string;
}

export type RunStatus = "queued" | "running" | "success" | "partial_success" | "fail" | "cancelled" | "error";

export interface RunSummary {
  id: string;
  name: string;
  goal: string;
  status: RunStatus;
  current_node?: string | null;
  created_at: string;
  started_at?: string | null;
  finished_at?: string | null;
  pending_questions: number;
  simulated?: boolean;
  simulation_mode?: string;
}

export interface RunDetail extends Omit<RunSummary, "pending_questions"> {
  completed_nodes: string[];
  node_statuses: Record<string, string>;
  duration_seconds?: number | null;
  logs_root: string;
  working_directory: string;
  pending_questions: PendingQuestion[];
  event_count: number;
  error?: string | null;
  result?: { status: string; cancelled: boolean; notes: string; failure_reason: string; loop_restarts: number; completed_nodes: string[] } | null;
  studio?: Record<string, unknown> | null;
}

export interface RunCost {
  ai_units?: number;
  premium_requests?: number;
  cost_usd?: number;
  input_tokens?: number;
  output_tokens?: number;
  reasoning_tokens?: number;
  tool_calls?: number;
}

export interface ForkOrigin {
  /** Run directory of the source run. */
  id: string;
  checkpoint: string;
  node: string;
  run_id: string;
}

/** A run read from disk by the history API (`/studio/api/runs`); `id` is the run directory name. */
export interface HistorySummary {
  id: string;
  run_id: string;
  name: string;
  goal?: string | null;
  started_at?: string | null;
  finished_at?: string | null;
  status: string;
  duration_ms?: number | null;
  node_count?: number | null;
  stage_executions?: number | null;
  failure_reason?: string | null;
  live_id?: string | null;
  restarts: string[];
  source_path?: string | null;
  simulated?: boolean;
  forked_from?: ForkOrigin | null;
  cost?: RunCost | null;
}

export interface HistoryStage {
  node: string;
  visits: number;
  status?: string | null;
  duration_ms?: number | null;
  backend?: Record<string, unknown> | null;
  files: string[];
  history_visits: number;
  /** Directory (run or loop-restart directory) holding the latest visit's files. */
  segment?: string | null;
}

export interface HistoryDetail {
  summary: HistorySummary;
  manifest?: Record<string, unknown> | null;
  result?: { status?: string; cancelled?: boolean; failure_reason?: string; notes?: string } | null;
  checkpoint?: { current_node?: string; completed_nodes: string[]; node_outcomes: Record<string, string>; final_status?: string | null } | null;
  graph_dot?: string | null;
  studio?: { layout?: LayoutSidecar; source_path?: string; forked_from?: ForkOrigin } & Record<string, unknown>;
  stages: HistoryStage[];
  checkpoints: string[];
}

export interface Percentiles {
  mean: number;
  p50: number;
  p95: number;
  max?: number;
}

export interface TelemetryNode {
  runs: number;
  visits: number;
  mean_visits: number;
  outcomes: Record<string, number>;
  fail_rate?: number;
  retries: number;
  duration_ms?: Percentiles;
  cost?: Record<string, number>;
  models?: Record<string, number>;
  top_failures?: { reason: string; count: number }[];
}

export interface Telemetry {
  name: string;
  runs: number;
  window?: { from: string; to: string };
  outcomes: Record<string, number>;
  duration_ms?: Percentiles;
  cost?: Record<string, { mean: number; total: number }>;
  nodes: Record<string, TelemetryNode>;
  edges: { from: string; to: string; count: number; runs: number }[];
  series: { id: string; started_at?: string; status: string; duration_ms?: number; stage_executions?: number; ai_units?: number }[];
}

export interface ForkResponse {
  id: string;
  status: string;
  name: string;
  logs_root: string;
  forked_from: ForkOrigin;
  diagnostics: Diagnostic[];
}

/** An engine event as streamed over SSE / stored in events.jsonl (snake_case fields + type + seq). */
export interface PipelineEventJson {
  type: string;
  seq?: number;
  timestamp: string;
  run_id?: string;
  description?: string;
  node_id?: string;
  label?: string;
  index?: number;
  handler_type?: string;
  attempt?: number;
  duration?: string;
  status?: string;
  notes?: string;
  error?: string;
  will_retry?: boolean;
  message?: string;
  level?: string;
  from?: string;
  to?: string;
  reason?: string;
  question_id?: string;
  question?: string;
  stage?: string;
  options?: QuestionOption[];
  answer?: string;
  name?: string;
  goal?: string;
  logs_root?: string;
  branch?: string;
  success?: boolean;
  [key: string]: unknown;
}
