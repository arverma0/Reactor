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
  /** API clients may choose a run's working directory (`--allow-client-workdir`). */
  allow_client_working_directory?: boolean;
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
  /** USD per 1M tokens (Copilot: Reactor's price table; API models: the model catalog). */
  input_cost_per_million?: number | null;
  cached_input_cost_per_million?: number | null;
  cache_write_cost_per_million?: number | null;
  output_cost_per_million?: number | null;
  price_source?: "price_table" | "catalog" | string | null;
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
  working_directory?: string | null;
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
  result?: {
    status: string;
    cancelled: boolean;
    notes: string;
    failure_reason: string;
    loop_restarts: number;
    completed_nodes: string[];
    /** Usage totals over every attempt (the engine's ledger). */
    usage?: RunCost;
    /** The run stopped before its next stage because its recorded cost reached its budget. */
    budget_exceeded?: boolean;
  } | null;
  /** The run's cost budget in US dollars (from the start or resume request); null = the server default or the pipeline's. */
  max_cost?: number | null;
  studio?: Record<string, unknown> | null;
}

export interface RunCost {
  cost_usd?: number;
  ai_units?: number;
  premium_requests?: number;
  input_tokens?: number;
  output_tokens?: number;
  cache_read_tokens?: number;
  reasoning_tokens?: number;
  model_calls?: number;
  tool_calls?: number;
}

/** A run stopped at its cost budget (`BudgetExceeded`): what it had spent, its budget, and the stage it stopped before. */
export interface BudgetStopInfo {
  spent_usd: number;
  limit_usd: number;
  next_node?: string | null;
  at?: string | null;
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
  /** The run's working directory (from its manifest); null for run directories that did not record it. */
  working_directory?: string | null;
  simulated?: boolean;
  forked_from?: ForkOrigin | null;
  cost?: RunCost | null;
  /** The final value of each outcome metric (`metrics.<name>`). */
  metrics?: Record<string, number>;
  /** Failures that flowed onward along unconditional edges (`FailedStageContinued`). */
  failures_continued?: number;
  /** Stages whose failure flowed onward and that never succeeded afterwards. */
  failed_stages?: string[];
  /** Times a stage's `max_visits` rerouted the run. */
  visit_limits?: number;
  /** Set when the run's latest stop was its cost budget. */
  budget?: BudgetStopInfo | null;
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
  /** Counts from the latest visit's changes.json (what the stage changed in its working directory). */
  changes?: StageChangesSummary | null;
  /** Retried attempts archived (`attempts/attempt-N`) across the visits in that directory. */
  attempts?: number;
  /** Usage summed over every visit and attempt of the stage. */
  cost?: RunCost;
  /** Outcome metrics per visit that reported any (visits count from 1 across the run). */
  metrics?: { visit: number; metrics: Record<string, number> }[];
  failures_continued?: number;
  visit_limits?: number;
}

export interface StageChangesSummary {
  added: number;
  modified: number;
  deleted: number;
  out_of_scope: number;
  mode?: string | null;
  concurrent: boolean;
  truncated: boolean;
}

/** One attempt of a stage visit, as its files describe it (`GET /studio/api/runs/{id}/stages/{node}/visits`). */
export interface AttemptArchive {
  /** `timeout`, `cancelled`, or `error` (from backend.json), else the status.json outcome (`fail`, `retry`, …). */
  outcome?: string | null;
  error?: string | null;
  files: string[];
  backend?: Record<string, unknown> | null;
}

export interface VisitArchive extends AttemptArchive {
  /** Visit number in this run directory; the latest visit is the stage directory itself. */
  visit: number;
  latest: boolean;
  /** The attempts the engine retried before the visit's last attempt (whose files are the visit's own). */
  attempts: (AttemptArchive & { attempt: number })[];
}

export interface StageVisits {
  node: string;
  run: string;
  visits: VisitArchive[];
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
  /** The run's `workspace.json`: how its working directory was snapshotted (`git` or `walk`), absent when tracking was off. */
  workspace?: ({ mode?: "git" | "walk"; directory?: string; repository?: string; prefix?: string; ref?: string } & Record<string, unknown>) | null;
  /** The `checkpoints` saved with a git snapshot of the working directory: a fork from one of them can rewind the directory. */
  workspace_checkpoints?: string[];
}

export interface Percentiles {
  mean: number;
  p50: number;
  p95: number;
  max?: number;
}

/** Statistics of a metric's values: `count` values (or, for run-level metrics, `runs` reporting it), mean, extremes, and the newest. */
export interface MetricStats {
  count?: number;
  runs?: number;
  mean: number;
  min: number;
  max: number;
  last: number;
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
  /** Failures of the stage that flowed onward along an unconditional edge. */
  failures_continued?: number;
  /** Landed flights with this stage's failure behind them. */
  failed_stage_landings?: number;
  /** Times its `max_visits` rerouted a flight. */
  visit_limits?: number;
  /** Every value the stage reported, per metric. */
  metrics?: Record<string, MetricStats>;
}

export interface TelemetryPoint {
  id: string;
  started_at?: string;
  status: string;
  duration_ms?: number;
  stage_executions?: number;
  ai_units?: number;
  premium_requests?: number;
  cost_usd?: number;
  /** Final value of each outcome metric. */
  metrics?: Record<string, number>;
  failures_continued?: number;
  failed_stages?: string[];
  budget_exceeded?: boolean;
}

export interface Telemetry {
  name: string;
  runs: number;
  window?: { from: string; to: string };
  outcomes: Record<string, number>;
  duration_ms?: Percentiles;
  cost?: Record<string, { mean: number; total: number }>;
  /** Flights that landed with failed stages behind them (a failure flowed on and was never made good). */
  failed_stage_landings?: number;
  /** Flights whose latest stop was their cost budget. */
  budget_stops?: number;
  /** Per outcome metric, its final value over the flights that reported it. */
  metrics?: Record<string, MetricStats>;
  nodes: Record<string, TelemetryNode>;
  edges: { from: string; to: string; count: number; runs: number }[];
  series: TelemetryPoint[];
}

export interface ForkRequest {
  checkpoint: string;
  dot?: string;
  context?: Record<string, unknown>;
  /** Instead of the source run's working directory. */
  working_directory?: string;
  /** Required to fork into a directory other than the source run's. */
  confirm_working_directory?: boolean;
  /**
   * Rewind the working directory to the git snapshot saved with `checkpoint` before the fork starts (the current state
   * is snapshotted first: `workspace_restore.undo_commit`). Only for `workspace_checkpoints`.
   */
  restore_workspace?: boolean;
}

/** What a fork's `restore_workspace` did to its working directory. */
export interface WorkspaceRestore {
  directory: string;
  /** The checkpoint whose workspace snapshot was restored. */
  checkpoint: string;
  /** The snapshot commit restored. */
  target: string;
  /** The snapshot of the directory taken just before the rewind: restore it to undo the rewind. */
  undo_commit: string;
  undo_ref: string;
  /** A git command that brings the pre-rewind files back. */
  undo_command: string;
  written: number;
  deleted: number;
  /** The first changed paths. */
  paths: string[];
}

export interface ForkResponse {
  id: string;
  status: string;
  name: string;
  logs_root: string;
  working_directory: string;
  /** Where `working_directory` came from: the source run, the request, or the server (the source run did not record one). */
  working_directory_source: "source_run" | "request" | "server";
  source_working_directory?: string | null;
  working_directory_note?: string | null;
  forked_from: ForkOrigin;
  /** Set when the fork rewound its working directory (`restore_workspace`). */
  workspace_restore?: WorkspaceRestore | null;
  diagnostics: Diagnostic[];
}

/** Why a fork could not rewind its working directory (`409`). */
export type WorkspaceRestoreErrorCode = "rewind_unavailable" | "workspace_snapshot_missing" | "workspace_mismatch" | "rewind_failed" | "workspace_busy";

/** Body of a fork refused because its working directory could not be rewound (`409`). */
export interface ForkRestoreError {
  error: string;
  code: WorkspaceRestoreErrorCode;
  working_directory?: string;
  source_working_directory?: string;
  server_working_directory?: string;
}

/** Body of a fork refused because of its working directory (`403`/`409`). */
export interface ForkWorkdirError {
  error: string;
  code: "working_directory_not_allowed" | "working_directory_missing" | "working_directory_confirmation_required";
  working_directory?: string;
  source_working_directory?: string;
  server_working_directory?: string;
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
