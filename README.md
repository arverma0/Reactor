# Reactor

Reactor is a C#/.NET implementation of [StrongDM's Attractor](https://github.com/strongdm/attractor): you describe a
multi-stage AI workflow as a Graphviz **DOT** graph, and Reactor runs it — calling coding agents at each stage,
routing on outcomes, retrying, pausing for human approval, fanning out in parallel, and checkpointing so any run can
be resumed.

It implements all three Attractor specs:

| Spec | Reactor project | What it is |
|---|---|---|
| [Attractor](https://github.com/strongdm/attractor/blob/main/attractor-spec.md) | `Reactor.Core` (`src/Reactor`) | DOT parser, validation, execution engine, handlers, checkpoints, human gates, events |
| [Coding Agent Loop](https://github.com/strongdm/attractor/blob/main/coding-agent-loop-spec.md) | `Reactor.Agent` | Agentic loop with provider-aligned tool sets (apply_patch, edit_file, shell, grep, …), subagents |
| [Unified LLM Client](https://github.com/strongdm/attractor/blob/main/unified-llm-spec.md) | `Reactor.Llm` | One client for OpenAI (Responses API), Anthropic, Gemini, and OpenAI-compatible endpoints |

On top of that it adds stage **backends** for the GitHub Copilot SDK, the GitHub Copilot CLI, and Claude Code, an
**HTTP server**, **Reactor Studio** — a visual workbench to design, simulate, fly, and measure pipelines — and the
`reactor` command-line tool.

```dot
digraph test_pipeline {
    graph [goal="Create a hello world Python script"]

    start       [shape=Mdiamond]
    plan        [shape=box, prompt="Plan how to create a hello world script for: $goal"]
    implement   [shape=box, prompt="Write the code based on the plan", goal_gate=true]
    review      [shape=box, prompt="Review the code for correctness"]
    done        [shape=Msquare]

    start -> plan -> implement
    implement -> review [condition="outcome=success"]
    implement -> plan   [condition="outcome=fail", label="Retry"]
    review -> done      [condition="outcome=success"]
    review -> implement [condition="outcome=fail", label="Fix"]
}
```

```text
$ reactor run examples/smoke.dot --backend copilot --model gpt-5-mini
backend: GitHub Copilot SDK, default model gpt-5-mini
▶ Pipeline 'test_pipeline' — Create a hello world Python script
● plan [codergen]
✔ plan success (11.7s)
● implement [codergen]
  │ → create hello.py
✔ implement success (7.8s)
● review [codergen]
  │ → view hello.py
✔ review success (11.1s)
✔ Pipeline completed (success) in 30.7s
  logs:   .reactor/runs/20260718-192803-test_pipeline
```

## Quick start

Requirements: the .NET 10 SDK. Optional: [Graphviz](https://graphviz.org) for graph rendering, and whichever agent
backend you want to use (see below).

```powershell
git clone <this repository> reactor && cd reactor
dotnet build -c Release
dotnet run --project src/Reactor.Cli -- doctor          # what backends/tools are available?
dotnet run --project src/Reactor.Cli -- run examples/smoke.dot --backend simulate
```

`reactor` can also be installed as a .NET tool: `dotnet pack src/Reactor.Cli -c Release` then
`dotnet tool install -g Reactor.Cli --add-source src/Reactor.Cli/bin/Release`. The package bundles the GitHub
Copilot runtime for the platform it was built on; elsewhere, point `COPILOT_CLI_PATH` at a runtime or use the other
backends.

### Backends

Each `codergen` stage (a `box` node) is handed to a **backend**. Pick one with `--backend` (default `auto`):

| Backend | Uses | Needs |
|---|---|---|
| `agent` | Reactor's own coding agent loop (Coding Agent Loop spec) | `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`/`GOOGLE_API_KEY`, or `OPENAI_COMPATIBLE_BASE_URL` |
| `direct` | One LLM call per stage, no tools (planning/review stages) | same as `agent` |
| `copilot` | GitHub Copilot SDK (runtime bundled with the CLI) | a GitHub account with Copilot (signed in via `copilot`) |
| `copilot-cli` | the `copilot` CLI as a subprocess | `copilot` on PATH |
| `claude-code` | Claude Code (`claude -p`) as a subprocess | `claude` on PATH, logged in |
| `simulate` | no LLM; stages return placeholder text | nothing |

`auto` picks `agent` when an API key is set, else `copilot`, `copilot-cli`, `claude-code`, then `simulate`.
Individual nodes can override the default with `backend="copilot-cli"` or route by `llm_provider` — see
[docs/backends.md](docs/backends.md).

To use a local model, point `OPENAI_COMPATIBLE_BASE_URL` at an OpenAI-compatible server (llama.cpp's
`llama-server`, Ollama, vLLM, …) and run with `--backend agent --provider openai-compatible --model <name>`. Add
`--max-tokens` (e.g. 2048) and stage `timeout`s: local servers often let a response run until the context window is
full, which takes a long time when a small model starts repeating itself. Small local models handle simple stages but
are far less reliable than frontier models at multi-step coding work.

## Reactor Studio

```powershell
reactor studio --backend copilot      # serves the current directory and opens http://127.0.0.1:5180/
```

Reactor Studio is a visual workbench for agent factories, built on the real engine:

- **Design** pipelines on a canvas where every route is a port (`✓ on success`, `✕ on fail`, gate options, labels),
  with groups, artifact wires, a model stylesheet editor, a two-way DOT editor, and an exact prompt preview.
- **Wind tunnel**: fly the design hundreds of times with simulated stage outcomes — zero tokens — to find loops that
  never converge, dead ends, unreachable goal gates, and failures that silently flow on; forecast time and cost per
  flight, and calibrate it to your real flights to forecast what a change will do to the clean landing rate, time, and
  cost. Also from the command line: `reactor simulate`.
- **Flights**: launch runs, answer human gates on the canvas, steer running agents, replay any run (from this server
  or `reactor run`) on a timeline, re-fly a run from just before any stage with an edited pipeline, and read each
  pipeline's flight record (success, time, cost, where it breaks).
- **Architect**: describe a factory, or ask for a revision or a critique backed by the flight record; every draft is
  checked by the parser, the validator, and the wind tunnel before you see it, and comes with a forecast.

See [docs/studio.md](docs/studio.md).

## Writing pipelines

A pipeline is a `digraph`. Node **shape** selects the handler:

| Shape | Handler | Does |
|---|---|---|
| `Mdiamond` | start | entry point (exactly one) |
| `Msquare` | exit | terminal; checks goal gates (exactly one) |
| `box` (default) | codergen | runs the stage prompt through the backend |
| `hexagon` | wait.human | asks a human to pick an outgoing edge |
| `diamond` | conditional | routes on the previous outcome/context |
| `component` | parallel | fans out to its targets concurrently |
| `tripleoctagon` | parallel.fan_in | joins parallel branches |
| `parallelogram` | tool | runs `tool_command` in a shell |
| `house` | stack.manager_loop | supervises a child pipeline |

Edges route on `condition="outcome=success"`, `condition="context.tests_passed=true && outcome!=fail"`, a
`label` matching the stage's preferred label, `weight`, and finally lexical order. Agents report non-default
outcomes by writing `status.json` in their stage directory (the prompt tells them how).

Stages can also declare the artifacts they read and produce (`inputs="spec, design"`,
`outputs="design:docs/DESIGN.md, verdict"`): prompts list them, a stage that does not write a declared file fails
(and can be retried), and each produced artifact is recorded in the context and announced as an `ArtifactProduced`
event — see [artifacts](docs/pipelines.md#artifacts-reactor-extension).

Full reference: [docs/pipelines.md](docs/pipelines.md). Examples in [examples/](examples):

| Example | Shows |
|---|---|
| [smoke.dot](examples/smoke.dot) | the spec's integration smoke test |
| [human-review.dot](examples/human-review.dot) | a human approval gate with timeout/default |
| [test-fix-loop.dot](examples/test-fix-loop.dot) | a tool node running tests, failures loop to a fixer |
| [parallel-review.dot](examples/parallel-review.dot) | parallel fan-out/fan-in |
| [stylesheet.dot](examples/stylesheet.dot) | per-node models via a CSS-like `model_stylesheet` |
| [supervisor.dot](examples/supervisor.dot) | a manager loop observing and steering a child pipeline |
| [factory.dot](examples/factory.dot) | a 15-node build/test/review/sign-off pipeline |
| [artifacts.dot](examples/artifacts.dot) | artifact pins: stages declare `inputs`/`outputs` (spec → design → code → test report → verdict) |

## Command line

```text
reactor run <file.dot>        Run a pipeline        (--backend, --model, -C <workdir>, --set k=v, --answer, -y, --json, -v/-q, --logs <dir>, --max-cost <usd>, --no-track-changes)
reactor resume <run-dir>      Resume from the last checkpoint (same flags; --max-cost raises a budget the run stopped at)
reactor validate <file.dot>   Parse and lint (--simulate: every simulated happy-path flight must land)
reactor simulate <file.dot>   Fly it in the wind tunnel: many simulated runs, zero tokens
                              (--runs 1000, --scenario happy|realistic|stress|chaos|<file.json>, --seed, --max-steps,
                               --min-success 0.95, --trace <run>, --set k=v, --json)
reactor render <file.dot>     Render to SVG/PNG/PDF/DOT (-o graph.png picks the format; --run <run-dir> colors stages by status)
reactor serve                 HTTP API + Reactor Studio + console (--urls, --token, --max-concurrent, --max-cost, and run's backend flags)
reactor studio                Same server, and opens the Studio in a browser
reactor agent [prompt]        Talk to the coding agent loop directly
reactor models                List catalog models with their token prices (--provider copilot: models your Copilot
                              account can use, priced from Reactor's Copilot price table; REACTOR_PRICES=<file> overrides it)
reactor doctor                Check backends, credentials, and tools
```

Runs are written to `<workdir>/.reactor/runs/<timestamp>-<name>/` (the timestamp is UTC, `yyyyMMdd-HHmmss`, as for
runs started by the server), or to the directory given with `--logs` — then nothing is created under
`<workdir>/.reactor`. A run directory holds `checkpoint.json`, `events.jsonl`, `result.json`, `graph.dot`, and one
directory per stage with `prompt.md`, `response.md`, `status.json`, `instructions.md` (the stage's standing
instructions), `backend.json` (model, tokens, and cost — native units plus a comparable `cost_usd`, also for attempts
that time out or fail) and agent transcripts. The run summary ends with the run's usage totals (cost, AI units or premium requests, tokens — failed and
timed-out attempts included); `--max-cost <usd>` (or the graph's `max_cost`) stops a run, resumably, before its next
stage once its recorded cost reaches the budget. Every stage's checkpoint is kept under `checkpoints/`, so the Studio
can re-fly a CLI run from any stage. Each stage that can change files also gets a `changes.json` (what it changed in
the working directory, from snapshots that never touch your git index or branches); `--no-track-changes` turns the
snapshots off for a run. Exit codes: 0 success, 1 pipeline failed, 2 invalid input, 3 configuration
error, 130 cancelled. Press Ctrl+C to stop gracefully; `reactor resume` continues.

House rules for agents go in the graph's (or a stage's, or a stylesheet rule's) `instructions` attribute; every backend
honors them, on top of a small built-in set (`house_rules=false` turns that off). See
[docs/pipelines.md](docs/pipelines.md#standing-instructions-reactor-extension).

While parallel branches run, every activity line names the stage it comes from (in a color of its own unless
`--no-color` or `NO_COLOR` is set), so concurrent agents stay apart:

```text
● proto_a [codergen]
● proto_b [codergen]
  │ proto_a: copilot-cli session 358962f6-… (claude-sonnet-5)
  │ proto_b: copilot-cli session 4e960be1-… (gpt-5.6-sol)
  │ proto_b → view docs/GAME_DESIGN.md
```

`reactor render <file.dot> --run <run-dir>` colors the stages of a run: finished stages by their latest status (from
`checkpoint.json` and `events.jsonl`), and the stages executing now — several while parallel branches run, none once
the run has ended — as running. The output format follows `-o`'s extension (`.svg`, `.png`, `.pdf`, `.jpg`, `.gif`,
`.json`, `.ps`, `.eps`, or `.dot`/`.gv` for the display DOT, which needs no Graphviz); `-f` must agree with it.

`reactor simulate` is the Studio's wind tunnel from the command line: it flies the pipeline through the real engine
with simulated stage outcomes (no model calls, no tool commands) and prints the readiness gauge — the **clean landing
rate** with its 95% interval, and the flights that landed with failed stages behind them — how flights go down (each
failure group with the stage and a replayable run), hot spots and loops, and simulated time and cost per flight. The
presets assume rough typical stage durations (agents 10 min, tools 1 min, supervisors 30 min); a scenario file can
start from a preset and set per-stage `duration_ms`, `cost` (USD per run), outcome `metrics`, and more (see
[http-api.md](docs/http-api.md#scenarios)):

```powershell
reactor simulate factory.dot --scenario realistic --min-success 0.95   # CI gate: exit 1 below 95% clean landings
reactor simulate factory.dot --seed 4242 --trace 17                    # replay flight 17 of that report, event by event
reactor simulate factory.dot --scenario my-scenario.json --json        # the report as POST /studio/api/simulate returns it
reactor validate factory.dot --simulate                                # exit 2 if any happy-path flight cannot land
```

`reactor simulate` exits 1 when the clean landing rate is below `--min-success`, and 2 on an invalid pipeline or
scenario.

## HTTP server

`reactor serve` exposes the spec's HTTP mode (section 9.5), Reactor Studio at `/`, and the classic web console at
`/console` for watching runs live and answering human gates:

```powershell
reactor serve --backend copilot --token $env:REACTOR_SERVER_TOKEN
curl -H "Authorization: Bearer $token" -H "Content-Type: text/vnd.graphviz" --data-binary @examples/smoke.dot http://127.0.0.1:5180/pipelines
```

Endpoints: `POST /pipelines`, `GET /pipelines/{id}`, `GET /pipelines/{id}/events` (SSE with replay),
`POST /pipelines/{id}/cancel`, `/resume`, `GET /pipelines/{id}/graph`, `/questions`,
`POST /pipelines/{id}/questions/{qid}/answer`, `/checkpoint`, `/context`, plus `POST /run`, `GET /status`,
`POST /answer`. The Studio API (`/studio/api/...`) adds parsing with editor metadata, prompt previews, workspace
files, a run history read from disk (including CLI runs), event replay, per-stage telemetry across runs, forking a run
from any stage, steering a running agent, Monte Carlo simulation, and the Architect. See
[docs/http-api.md](docs/http-api.md).

## Using the libraries

```csharp
using Reactor.Backends;
using Reactor.Execution;
using Reactor.Llm;

await using var client = Client.FromEnv();                       // OPENAI_API_KEY / ANTHROPIC_API_KEY / ...
await using var backend = new AgentLoopBackend(new AgentLoopBackendOptions { Client = client });
var runner = new PipelineRunner(new PipelineRunnerOptions { Backend = backend });

var result = await runner.RunAsync(File.ReadAllText("examples/smoke.dot"), new RunOptions { WorkingDirectory = "." });
Console.WriteLine($"{result.Status} — {string.Join(" → ", result.CompletedNodes)}");
```

The Unified LLM client works on its own too:

```csharp
var answer = await Ai.GenerateAsync(new GenerateOptions { Model = "claude-opus-4-6", Prompt = "Explain DOT in one line" });
```

## Repository layout

```text
src/Reactor          Reactor.Core: graph model, DOT parser, validation, engine, handlers, events, rendering
src/Reactor.Llm      Unified LLM client, provider adapters, high-level generate/stream/object APIs
src/Reactor.Agent    Coding agent loop: sessions, tools, execution environments, provider profiles, subagents
src/Reactor.Backends Codergen backends: agent loop, direct LLM, Copilot CLI, Claude Code, router, tool hooks
src/Reactor.Copilot  GitHub Copilot SDK backend
src/Reactor.Server   HTTP API, server-sent events, Studio API and hosting, web console
src/Reactor.Studio   Reactor Studio web app (React, TypeScript, React Flow), embedded into Reactor.Server
src/Reactor.Cli      the `reactor` command
tests/               xUnit v3 test projects (one per library, and one for the CLI)
examples/            example pipelines
docs/                architecture, pipeline reference, backends, Studio, HTTP API, spec conformance
```

## Development

```powershell
dotnet build                     # warnings are errors; builds and embeds the Studio when Node.js 20.19+ is installed
dotnet test                      # ~1,100 tests across seven projects
cd src/Reactor.Studio; npm test  # Studio unit tests (Vitest)
```

Skip the Studio build with `-p:SkipStudio=true` (or `REACTOR_SKIP_STUDIO=1`); see [docs/studio.md](docs/studio.md#building-the-studio).
Test projects are executables (xUnit v3), so a single suite can also run directly, e.g.
`tests/Reactor.Tests/bin/Debug/net10.0/Reactor.Tests.exe -class "Reactor.Tests.Engine.EngineTests"`.

Further reading: [architecture](docs/architecture.md) · [spec conformance and design decisions](docs/spec-conformance.md).
