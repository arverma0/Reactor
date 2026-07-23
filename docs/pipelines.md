# Pipeline reference

A Reactor pipeline is a Graphviz `digraph`. This page lists everything the engine understands; anything else is
ignored by the engine (and still rendered by Graphviz).

## DOT subset

- One `digraph` (optionally `strict`) per file; node ids are identifiers (`[A-Za-z_][A-Za-z0-9_]*`) or quoted strings.
- `graph [...]`, `node [...]`, `edge [...]` default blocks; top-level `key = value` graph attributes.
- Chained edges `a -> b -> c [label="x"]` create one edge per pair, all with the attributes.
- Subgraphs are flattened. Defaults declared inside a subgraph apply only inside it, and a subgraph `label` becomes a
  stylesheet class for its nodes (`label="Deep thinking"` → `.deep-thinking`).
- Values: quoted strings (with `\"`, `\n` escapes and `+` concatenation), identifiers, numbers, `true`/`false`, and
  durations (`250ms`, `30s`, `5m`, `2h`, `1d`).
- Comments: `//`, `/* */`, and `#` at the start of a line.

Errors report line and column: `reactor validate file.dot`.

## Graph attributes

| Attribute | Default | Meaning |
|---|---|---|
| `goal` | `""` | The pipeline goal; `$goal` in prompts expands to it, and it is mirrored to `graph.goal` in context. |
| `label` | `""` | Display name. |
| `model_stylesheet` | `""` | CSS-like rules assigning `llm_model`, `llm_provider`, `reasoning_effort` (see below). |
| `default_max_retries` | `0` | Extra attempts for nodes without `max_retries` (legacy alias `default_max_retry`). |
| `retry_target` / `fallback_retry_target` | `""` | Where to jump when a goal gate is unsatisfied at exit, or a failed node has nowhere to go. |
| `default_fidelity` | `compact` | Context carried into codergen prompts (see fidelity). |
| `default_thread_id` / `thread_id` | | Thread for `full` fidelity stages. |
| `stack.child_dotfile`, `stack.child_workdir` | | Child pipeline for manager nodes. |
| `tool_hooks.pre`, `tool_hooks.post` | | Shell commands around each agent tool call (see hooks). |

## Node attributes

| Attribute | Default | Meaning |
|---|---|---|
| `shape` | `box` | Selects the handler (table below). |
| `type` | | Explicit handler type; overrides `shape`. Custom handlers register under a type string. |
| `label` | node id | Display name; for `hexagon` nodes, the question text. |
| `prompt` | label | Task for codergen stages. `$goal` is expanded. |
| `max_retries` | graph default | Additional attempts on `retry`/`fail` outcomes or retryable errors. |
| `retry_policy` | | Preset backoff: `none`, `standard`, `aggressive`, `linear`, `patient`. |
| `goal_gate` | `false` | The pipeline may only exit once this node has succeeded. |
| `retry_target`, `fallback_retry_target` | | Per-node override of the graph's targets. |
| `timeout` | | Stage time limit (e.g. `10m`); the stage fails when exceeded. |
| `llm_model`, `llm_provider`, `reasoning_effort` | `reasoning_effort=high` for reasoning models | Model selection for the stage (usually via the stylesheet); see [backends](backends.md) for how each backend applies the effort. |
| `fidelity`, `thread_id` | | Context carry-over mode and conversation thread. |
| `class` | | Comma-separated stylesheet classes. |
| `auto_status` | `false` | Record SUCCESS automatically when the handler writes no status. |
| `allow_partial` | `false` | When retries are exhausted on `retry`, accept `partial_success` instead of failing. |
| `backend` | | Reactor extension: route this stage to a named backend (`agent`, `direct`, `copilot`, `copilot-cli`, `claude-code`). |
| `inputs` | | Reactor extension: artifacts the stage reads, e.g. `inputs="spec, design"` (see [Artifacts](#artifacts-reactor-extension)). |
| `outputs` | | Reactor extension: artifacts the stage produces — `name` (its final response, or a tool's stdout) or `name:path` (a file it must write), e.g. `outputs="design:docs/DESIGN.md, verdict"`. |

### Handler-specific attributes

| Handler (shape) | Attributes |
|---|---|
| wait.human (`hexagon`) | `human.timeout`, `human.default_choice` (target id, key, or label), `human.input="freeform"` with `human.question` |
| parallel (`component`) | `join_policy` = `wait_all` (default; `partial_success` if any branch fails) or `first_success` (cancels the other branches once one succeeds); `max_parallel` (default 4). Branch context changes are not merged back, except [artifact records](#artifacts-reactor-extension) |
| fan-in (`tripleoctagon`) | optional `prompt`: an LLM picks the best branch (reply `BEST: <id>`); otherwise ranked by status and score |
| tool (`parallelogram`) | `tool_command` (run with `cmd.exe /c` on Windows, `bash -c` elsewhere); `timeout` (default 10m); produced `inputs` arrive as `REACTOR_INPUT_<NAME>` file paths |
| manager (`house`) | `manager.poll_interval` (45s), `manager.max_cycles` (1000), `manager.stop_condition`, `manager.actions` (`observe,wait`; add `steer`), `manager.steer_cooldown` (5m), `manager.steer_prompt`, `stack.child_autostart`, `stack.child_logs_root` |
| codergen with the `agent` backend | `agent.max_tool_rounds` (default 200), `agent.max_turns`, `agent.max_tokens` (output tokens per response), `agent.instructions`, `agent.shell_timeout` |

## Edge attributes

| Attribute | Meaning |
|---|---|
| `label` | Display text; also matched against the stage's preferred label (accelerators like `[A] Approve`, `A) Approve`, `A - Approve` are understood). |
| `condition` | Boolean expression (below); the edge is eligible only when it is true. |
| `weight` | Higher wins among otherwise-equal candidates. |
| `fidelity`, `thread_id` | Override the target stage's fidelity/thread when arriving over this edge. |
| `loop_restart` | Restart the run from the target with a fresh context, in a new run directory (`<run>-restart-N`). |

## Routing

After a stage finishes, the engine picks the next edge in this order:

1. edges whose `condition` is true (highest `weight`, then target id);
2. an edge whose label matches the outcome's `preferred_label`;
3. an edge to one of the outcome's `suggested_next_ids`;
4. unconditional edges by highest `weight`;
5. lexical order of target ids.

A **failed** stage uses the same selection (so `condition="outcome=fail"` and unconditional edges both work); if
nothing is eligible it jumps to its `retry_target`, then `fallback_retry_target`, else the run fails. Human gates,
parallel and fan-in nodes only follow explicit routes on failure. Reaching the exit node succeeds when every
`goal_gate` node has succeeded; otherwise the engine jumps to the retry target or fails the run. Per the spec, a
failure that is routed onward does not by itself fail the run — `reactor run` lists such stages in its summary.

### Conditions

```text
outcome=success                          outcome != fail
preferred_label=Approve                  context.tests_passed=true && outcome=success
context.attempts                         (bare key: true when non-empty and not false/0/no/none/null)
```

Keys: `outcome`, `preferred_label`, and `context.<key>` (or a bare context key). Only `=`, `!=`, bare keys, and
`&&` are supported, as in the spec; `reactor validate` rejects anything else.

## Outcomes and the status file

Every stage ends with an outcome: `success`, `partial_success`, `retry`, `fail`, or `skipped`, plus optional
`preferred_label`, `suggested_next_ids`, `context_updates`, `notes`, and `failure_reason`. The engine writes it to
`<run>/<stage>/status.json`.

Agent backends are told (at the end of the prompt) that they may write that file themselves to report failure, ask
for a retry, or choose the next edge:

```json
{"outcome": "fail", "failure_reason": "3 tests fail", "preferred_label": "Fix", "context_updates": {"failing": "3"}}
```

If the agent does not write it, the stage succeeds with the agent's last response recorded in `last_response`.

## Context

A key/value store shared by all stages and saved in every checkpoint. Values are JSON-compatible. The engine sets:

| Key | Value |
|---|---|
| `outcome`, `preferred_label`, `failure_reason` | from the last stage |
| `current_node`, `last_stage`, `last_response` | progress |
| `graph.*` | graph attributes (e.g. `graph.goal`) |
| `internal.run_id`, `internal.fidelity`, `internal.thread_id`, `internal.retry_count.<node>` | engine state |
| `tool.output`, `tool.exit_code`, `tool.stderr`, `tool.duration_ms` | after a tool stage |
| `parallel.results`, `parallel.success_count`, `parallel.fail_count`, `parallel.fan_in.best_id`, … | after parallel/fan-in |
| `human.gate.selected`, `human.gate.label`, `human.gate.text` | after a human gate |
| `context.stack.child.*` | manager observations of the child run |
| `artifacts.<name>` | the latest record of each produced artifact (Reactor extension, see [Artifacts](#artifacts-reactor-extension)) |

Seed values from the command line with `reactor run --set ticket=ABC-123 --set retries=3` (values are parsed as JSON
when they look like JSON).

## Artifacts (Reactor extension)

Stages can declare typed data ports — the artifacts they read and produce — so a pipeline's data flow is explicit,
checked when the pipeline is validated and run, and visible in prompts and events:

```dot
spec      [prompt="Specify the feature", outputs="spec:docs/SPEC.md"]
architect [prompt="Design it", inputs="spec", outputs="design:docs/DESIGN.md"]
test      [shape=parallelogram, tool_command="python -m pytest -q", outputs="test_report"]
review    [prompt="Review the change", inputs="design, test_report", outputs="verdict"]
```

- `outputs`: comma-separated entries. `name:path` is a **file** the stage must write, relative to the run's working
  directory (`/` or `\` separators; `.` and `..` segments are resolved but may not leave the working directory). A
  bare `name` is a **text** artifact: a codergen stage's final response, or a tool's stdout.
- `inputs`: comma-separated artifact names.
- Names are identifiers (`[A-Za-z_][A-Za-z0-9_]*`); whitespace around entries and around the colon is ignored.
- Only codergen (`box`) and tool (`parallelogram`) stages use them; on other node types they are ignored.

[examples/artifacts.dot](../examples/artifacts.dot) wires a spec → design → implement → test → review factory this way.

**Prompts.** A codergen stage's prompt gets these sections after the task (before the status-file instructions):

```text
## Inputs
- `design` — file `docs/DESIGN.md` (written by stage `architect`). Read it.
- `test_report` — text from stage `test`:
  > 12 passed in 0.31s
- `verdict` — not produced yet in this run.

## Outputs
Produce these before you finish:
- `changes` → your final response is recorded as this artifact
```

Inputs resolve from the latest record in the context. Text is quoted up to about 4,000 characters (longer text keeps
its beginning and end around a `[… truncated]` line), and a version after the first is noted (`version 2, written by
stage …`). On a retry, the prompt also names the declared files that are still missing. Tool stages receive each
produced input as an environment variable `REACTOR_INPUT_<NAME>` holding a file path: the artifact's file, or
`<stage dir>/inputs/<name>.txt` with a text artifact's recorded text. Stages without `inputs`/`outputs` get exactly
the prompt they would get without this feature.

**Contract.** When a stage finishes with `success` or `partial_success` — reported by the backend, by an
agent-written `status.json`, or by a tool's exit code — every file output must exist. If one does not, the stage
fails with ``Declared output `design` (docs/DESIGN.md) was not written.``, which is retried (`max_retries`) and
routed like any other failure (`condition="outcome=fail"`, retry targets); the routing hints of the reported outcome
are dropped. Only existence is checked, so a file left by an earlier visit satisfies the contract. Text outputs
cannot be missing (empty text is recorded as is).

**Failures.** A codergen stage that fails records nothing. A tool stage that fails — its command exits non-zero or
times out, or a declared file is missing — still records its text outputs (stdout) and every declared file that
exists, without the contract, because a failing run's output is what the next stage needs:

```dot
test [shape=parallelogram, tool_command="python -m pytest -q", outputs="test_report"]
fix  [prompt="Make the failing tests pass", inputs="test_report"]
test -> fix [condition="outcome=fail"]
fix -> test
```

**Records.** Each recorded output is stored in the context as `artifacts.<name>` (always the latest version) and
announced with an `ArtifactProduced` event when the engine records the stage's outcome (after its `StageCompleted`;
attempts that are retried announce nothing):

```json
{"name": "design", "kind": "file", "path": "docs/DESIGN.md", "text": null, "producer": "architect", "version": 2,
 "bytes": 1834, "sha256": "9f2c4e…", "produced_at": "2026-07-23T16:05:11.2140000+00:00"}
```

`version` counts how often the artifact has been produced in this run (loops; a `loop_restart` starts a new run).
`text` holds a text artifact's content, up to 16 K characters (beginning and end kept), and is null for files. `bytes`
is the file size or the full text's UTF-8 size; `sha256` is the digest of the file (null above 16 MB) or of the full
text. Records are ordinary context values: checkpointed, restored by `reactor resume`, and returned by
`GET /pipelines/{id}/context`. They are left out of the context preamble, since a stage sees what it declares as
inputs. A plain value seeded with `--set artifacts.ticket="..."` reads as a text artifact (version 0).

**Parallel branches** run on cloned contexts, and artifact records are the one thing merged back: when the branches
finish, the records made in them — failed branches included — are copied into the parent context in branch
declaration order, so stages after the join can read them (a review panel whose reviewers each produce findings, and
an `apply` stage after the fan-in with all of them as inputs). Branches cannot see each other's records while they
run. When several branches produce the same artifact, the branch declared last wins, and its `version` becomes the
version before the fork plus every production in every branch, so versions keep increasing (a record made in a
single branch is kept as is). Branches cancelled by `join_policy="first_success"` contribute nothing. The merge emits
no events of its own; each production was announced in its branch.

**Simulation.** Without a backend, codergen stages write no files, so their file outputs are recorded without the
existence check (`bytes` 0 and no digest unless the file exists).

**Validation.** `artifact_syntax` (error): malformed entries — invalid or duplicate names; empty, absolute, or
escaping paths; directories; wildcards. `artifact_inputs` (warning): an input that no stage produces.
`artifact_order` (warning): no producer of an input can run before the consuming stage (records made in parallel
branches count for the stages after the join). `artifact_handlers` (warning): `inputs` or `outputs` on a node type
that ignores them.

## Fidelity and threads

`fidelity` controls what a codergen stage sees of earlier work:

| Mode | Prompt preamble |
|---|---|
| `full` | none — the stage continues the same agent conversation as the previous stage on its thread |
| `compact` (default) | goal, completed stages with outcomes and notes, and context values |
| `truncate` | the goal only |
| `summary:low` / `summary:medium` / `summary:high` | progressively more detailed summaries (≈600 / 1500 / 3000 tokens) of earlier stages, context, and logs |

Stages with `fidelity="full"` share a conversation per **thread**. The thread comes from the node's `thread_id`, the
incoming edge's `thread_id`, the graph's `default_thread_id`, the node's subgraph class, or else the previous stage's
thread. Backends keep one session per thread: the agent loop keeps the `Session`, the Copilot SDK keeps the session,
and the CLI backends resume the CLI's session id. After `reactor resume`, in-memory sessions are gone, so the first
resumed stage falls back to `summary:high`.

## Model stylesheet

```css
*               { llm_model: gpt-5-mini; reasoning_effort: medium; }
box             { llm_provider: anthropic; }
.deep-thinking  { llm_model: claude-sonnet-5; reasoning_effort: high; }
#final_review   { llm_model: gpt-5.5; }
```

Selectors: `*`, a shape name, `.class`, `#node_id`. Specificity is universal < shape < class < id; later rules win
ties; explicit node attributes always win. Properties: `llm_model`, `llm_provider`, `reasoning_effort`.

## Tool hooks

`tool_hooks.pre` / `tool_hooks.post` (graph- or node-level) run a shell command around every agent tool call made by
the `agent` and `copilot` backends. The command gets `REACTOR_HOOK`, `REACTOR_TOOL_NAME`, `REACTOR_TOOL_CALL_ID`,
`REACTOR_NODE_ID`, `REACTOR_RUN_ID`, `REACTOR_SESSION_ID` (and `REACTOR_TOOL_IS_ERROR` for post) in its
environment, and a JSON description of the call (including arguments, and output for post hooks) on stdin. A
non-zero pre-hook exit blocks the call and tells the model why; post-hook failures are only logged. Hooks run one at a
time per session so audit scripts can append to files safely.

## Run directory

```text
.reactor/runs/20260718-192803-test_pipeline/
  manifest.json  graph.dot  checkpoint.json  events.jsonl  result.json
  plan/          prompt.md  response.md  status.json  backend.json  (+ transcript.md, agent-events.jsonl, ...)
  implement/     ...        history/visit-1/ (files from earlier visits of a stage that ran more than once)
  checkpoints/   0001-start.json  0002-plan.json  ...   (server runs: the checkpoint after each stage)
  studio.json    (server runs started from the Studio: source file, layout, forked_from)
```

`events.jsonl` has one JSON object per event (`PipelineStarted`, `StageStarted`, `StageCompleted`, `StageLog`,
`EdgeSelected`, `InterviewStarted`, `CheckpointSaved`, `ArtifactProduced`, …). `reactor resume <run-dir>` continues from
`checkpoint.json`.

**Checkpoint snapshots and forks.** With `RunOptions.KeepCheckpointHistory` (on for runs started by `reactor serve`),
every checkpoint saved after a top-level stage is also kept as `checkpoints/{index:D4}-{node}.json`, where `index` is
the stage execution index (the `index` of its `StageStarted`/`StageCompleted` events; parallel branch stages use
indexes but get no snapshot of their own). The final save that records the run's outcome is not a snapshot. A
snapshot is an ordinary checkpoint: copy it as `checkpoint.json` into a new directory with a `graph.dot` and
`reactor resume` (or `POST /studio/api/runs/{id}/fork`, see [http-api.md](http-api.md)) continues **after** that
stage, routing on its recorded outcome — so to re-run a stage, start from the snapshot taken just before it. The
graph may be edited first (prompts, models, edges) as long as it still has the snapshot's stage. Only engine state
is restored: files the agents changed in the working directory are not rewound.
