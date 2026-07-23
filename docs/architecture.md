# Architecture

## Projects

```text
                        ┌─────────────┐
                        │ Reactor.Cli │  reactor run/serve/agent/...
                        └──────┬──────┘
          ┌──────────────┬─────┴───────┬───────────────┐
          ▼              ▼             ▼               ▼
 ┌────────────────┐ ┌──────────┐ ┌───────────────┐ ┌──────────────────┐
 │ Reactor.Server │ │ Reactor. │ │ Reactor.      │ │ GitHub.Copilot.  │
 │ HTTP + SSE +   │ │ Copilot  │─▶ Backends      │ │ SDK (+ bundled   │
 │ Studio, console│ │ (SDK)    │ │ agent/direct/ │ │ runtime)         │
 └───────┬────────┘ └────┬─────┘ │ CLI agents,   │ └──────────────────┘
         │               │       │ router, hooks │
         │               │       └──┬─────────┬──┘
         │               │          ▼         ▼
         │               │   ┌─────────────┐ ┌─────────────┐
         │               │   │Reactor.Agent│─▶ Reactor.Llm │
         │               │   │agent loop,  │ │unified LLM  │
         │               │   │tools, envs  │ │client       │
         │               │   └─────────────┘ └─────────────┘
         ▼               ▼
 ┌──────────────────────────────────────────┐
 │ Reactor.Core (src/Reactor)               │  no dependencies
 │ graphs, parser, validation, engine,      │
 │ handlers, human interaction, events      │
 └──────────────────────────────────────────┘
```

`Reactor.Core` knows nothing about LLMs: codergen stages call an `ICodergenBackend`, and retry classification
reads a public `bool Retryable` property from exceptions, so the Reactor.Llm error hierarchy (and the backends'
exceptions) plug in without a dependency. `Reactor.Agent` and `Reactor.Llm` are usable without the engine.

## Engine (Reactor.Core)

```text
DOT ──DotParser──▶ Graph ──transforms──▶ Graph ──PipelineValidator──▶ diagnostics ──PipelineRunner──▶ PipelineResult
                         ($goal expansion,                (23 rules; errors
                          model stylesheet,               stop the run)
                          custom IGraphTransform)
```

`PipelineRunner.RunAsync` creates a `PipelineExecution` per run, which owns the context, checkpoint, history, and
event bus, and runs the loop from the spec (section 3.2):

1. **Terminal check** — at the exit node, verify goal gates (jump to `retry_target` / fail when unsatisfied), run the
   exit handler, checkpoint, finish.
2. **Execute** — resolve the handler (`type`, then `shape`, then the default codergen handler), apply the node's retry
   policy (attempts, backoff with jitter, `Retryable` classification), the stage `timeout`, and record the outcome to
   `status.json`. Handlers may return `null` to let the engine read an agent-written `status.json`.
3. **Record** — apply `context_updates`, set `outcome`/`preferred_label`/`failure_reason`, append to history,
   save `checkpoint.json` atomically (write-then-rename).
4. **Route** — `EdgeSelector` implements the 5-step priority; `FidelityResolver` decides the next stage's fidelity
   and thread. Failures follow the same selection, then retry targets. `loop_restart` edges start a fresh run in a
   sibling directory.

Supporting pieces:

- `PipelineContext` — thread-safe JSON-compatible key/value store; branches get deep clones. `RunServices`
  (logs root, events, backend, interviewer, artifact store, branch executor, child launcher) hangs off it for
  handlers but is never checkpointed.
- **Parallel** — the parallel handler runs each branch through `IBranchExecutor` (the engine's own traversal, on a
  cloned context) with a `max_parallel` semaphore until branches reach a fan-in, exit, or dead end; results go to
  `parallel.results`, artifact records made in the branches are merged back in branch order, and the outcome jumps to
  the join node.
- **Child pipelines** — the manager handler starts a child run through `IChildPipelineLauncher` (same runner,
  backend, and interviewer, separate logs root) and observes it via its events.
- **Human interaction** — handlers only see `IInterviewer` (`AskAsync(Question)`); console, queue, callback,
  auto-approve, recording, and the server's web interviewer are interchangeable. Timeouts cancel the ask.
- **Events** — typed records (`StageStarted`, `StageLog`, …) flow through `PipelineEventBus` to the run's
  `events.jsonl`, `RunOptions.OnEvent`, and any `IPipelineEventSink`.
- **Artifact pins** (Reactor extension) — `ArtifactPins` parses the `inputs`/`outputs` node attributes;
  `StageArtifacts` renders a codergen prompt's Inputs/Outputs sections, enforces the output contract on successful
  stages (a missing declared file turns the outcome into FAIL, so retries and routing apply), and adds versioned
  `artifacts.<name>` records to the outcome's context updates (failing tool stages still record their stdout). The
  engine announces each record with an `ArtifactProduced` event when it records the outcome, and the parallel
  handler merges records made in branches back into the parent context. The codergen and tool handlers call it;
  custom handlers can too. Not to be confused with `ArtifactStore`, the spec's store for large values.
- **Resume** — `ResumeAsync(logsRoot)` reloads `checkpoint.json` (context, completed nodes, retry counts, node
  outcomes, last fidelity/thread, history) and continues after the checkpointed node; a `full`-fidelity stage right
  after resume degrades to `summary:high` because agent sessions are not persisted.
- **Simulation** — `Reactor.Simulation` (the Studio's wind tunnel) runs the real engine on a dedicated runner whose
  codergen, tool, manager, and custom handlers return scenario-drawn outcomes; human gates and parallel nodes keep their
  real handlers. Draws are a pure function of (seed, sample, node, execution), so Monte Carlo samples replay exactly as
  traces; samples run in memory (`PipelineRunnerOptions.PersistRunState = false`), without any engine file I/O.

## Agent loop (Reactor.Agent)

`Session.SubmitAsync(input)` implements the Coding Agent Loop spec:

```text
user input ─▶ [steering] ─▶ LLM request (system prompt + history + tools)
                              │  stream text deltas, accumulate tool calls
                              ▼
                     tool calls? ──no──▶ done (or AWAITING_INPUT when the reply is a question)
                              │yes
                              ▼
       validate args (JSON schema) ─▶ interceptors (hooks/approval) ─▶ execute (parallel when the profile allows)
                              │  truncate output for the model (full output in events)
                              ▼
               tool results ─▶ steering ─▶ loop detection ─▶ limits (rounds/turns) ─▶ next LLM request
```

- **Provider profiles** pair a tool set and system prompt with a model family (OpenAI/codex, Anthropic/Claude Code,
  Gemini/gemini-cli); custom tools register on the profile's `ToolRegistry`.
- **Execution environments** (`IExecutionEnvironment`) isolate where tools run: `LocalExecutionEnvironment`
  (process trees killed on timeout/abort, sensitive environment variables filtered, ripgrep when available), plus
  logging, read-only, and scoped (subdirectory) wrappers.
- **Subagents** (`spawn_agent`, `send_input`, `wait`, `close_agent`) run child sessions sharing the environment,
  with a depth limit; their events are forwarded with an `agent_id`.
- Errors follow the spec: provider retries in the LLM layer; authentication and other non-retryable errors close
  the session; context-window overflow is a warning; aborts kill processes, close subagents, and emit
  `SESSION_END`.

## Unified LLM client (Reactor.Llm)

`Client` routes a `Request` to a provider adapter by name (never guessing a provider), applying middleware (onion
order) and lazy adapter initialization. Adapters translate the unified message model — text, images, documents,
tool calls/results, thinking blocks with signatures — to each native API and back, both for complete responses and
for streams (start/delta/end events, tool-call deltas, finish with usage). The high-level `Ai` API adds tool
execution loops, retries with backoff and `Retry-After`, timeouts, and structured output
(`GenerateObjectAsync` + `OutputAs<T>()` via native JSON schema, a forced tool, or prompt-and-parse).

## Backends and hosting

`Reactor.Backends` adapts agents to `ICodergenBackend` (see [backends.md](backends.md)); `BackendRouter` composes
them per node. `Reactor.Server` wraps the runner in a `PipelineRunManager` (run registry, concurrency gate, event
journal with gap-free replay, web interviewer per run) and minimal-API endpoints; its `RunHistory` reads run
directories from disk (cached per directory, refreshed incrementally) for the Studio's history, telemetry, and forks.
`Reactor.Cli` composes everything from command-line options and the environment.

## Reactor Studio

`src/Reactor.Studio` is a React/TypeScript single-page app (React Flow canvas, CodeMirror, zustand stores), built by
Vite during `dotnet build` and embedded into `Reactor.Server` as resources served at `/` (see [studio.md](studio.md)).
It keeps no rules of its own about pipelines: parsing, effective models, validation, prompts, simulation, and runs all
go through the Studio API (`Reactor.Server/Studio`), which calls the same parser, transforms, validator, runner, and
simulator as the CLI.

| Studio | Studio API (`Reactor.Server/Studio`) | Engine |
|---|---|---|
| Design: document model, DOT writer, canvas, inspector | `StudioApi`: parse with editor metadata, prompt preview, models, workspace files | `DotParser`, transforms, `PipelineValidator`, `CodergenHandler.BuildPrompt` |
| Wind tunnel | `SimulationEndpoints`: Monte Carlo reports, single-flight traces | `Reactor.Simulation` on in-memory runs |
| Flights | `/pipelines` and SSE; `FlightsEndpoints`: history, telemetry, forks, steering | `PipelineRunManager`, `RunHistory`, checkpoint snapshots |
| Architect | `ArchitectApi`: runs `architect.dot` (a design stage and a check stage) | the server's backend, `PipelineValidator`, `MonteCarloSimulator` |

The document model (`StudioDoc`) mirrors DOT's structure — nodes with their explicit attributes, edges, subgraphs as
groups, default blocks — so the writer can produce the same graph the engine parsed (checked by round-tripping every
example through the server's parser). Everything visual lives in the `.layout.json` sidecar.

## Testing

| Project | Focus |
|---|---|
| `Reactor.Tests` | parser, conditions, stylesheet, validation rules, engine routing/retries/goal gates/parallel/resume/manager, handlers, artifact pins, examples (incl. the spec smoke test) |
| `Reactor.Llm.Tests` | request/response/stream translation per provider against recorded payloads, errors, retries, high-level API, object generation |
| `Reactor.Agent.Tests` | tools (read/write/edit/apply_patch/shell/grep/glob/web_fetch), environments, truncation, the session loop, subagents, profiles |
| `Reactor.Backends.Tests` | agent backend in real pipelines with real tools and a scripted model, CLI backends against fake CLIs replaying recorded output, hooks, routing |
| `Reactor.Copilot.Tests` | SDK backend orchestration through a fake runtime (threads, models, errors, hooks) |
| `Reactor.Server.Tests` | the HTTP API on an in-memory test server: runs, SSE, human gates, cancel/queue/resume, auth, Studio API, run history, telemetry, forks, steering, simulation, the Architect (with a scripted backend) |
| Studio (Vitest) | DOT writer and importer, routes, conditions, stylesheet, doc diffs, fork checkpoints, flight merging, wind tunnel calibration, Architect evidence |

Live runs against real agents (Copilot SDK, Copilot CLI) were used to validate the end-to-end behavior described
in these documents; see [spec-conformance.md](spec-conformance.md).
