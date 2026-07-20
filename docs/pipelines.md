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

### Handler-specific attributes

| Handler (shape) | Attributes |
|---|---|
| wait.human (`hexagon`) | `human.timeout`, `human.default_choice` (target id, key, or label), `human.input="freeform"` with `human.question` |
| parallel (`component`) | `join_policy` = `wait_all` (default; `partial_success` if any branch fails) or `first_success` (cancels the other branches once one succeeds); `max_parallel` (default 4) |
| fan-in (`tripleoctagon`) | optional `prompt`: an LLM picks the best branch (reply `BEST: <id>`); otherwise ranked by status and score |
| tool (`parallelogram`) | `tool_command` (run with `cmd.exe /c` on Windows, `bash -c` elsewhere); `timeout` (default 10m) |
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

Seed values from the command line with `reactor run --set ticket=ABC-123 --set retries=3` (values are parsed as JSON
when they look like JSON).

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
```

`events.jsonl` has one JSON object per event (`PipelineStarted`, `StageStarted`, `StageCompleted`, `StageLog`,
`EdgeSelected`, `InterviewStarted`, `CheckpointSaved`, …). `reactor resume <run-dir>` continues from
`checkpoint.json`.
