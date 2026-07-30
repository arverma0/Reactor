# Backends

A **backend** (`ICodergenBackend`) executes `codergen` stages: it receives the node, the fully built prompt, and the
run context, and returns response text or an explicit outcome. The pipeline definition does not change between
backends.

```csharp
public interface ICodergenBackend
{
    Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken cancellationToken);
    bool SupportsStatusFile => false;                       // true: the agent can write status.json
    bool SupportsStatusFileFor(Node node) => SupportsStatusFile;
    bool SupportsSteering => false;                         // true: steering.md reaches the running agent
    bool SupportsSteeringFor(Node node) => SupportsSteering;
}
```

Every backend in this repository writes `backend.json` metadata (model, tokens, cost/AI units, session id, tool
counts) to the stage directory, forwards agent activity (`→ tool args`, `✗ tool error`, warnings) to the run's
event stream as `StageLog` events, and maps failures to exceptions carrying a `Retryable` flag so the engine retries
only what can succeed on retry (rate limits, 5xx, network) and not authentication, policy, or configuration errors.
An attempt that fails, times out, or is cancelled attaches what it used so far to its exception
(`BackendMetadata.Attach`: model, tokens, model and tool calls, AI units or premium requests, session id, and the
agent's last message), so its `backend.json` is still written (with `outcome` and `error`) and the last message goes
to `response.partial.md`. Steering (`SupportsSteering`) is supported by `agent` and `copilot`; `BackendRouter`
answers for the backend each node routes to.

**Threads.** Stages with `full` fidelity on one thread share a backend session (the conversation, and for agents the
tool state) for the duration of one engine execution; a resumed run or a `loop_restart` starts fresh sessions. If a
thread's session is lost — a timeout or abort killed it, it was evicted after idling, or parallel branches forked the
thread — the next stage on that thread starts a new session with a `summary:high` preamble of the run so far instead
of starting blind. The prompt actually sent is then saved as `prompt.effective.md`, and `backend.json` records
`thread_context` (`new`, `continued`, or `recovered`).

## Choosing a backend

`reactor run --backend <name>` picks the default; a `BackendRouter` then routes each node:

1. `backend="<name>"` on the node (must name a configured route; otherwise the stage fails with the list of routes);
2. `llm_provider` when a route of that name exists (e.g. `anthropic` routes to the agent loop when
   `ANTHROPIC_API_KEY` is set);
3. otherwise the default.

Routes registered by the CLI: `agent`, `direct`, and each native provider with a key (`openai`, `anthropic`,
`gemini`, `openai-compatible`); `copilot`/`copilot-sdk`; `copilot-cli` and `claude-code`/`claude` when the CLIs are
installed. `--model` sets the default model for stages without `llm_model`.

## Standing instructions

Every backend honors the stage's standing instructions — Reactor's built-in house rules, the graph's and the node's
`instructions` (see [pipelines.md](pipelines.md#standing-instructions-reactor-extension)) — and writes them to
`{stage}/instructions.md`:

| Backend | Delivered as |
|---|---|
| `agent` | the session's user instructions in the system prompt (plus the legacy `agent.instructions`) |
| `direct` | appended to the system message (no house rules: the backend has no tools) |
| `copilot` | appended to the Copilot session's system message (`SystemMessageConfig`, append mode) when the session is created; a later stage on the same thread whose instructions differ gets them at the top of its prompt |
| `copilot-cli` | a delimited `# Instructions` section at the top of the prompt (the CLI has no system-prompt option), once per session — again only when they change on a resumed thread |
| `claude-code` | `--append-system-prompt` (on one line when the executable is a batch file; instructions over 16,000 characters go into the prompt instead) |

A tool call cut off at the model's output-token limit ("Tool call arguments truncated by the model output token
limit") loses that whole generation. The `copilot`, `copilot-cli`, and `claude-code` backends log it as a stage
warning that explains what happened and count it in `backend.json` (`output_limit_truncations`).

## Usage and cost in `backend.json`

Backends keep the raw usage fields they get — `input_tokens` (including cached), `output_tokens`,
`cache_read_tokens`, `cache_write_tokens`, `reasoning_tokens`, `model_calls`, `tool_calls`, and Copilot's
`ai_units` (AI credits) and `premium_requests` — and add one comparable figure:

| Field | Meaning |
|---|---|
| `cost_usd` | List price of the stage's usage in USD. |
| `cost_basis` | What it was computed from: `ai_units` (× $0.01 per AI credit), `premium_requests` (legacy request-based billing; × the `premium_request_usd` of a `REACTOR_PRICES` file, as the built-in table has no premium request price), or `tokens` (× per-model token prices). |
| `cost_estimated` | `true` when Reactor derived the quantity itself (tokens × a price table, or premium requests from a model multiplier) rather than taking a figure the runtime reported. |

| Backend | `cost_usd` from |
|---|---|
| `copilot` | the AI units the runtime reports; tokens × the price table when it reports none |
| `copilot-cli` | the AI units in the CLI's usage checkpoint (which also yields tokens and model calls); for a killed run, the session's on-disk usage records, else premium requests estimated from the model's multiplier (priced only when `premium_request_usd` is set) |
| `claude-code` | `total_cost_usd` from Claude Code's result; for a killed run, the per-message usage parsed so far × the model catalog's prices |
| `agent`, `direct` | tokens × the model catalog's `input_cost_per_million`/`output_cost_per_million` (cache tokens at `cached_input_cost_per_million`/`cache_write_cost_per_million` when set, else the input rate) |

**The Copilot price table.** Reactor ships the per-model token prices of GitHub Copilot's usage-based billing
(`src/Reactor.Llm/Catalog/copilot-prices.json`, with source URLs and retrieval dates; long-context tiers and legacy
premium-request multipliers where documented). It fills in prices when the runtime returns no billing data
(`reactor models --provider copilot`, `GET /studio/api/models`) and prices token estimates. Point
`REACTOR_PRICES` at a JSON file with the same shape to add or replace entries (entries replace built-in ones by id;
`"replace": true` drops the built-in entries); a file that cannot be read is reported by `reactor models` and ignored.

**Attempts.** Each attempt of a stage opens its transcript (`agent-events.jsonl`, `copilot-events.jsonl`,
`cli-events.jsonl`) with `{"type":"reactor.attempt","attempt":N,"ts":"…"}` (N = the engine's retry count + 1), records
Reactor writes carry `ts`, and `backend.json` records `attempt`. Backends do not rely on stage files surviving between
attempts.

## `agent` — the coding agent loop

`AgentLoopBackend` runs each stage as a `Reactor.Agent` session (the Coding Agent Loop spec):

- **Profile** by provider: OpenAI models get the codex-style tool set (`apply_patch`, `shell`, …), Anthropic models
  the Claude Code-style set (`edit_file`, 120 s shell timeout), Gemini the gemini-cli set (`read_many_files`,
  `list_dir`, `web_fetch`, …). Unknown providers (OpenAI-compatible endpoints) use the OpenAI set.
- **Model resolution**: node `llm_provider` → provider inferred from `llm_model` → `--provider` → the client's
  default; model: node `llm_model` → `--model` → the catalog's newest tool-capable model for the provider.
- **Reasoning effort**: an explicit `reasoning_effort` (attribute or stylesheet) is sent unless the catalog marks the
  model as non-reasoning. Without one, models the catalog knows to reason get the spec default `high`; unknown models
  (typical for OpenAI-compatible servers, which often reject the parameter) get none.
- **Threads**: `full`-fidelity stages on one thread share a session (conversation and tool state). Parallel branches
  that contend for the same thread get their own session instead of waiting.
- **Budget**: 200 tool rounds per stage unless `agent.max_tool_rounds` (or `SessionConfig.MaxToolRoundsPerInput`)
  sets another limit (`AgentLoopBackendOptions.DefaultMaxToolRounds`; 0 = unlimited). Output tokens per response
  come from `agent.max_tokens`, else `--max-tokens`, else the provider's default; a response cut off at the limit is
  logged as a warning.
- **Status file**: the prompt tells the agent where `status.json` lives; if the agent stops at a turn limit without
  writing it, the stage fails ("stopped at its turn limit") instead of silently succeeding, so a stuck agent cannot
  run unattended forever.
- **Steering**: a supervisor (`house` node) or the server (`POST /studio/api/pipelines/{id}/steer`) writes
  `steering.md` into the stage directory; the backend polls for it (every second, `SteeringPollInterval`), renames it
  to `steering.consumed-N.md`, and injects the text into the running session before its next model request.
- **Hooks**: `tool_hooks.pre/post` run around every tool call (see [pipelines.md](pipelines.md#tool-hooks)).
- **Artifacts**: `transcript.md` (the conversation) and `agent-events.jsonl` (every session event).
- Node attributes: `agent.max_tool_rounds`, `agent.max_turns`, `agent.max_tokens`, `agent.instructions` (legacy:
  appended to the system prompt after the standing `instructions`), `agent.shell_timeout`.

Credentials come from the environment (`Client.FromEnv`): `OPENAI_API_KEY` (+`OPENAI_BASE_URL`, `OPENAI_ORG_ID`,
`OPENAI_PROJECT_ID`), `ANTHROPIC_API_KEY` (+`ANTHROPIC_BASE_URL`), `GEMINI_API_KEY`/`GOOGLE_API_KEY`
(+`GEMINI_BASE_URL`), `OPENAI_COMPATIBLE_BASE_URL` (+`OPENAI_COMPATIBLE_API_KEY`). The CLI raises the adapter
timeouts for agent work (stream gaps up to 5 minutes during long reasoning).

## `direct` — one LLM call per stage

`DirectLlmBackend` sends the prompt as a single request without tools — useful for planning, critique, and
summaries. It cannot write files, so the status-file protocol is not offered. `full`-fidelity threads share a
message history. Route individual nodes to it with `backend="direct"`; `--max-tokens` limits each response.

## `copilot` — GitHub Copilot SDK

`CopilotSdkBackend` drives the GitHub Copilot runtime through the `GitHub.Copilot.SDK` package. The `reactor` CLI
bundles the runtime (`runtimes/<rid>/native/copilot`); `COPILOT_CLI_PATH` or `--copilot-runtime` override it.
Authentication is the signed-in Copilot user (sign in once with the `copilot` CLI) or a `GitHubToken` option.

- One runtime process per backend; one Copilot session per stage, or per `full`-fidelity thread (later stages
  continue the conversation; a model change on the thread calls `SetModelAsync`).
- **Model verification**: before a stage starts, the requested model is checked against the account's model list.
  An unavailable or policy-disabled model fails the stage immediately (not retried) with the list of available
  models — without this, the runtime silently substitutes another, possibly more expensive, model. If the runtime
  still reports a different model, the stage log warns and `backend.json` records `requested_model` and `model`.
  `reactor models --provider copilot` lists what your account can use.
- Copilot model names use dots (`claude-sonnet-4.5`); Anthropic-style ids (`claude-sonnet-4-5`,
  `claude-opus-4-6-20260101`) are converted. `reasoning_effort` is passed only when the node sets it and the model
  supports that level.
- Tool permissions are approved automatically; `tool_hooks` run through the SDK's pre/post tool-use hooks and can
  deny a call. Directories a stage needs outside its working directory (its stage directory) are added to the
  session's allow-list.
- `SendAndWaitAsync` is bounded by the node `timeout` (plus 30 s) or 4 hours; cancellation aborts the Copilot turn.
- **Steering**: while the turn runs, a `steering.md` in the stage directory (from a supervisor or the server's steer
  endpoint) is taken like on the `agent` backend and sent with `SendAsync(new MessageOptions { Mode = "immediate" })`,
  which the runtime folds into the turn in progress. Nothing is taken once the turn has ended; a message that arrives
  in the instant the turn ends is logged as not sent.
- Usage (input/output/cached/cache-write/reasoning tokens, AI units) is summed per stage into `backend.json`; activity
  is in `copilot-events.jsonl` (including each call's `nano_aiu` and the runtime's `usage.checkpoint` totals).
- **AI units per attempt**: the runtime reports AI units as a cumulative total per session, so each stage — and each
  attempt of a stage — records the difference from what earlier attempts on the session were already charged. When a
  stage ends or is aborted with model calls not yet covered by a checkpoint, the backend waits briefly
  (`UsageSettleTimeout`, 3 s) for the closing checkpoint, and counts calls still uncovered from their per-call usage;
  a checkpoint that arrives between stages is not charged to the next one. A timed-out attempt thus keeps its own
  units, and a retry on the same thread reports only its own.

## `copilot-cli` — the GitHub Copilot CLI

`CopilotCliBackend` runs `copilot --output-format json --allow-all-tools --no-ask-user --session-id <uuid>` per
stage with the prompt on stdin, in the run's working directory.

- The npm `copilot.cmd` shim on Windows is unwrapped to `node npm-loader.js` so arguments are never re-parsed by
  `cmd.exe`. Set `REACTOR_COPILOT_CLI` or `--copilot-cli` to use a specific executable.
- `--session-id` both creates and resumes sessions, so `full`-fidelity threads keep one Copilot session. The CLI
  reports cumulative usage per session (premium requests, and in a usage checkpoint, when it emits one, AI units and
  per-model tokens and request counts); the backend records each stage's delta. A run killed before it reports usage
  (timeout, cancel) is accounted from the session's usage records in
  `<COPILOT_HOME>/session-state/<session id>/events.jsonl` (`usage_source: "session-state"`), else its premium
  requests are estimated from the price table's multiplier (`premium_requests_estimated`).
- The stage directory is added with `--add-dir` when it lies outside the working directory, so the agent can write
  `status.json`.
- An unknown model fails immediately (the CLI rejects it) and is not retried.
- Raw JSON events (minus streaming deltas) are kept in `cli-events.jsonl`; stderr in `stderr.log`.
- Cancelling the run, or a stage `timeout`, terminates the CLI's whole process tree, including shells and servers the
  agent started (a job object on Windows, a process group elsewhere); this holds for `claude-code` too. When the CLI
  exits by itself, processes it left running are not touched.

## `claude-code` — Claude Code

`ClaudeCodeBackend` runs `claude -p --output-format stream-json --verbose --dangerously-skip-permissions` with the
prompt on stdin (or `--permission-mode acceptEdits` with `SkipPermissions = false`).

- New threads start with `--session-id <uuid>`; later stages use `--resume`. If a failed run already created the
  session (for example "Not logged in"), the next attempt resumes it instead of colliding with it.
- Claude aliases (`sonnet`, `opus`, `haiku`) and Anthropic ids pass through (dotted versions are converted); a
  non-Claude `llm_model` falls back to the CLI default with a warning. `reasoning_effort` maps to `--effort`
  low/medium/high.
- Cost, turns, and token usage from the final `result` event go to `backend.json`; a killed run keeps the token
  usage of the messages it had streamed.
- Authentication, permission, and model errors are not retried.

## `simulate`

No backend: codergen stages succeed with `[Simulated] Response for stage: <id>`. Human gates, tool nodes, parallel
branches, and manager loops still run, so this is the quickest way to check a pipeline's routing. Simulated stages
write no files, so their declared file `outputs` are recorded without the existence check.

## Writing your own

Implement `ICodergenBackend`, then pass it to `PipelineRunnerOptions.Backend` (or add it to a `BackendRouter`):

```csharp
sealed class EchoBackend : ICodergenBackend
{
    public Task<CodergenResult> RunAsync(Node node, string prompt, PipelineContext context, CancellationToken ct) =>
        Task.FromResult(CodergenResult.FromText($"echo: {node.Id}").WithMetadata(new Dictionary<string, object?> { ["backend"] = "echo" }));
}

var router = new BackendRouter(new EchoBackend(), "echo").Add("copilot", new CopilotSdkBackend());
var runner = new PipelineRunner(new PipelineRunnerOptions { Backend = router });
```

Return `CodergenResult.FromOutcome(Outcome.Fail("reason"), text)` to fail a stage explicitly, and throw an exception
with a `bool Retryable` property to control engine retries. Helpers in `Reactor.Backends`: `StageSettings.From(node,
context)` (model, effort, fidelity, thread, directories), `ModelResolution`, `ToolHookInterceptor`, and the CLI
plumbing (`CommandResolver`, `CliProcess`, `CliAgentBackend`) for wrapping other agent CLIs.
