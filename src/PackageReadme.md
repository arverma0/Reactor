# Reactor

Reactor is a C#/.NET implementation of [StrongDM's Attractor](https://github.com/strongdm/attractor): describe a
multi-stage AI workflow as a Graphviz DOT graph, and Reactor runs it, calling coding agents at each stage, routing on
outcomes, retrying, pausing for human approval, fanning out in parallel, and checkpointing so any run can be resumed.

```powershell
dotnet tool install -g Reactor.Cli
reactor doctor                                   # which backends and tools are available?
reactor run pipeline.dot --backend copilot       # run a pipeline
reactor studio                                   # design, simulate, run, and review pipelines in the browser
```

| Package | What it is |
|---|---|
| `Reactor.Cli` | the `reactor` command (a .NET tool) |
| `Reactor.Engine` | DOT parser, validation, execution engine, handlers, checkpoints, human gates, events |
| `Reactor.Llm` | one client for OpenAI, Anthropic, Gemini, and OpenAI-compatible endpoints |
| `Reactor.Agent` | coding agent loop with provider-aligned tools and subagents |
| `Reactor.Backends` | stage backends: the agent loop, direct LLM calls, the Copilot CLI, Claude Code |
| `Reactor.Copilot` | GitHub Copilot SDK backend |
| `Reactor.Server` | HTTP API, server-sent events, and Reactor Studio |

The `Reactor.Cli` package bundles the GitHub Copilot runtime for the platform it was built on, under the GitHub
Copilot CLI license (`runtimes/<rid>/native/LICENSE.md`). On other platforms, point `COPILOT_CLI_PATH` at a runtime or
use another backend.

Documentation, examples, and source: <https://github.com/arverma0/Reactor>. Licensed under the Apache License 2.0.
