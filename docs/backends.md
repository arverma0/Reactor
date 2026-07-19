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
}
```

Every backend in this repository writes `backend.json` metadata (model, tokens, cost/AI units, session id, tool
counts) to the stage directory, forwards agent activity (`→ tool args`, `✗ tool error`, warnings) to the run's
event stream as `StageLog` events, and maps failures to exceptions carrying a `Retryable` flag so the engine retries
only what can succeed on retry (rate limits, 5xx, network) and not authentication, policy, or configuration errors.

## Choosing a backend

`reactor run --backend <name>` picks the default; a `BackendRouter` then routes each node:

1. `backend="<name>"` on the node (must name a configured route; otherwise the stage fails with the list of routes);
2. `llm_provider` when a route of that name exists (e.g. `anthropic` routes to the agent loop when
   `ANTHROPIC_API_KEY` is set);
3. otherwise the default.

Routes registered by the CLI: `agent`, `direct`, and each native provider with a key (`openai`, `anthropic`,
`gemini`, `openai-compatible`); `copilot`/`copilot-sdk`; `copilot-cli` and `claude-code`/`claude` when the CLIs are
installed. `--model` sets the default model for stages without `llm_model`.

## `agent` — the coding agent loop

`AgentLoopBackend` runs each stage as a `Reactor.Agent` session (the Coding Agent Loop spec):

- **Profile** by provider: OpenAI models get the codex-style tool set (`apply_patch`, `shell`, …), Anthropic models
  the Claude Code-style set (`edit_file`, 120 s shell timeout), Gemini the gemini-cli set (`read_many_files`,
  `list_dir`, `web_fetch`, …). Unknown providers (OpenAI-compatible endpoints) use the OpenAI set.
- **Model resolution**: node `llm_provider` → provider inferred from `llm_model` → `--provider` → the client's
  default; model: node `llm_model` → `--model` → the catalog's newest tool-capable model for the provider.
- **Threads**: `full`-fidelity stages on one thread share a session (conversation and tool state). Parallel branches
  that contend for the same thread get their own session instead of waiting.
- **Status file**: the prompt tells the agent where `status.json` lives; if the agent stops at a turn limit without
  writing it, the stage fails ("stopped at its turn limit") instead of silently succeeding.
- **Steering**: a supervisor (`house` node) writes `steering.md` into the stage directory; the backend polls for it
  and injects it into the running session.
- **Hooks**: `tool_hooks.pre/post` run around every tool call (see [pipelines.md](pipelines.md#tool-hooks)).
- **Artifacts**: `transcript.md` (the conversation) and `agent-events.jsonl` (every session event).
- Node attributes: `agent.max_tool_rounds`, `agent.max_turns`, `agent.instructions` (appended to the system prompt),
  `agent.shell_timeout`.

Credentials come from the environment (`Client.FromEnv`): `OPENAI_API_KEY` (+`OPENAI_BASE_URL`, `OPENAI_ORG_ID`,
`OPENAI_PROJECT_ID`), `ANTHROPIC_API_KEY` (+`ANTHROPIC_BASE_URL`), `GEMINI_API_KEY`/`GOOGLE_API_KEY`
(+`GEMINI_BASE_URL`), `OPENAI_COMPATIBLE_BASE_URL` (+`OPENAI_COMPATIBLE_API_KEY`). The CLI raises the adapter
timeouts for agent work (stream gaps up to 5 minutes during long reasoning).

## `direct` — one LLM call per stage

`DirectLlmBackend` sends the prompt as a single request without tools — useful for planning, critique, and
summaries. It cannot write files, so the status-file protocol is not offered. `full`-fidelity threads share a
message history. Route individual nodes to it with `backend="direct"`.

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
- Usage (input/output/cached/reasoning tokens, AI units) is summed per stage into `backend.json`; activity is in
  `copilot-events.jsonl`.

## `copilot-cli` — the GitHub Copilot CLI

`CopilotCliBackend` runs `copilot --output-format json --allow-all-tools --no-ask-user --session-id <uuid>` per
stage with the prompt on stdin, in the run's working directory.

- The npm `copilot.cmd` shim on Windows is unwrapped to `node npm-loader.js` so arguments are never re-parsed by
  `cmd.exe`. Set `REACTOR_COPILOT_CLI` or `--copilot-cli` to use a specific executable.
- `--session-id` both creates and resumes sessions, so `full`-fidelity threads keep one Copilot session. The CLI
  reports cumulative premium requests per session; the backend records each stage's delta.
- The stage directory is added with `--add-dir` when it lies outside the working directory, so the agent can write
  `status.json`.
- An unknown model fails immediately (the CLI rejects it) and is not retried.
- Raw JSON events (minus streaming deltas) are kept in `cli-events.jsonl`; stderr in `stderr.log`.

## `claude-code` — Claude Code

`ClaudeCodeBackend` runs `claude -p --output-format stream-json --verbose --dangerously-skip-permissions` with the
prompt on stdin (or `--permission-mode acceptEdits` with `SkipPermissions = false`).

- New threads start with `--session-id <uuid>`; later stages use `--resume`. If a failed run already created the
  session (for example "Not logged in"), the next attempt resumes it instead of colliding with it.
- Claude aliases (`sonnet`, `opus`, `haiku`) and Anthropic ids pass through (dotted versions are converted); a
  non-Claude `llm_model` falls back to the CLI default with a warning. `reasoning_effort` maps to `--effort`
  low/medium/high.
- Cost, turns, and token usage from the final `result` event go to `backend.json`.
- Authentication, permission, and model errors are not retried.

## `simulate`

No backend: codergen stages succeed with `[Simulated] Response for stage: <id>`. Human gates, tool nodes, parallel
branches, and manager loops still run, so this is the quickest way to check a pipeline's routing.

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
