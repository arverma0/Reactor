# HTTP API

`reactor serve` hosts the Attractor HTTP mode (spec 9.5) and a web console. Start it with:

```powershell
reactor serve --backend copilot --urls http://127.0.0.1:5180 --token <secret> [-C <workdir>] [--max-concurrent 4]
```

- **Auth**: with `--token` (or `REACTOR_SERVER_TOKEN`), every API call needs `Authorization: Bearer <token>`; the
  event stream also accepts `?access_token=<token>` because browsers' `EventSource` cannot send headers. `/` (the
  console) and `/healthz` are open. The server binds to loopback by default and warns when exposed without a token —
  pipelines run shell commands, so treat access as code execution.
- **Runs** execute in the server's working directory (clients may choose one only with `--allow-client-workdir`),
  at most `--max-concurrent` at a time (others are `queued`), each limited to 1000 stage executions.
- JSON uses `snake_case`. Errors are `{"error": "..."}` with a 4xx/5xx status.

Embedding instead of the CLI:

```csharp
var app = ReactorServer.Build(new ReactorServerOptions { Backend = backend, WorkingDirectory = repoDir, AuthToken = token });
await app.RunAsync("http://127.0.0.1:5180");
```

## Endpoints

| Method | Path | Description |
|---|---|---|
| `POST` | `/pipelines` (alias `/run`) | Start a run. Body: DOT source (`text/vnd.graphviz` or `text/plain`), or JSON `{"dot": "...", "context": {...}, "working_directory": "...", "studio": {...}}`. `201` with `{id, status, logs_root, links}`; `400` with `error` and `diagnostics` for parse/validation errors. |
| `POST` | `/validate` | Parse and lint without running: `{valid, name, nodes, edges, diagnostics}`. |
| `GET` | `/pipelines` (alias `/status`) | All runs, newest first. |
| `GET` | `/pipelines/{id}` (aliases `/status/{id}`, `/status?id=`) | Status, current node, `active_nodes`, completed nodes, per-node statuses, pending questions, timing, result, `studio` metadata. |
| `GET` | `/pipelines/{id}/events` | Server-sent events (below). |
| `POST` | `/pipelines/{id}/cancel` | Cancel a running or queued run: `202`; `409` if already finished. |
| `POST` | `/pipelines/{id}/resume` | Resume a failed/cancelled run from its checkpoint: `202`. |
| `GET` | `/pipelines/{id}/graph?format=svg\|png\|dot` | Graph colored by stage status (green success, red fail, blue running, …). Needs Graphviz for svg/png (`503` with the DOT otherwise). |
| `GET` | `/pipelines/{id}/questions` | Pending human-gate questions. |
| `POST` | `/pipelines/{id}/questions/{qid}/answer` | Answer: `{"value": "A"}` (option key or label, `yes`/`no`, or `skip`) or `{"text": "..."}` for freeform. `400` lists valid options. |
| `POST` | `/answer` | Alias: `{"pipeline_id": "...", "question_id": "...", "value": "..."}`; `question_id` may be omitted when exactly one question is pending. |
| `GET` | `/pipelines/{id}/checkpoint` | The latest `checkpoint.json`. |
| `GET` | `/pipelines/{id}/context` | Context values: from the final result, else the latest checkpoint, else the initial context (`source` says which). |
| `GET` | `/pipelines/{id}/stages/{node}` | A stage's outcome and file list. |
| `GET` | `/pipelines/{id}/stages/{node}/files/{name}` | One stage file (`prompt.md`, `response.md`, `status.json`, `backend.json`, transcripts…). |
| `GET` | `/healthz` | `{"status":"ok"}`. |
| `GET` | `/` | Web console. |

Run states: `queued`, `running`, `success`, `partial_success`, `fail`, `cancelled`, `error` (the run could not
execute, e.g. an engine exception).

## Event stream

```text
GET /pipelines/{id}/events            (Last-Event-ID: <n> or ?since=<n> to skip already-seen events)

retry: 3000

id: 1
event: PipelineStarted
data: {"name":"Gate","goal":"","logs_root":"...","timestamp":"...","run_id":"245dde38bd50","type":"PipelineStarted","description":"...","seq":1}

id: 7
event: InterviewStarted
data: {"question_id":"5270…","question":"Approve greet.py?","stage":"approve","options":[{"key":"A","label":"[A] Approve"},…],"question_type":"multiple_choice",…}

: keep-alive

event: end
data: {"id":"245dde38bd50","status":"success"}
```

Every run keeps its events in memory (up to 50,000; the complete log is always in the run's `events.jsonl`), so a
client connecting late receives the backlog first and then live events, without gaps or duplicates. The stream ends
with an `end` event when the run finishes; comments (`: keep-alive`) are sent every 15 seconds.

Event types: `PipelineStarted`, `PipelineCompleted`, `PipelineFailed`, `StageStarted`, `StageCompleted`,
`StageFailed`, `StageRetrying`, `StageLog` (agent activity: `node_id`, `message`, `level`), `EdgeSelected`,
`GoalGateUnsatisfied`, `LoopRestarted`, `ParallelStarted`, `ParallelBranchStarted`, `ParallelBranchCompleted`,
`ParallelCompleted`, `InterviewStarted`, `InterviewCompleted`, `InterviewTimeout`, `CheckpointSaved`, and
`ArtifactProduced` (Reactor extension: a stage's declared output was recorded, emitted after the stage's
`StageCompleted`; `node_id`, `name`, `kind` (`file` or `text`), `path`, `bytes`, `version`, `sha256` — `path` and
`sha256` are omitted when null; see [artifacts](pipelines.md#artifacts-reactor-extension)):

```text
id: 9
event: ArtifactProduced
data: {"node_id":"architect","name":"design","kind":"file","path":"docs/DESIGN.md","bytes":1834,"version":1,"sha256":"9f2c4e…","timestamp":"…","run_id":"245dde38bd50","type":"ArtifactProduced","description":"Artifact design v1 produced by architect: docs/DESIGN.md (1834 bytes)","seq":9}
```

The latest record of each artifact is in the run's context (`GET /pipelines/{id}/context`) as `artifacts.<name>`.

## Studio: flights

Run history, replay, telemetry, forks, and steering for the Studio's Flights view, under `/studio/api` (same auth;
`GET /studio/api/info` lists the features `history`, `telemetry`, `fork`, `steering`). The history is read from the
run directories on disk (`<workdir>/.reactor/runs`), so it includes runs of earlier server processes and
`reactor run` CLI runs, survives restarts, and is cached per run directory (refreshed as `events.jsonl` grows).

| Method | Path | Description |
|---|---|---|
| `GET` | `/studio/api/runs?name=&limit=100` | Run summaries, newest first (`name` = graph name). |
| `GET` | `/studio/api/runs/{id}` | Summary, `manifest`, `result`, `checkpoint`, `graph_dot`, `studio`, `stages`, `checkpoints`. |
| `GET` | `/studio/api/runs/{id}/events?since=0&limit=20000` | `events.jsonl` paged: `{events, next, done}`; `limit` ≤ 50000. |
| `GET` | `/studio/api/runs/{id}/stages/{node}/files/{name}?visit=N` | A stage file; `visit` selects `history/visit-N` (the latest visit is the stage directory itself). |
| `GET` | `/studio/api/telemetry?name=&limit=50` | Aggregates over the latest finished, non-simulated runs of a pipeline. |
| `POST` | `/studio/api/runs/{id}/fork` | Start a new run from a stage snapshot of `{id}`, optionally with another pipeline. `201`. |
| `POST` | `/studio/api/pipelines/{id}/steer` | Send a message to the agent of a running stage. `202`. |

`{id}` under `/studio/api/runs` is a **run directory name** (a summary's `id`); `live_id` is the server run id used by
`/pipelines/{id}` while the server knows the run. Ids must be plain directory names directly under the runs
directory (no separators, no `..`); anything else is `404`. Directories without `manifest.json` are not runs.
Loop restarts (`<run>-restart-N`) are folded into their run (`restarts`); their names are also valid `{id}`s (the
detail and events describe the whole run; stage files are read from that directory).

**Summary** (`GET /studio/api/runs` → `{"runs": [...]}`):

```json
{"id": "20260723-164549-factory-7f55af02c909", "run_id": "7f55af02c909", "name": "factory", "goal": "Ship the feature",
 "started_at": "2026-07-23T16:45:49.3542699+00:00", "finished_at": "2026-07-23T16:45:49.4165759+00:00",
 "status": "success", "duration_ms": 38, "node_count": 5, "stage_executions": 3, "failure_reason": null,
 "live_id": "7f55af02c909", "restarts": [], "source_path": "examples/factory.dot", "simulated": false,
 "forked_from": {"id": "20260723-164531-factory-99397269d91f", "checkpoint": "0002-plan", "node": "plan", "run_id": "99397269d91f"},
 "cost": {"ai_units": 3.5, "input_tokens": 350, "output_tokens": 30}}
```

- `status`: `queued`/`running` while the server runs it; else from `result.json` (`success`, `fail`, or `cancelled`
  when `cancelled` is true); else the server's final state (`error` is reported as `fail`); else `interrupted` (the
  run stopped without a result, e.g. a killed process). Runs the server has queued but not started yet are listed too.
- `duration_ms`: from `result.json`; for running runs the time so far; for interrupted runs until the last event.
- `stage_executions`: stage visits (`StageStarted` first attempts) across the run and its restarts.
- `cost`: sums of `backend.json` fields over every stage visit (including `history/visit-N`): `ai_units`,
  `premium_requests`, `cost_usd`, `input_tokens`, `output_tokens`, `reasoning_tokens`, `tool_calls`. Fields no stage
  reported are omitted.
- `source_path`, `forked_from`: from `studio.json`. `simulated`: the run's `studio.json`/`manifest.json` says so, or it
  has a `simulation.json` (runs the server simulates are kept in `.reactor/simulations/` and are not listed at all; see
  [Simulation](#simulation-studio-wind-tunnel)).

**Detail**: `checkpoint` is `{current_node, completed_nodes, node_outcomes: {node: status}, final_status}` from the
latest `checkpoint.json`; `stages` lists every executed node in order of first execution:
`{node, visits, status, duration_ms (sum over visits), backend (latest visit's backend.json), files (latest visit),
history_visits, segment (the directory holding the files)}`; `checkpoints` lists the stage snapshots available for
forking (`["0001-start", "0002-plan", ...]`, prefixed `restart-N/` in loop-restart directories).

**Events**: each event is the `events.jsonl` object plus `seq`, its 1-based line number across the run directory
and its restart directories in order. Pass `next` as `since` for the following page; `done` is true when the end was
reached and the run is not running. A half-written last line is held back until it is complete; corrupt lines are
skipped (their `seq` is not reused). `seq` numbers lines of `events.jsonl`; they usually equal the SSE ids of a live
run, but are not guaranteed to (SSE also carries the server's own notes, such as steering, which are not in the file).

**Telemetry** (`GET /studio/api/telemetry?name=factory`), over the newest `limit` runs whose status is `success`,
`partial_success`, `fail`, or `cancelled` and that are not simulated:

```json
{"name": "factory", "runs": 3, "window": {"from": "...", "to": "..."},
 "outcomes": {"success": 1, "fail": 1, "cancelled": 1, "partial_success": 0},
 "duration_ms": {"mean": 2000, "p50": 2000, "p95": 2900},
 "cost": {"ai_units": {"mean": 6.666667, "total": 20}, "input_tokens": {"mean": 666.666667, "total": 2000}},
 "nodes": {"build": {"runs": 3, "visits": 4, "mean_visits": 1.333, "outcomes": {"fail": 2, "success": 1}, "fail_rate": 0.6667,
                     "retries": 0, "duration_ms": {"mean": 500, "p50": 500, "p95": 680, "max": 700},
                     "cost": {"ai_units": 4.333333}, "models": {"claude-opus-4.6": 2, "gpt-5": 1},
                     "top_failures": [{"reason": "tests failed", "count": 2}]}},
 "edges": [{"from": "plan", "to": "build", "count": 4, "runs": 3}],
 "series": [{"id": "...", "started_at": "...", "status": "success", "duration_ms": 1000, "stage_executions": 2, "ai_units": 4}]}
```

Per node: `runs` that executed it, `visits` and `mean_visits` per run, `outcomes` of completed visits, `fail_rate` =
failed / completed visits, `retries` = extra attempts (`StageStarted` with `attempt` > 1), durations from
`StageCompleted`, `cost` = mean per visit over the visits whose `backend.json` reported the field, `models` = visits
per `backend.json` model, `top_failures` = the five most common failure reasons (first line). Percentiles interpolate
linearly; `series` is oldest → newest. Values no run reported are omitted.

**Fork** — counterfactual re-runs. Server runs keep a copy of the checkpoint saved after each stage
(`checkpoints/0004-plan.json`: after stage execution 4, `plan`).

```http
POST /studio/api/runs/20260723-164531-factory-99397269d91f/fork
{"checkpoint": "0002-plan", "dot": "<modified DOT, optional>", "context": {"ticket": "ABC-123"}}

201 {"id": "7f55af02c909", "status": "queued", "name": "factory", "logs_root": ".../20260723-164549-factory-7f55af02c909",
     "forked_from": {"id": "20260723-164531-factory-99397269d91f", "checkpoint": "0002-plan", "node": "plan", "run_id": "99397269d91f"},
     "diagnostics": [], "links": {"self": "/pipelines/7f55af02c909", "events": "/pipelines/7f55af02c909/events",
                                  "history": "/studio/api/runs/20260723-164549-factory-7f55af02c909"}}
```

The fork is a new run directory (`graph.dot` = the given DOT or the source's, `checkpoint.json` = the snapshot with
the new run id, no final status, and the `context` overrides, a `manifest.json`, and a `studio.json` with
`forked_from` plus the source's Studio metadata), started like a resume: it is listed in `/pipelines`, streams
events, asks questions, and can be cancelled. The engine continues **after** the snapshot's stage and routes on that
stage's recorded outcome, so to re-run stage X (say, with another `llm_model`), fork from the snapshot taken just
before X. The pipeline may change prompts, models, and edges, but must still contain the snapshot's stage. **The
working directory is not rewound**: files the source run (or later runs) wrote stay as they are. Errors: `400` for
an invalid DOT (`error`, `diagnostics`) or checkpoint name, `404` for an unknown run or checkpoint.

**Steer** — `POST /studio/api/pipelines/{id}/steer` with `{"message": "...", "node": "implement"}` (`node` defaults
to the running stage and must be running) appends the message (UTF-8) to `<logs_root>/<node>/steering.md` and adds a
`StageLog` note ("steering queued by user: …") to the run's live event stream:
`202 {"status": "queued", "node": "implement", "path": ".../implement/steering.md"}`. `404` unknown run; `400` empty
or > 16 KB message, unknown node; `409` when the run is not running a stage, the node is not running, or the stage
is not an agent stage (human gates, tools, …). The `agent` and `copilot` backends pick the file up within a second
and pass it to the agent in the middle of its turn; `copilot-cli`, `claude-code`, and `direct` stages do not read it
(see [backends.md](backends.md)).

**Studio metadata**: a `"studio"` object in the `POST /pipelines` JSON body (e.g. `{"source_path":
"examples/factory.dot", "layout": {...}}`, at most 4 MB) is saved as `<logs_root>/studio.json` when the run is
created and returned as `studio` by `GET /pipelines/{id}` and the history.

## Example session

```powershell
$h = @{ Authorization = "Bearer $token" }
$run = Invoke-RestMethod -Method Post http://127.0.0.1:5180/pipelines -Headers $h `
         -ContentType application/json -Body (@{ dot = Get-Content -Raw examples/human-review.dot } | ConvertTo-Json)
# ... later
$q = (Invoke-RestMethod "http://127.0.0.1:5180/pipelines/$($run.id)/questions" -Headers $h)[0]
Invoke-RestMethod -Method Post "http://127.0.0.1:5180/pipelines/$($run.id)/questions/$($q.id)/answer" -Headers $h `
  -ContentType application/json -Body '{"value":"A"}'
Invoke-RestMethod "http://127.0.0.1:5180/pipelines/$($run.id)" -Headers $h
```

## Simulation (Studio wind tunnel)

Test a pipeline's routing logic at zero token cost before running it for real: the real engine runs (routing, retries,
goal gates, retry targets, conditions, parallel fan-out/fan-in, human gates); only the stage *behaviors* are simulated.
Simulated stages never call a backend, never run a `tool_command`, and never launch a child pipeline.

| Method | Path | Description |
|---|---|---|
| `POST` | `/studio/api/simulate` | Monte Carlo. Body: `{"dot": "...", "scenario": {...}, "runs": 500, "max_steps": 200, "seed": 123, "context": {...}}` (all but `dot` optional; `runs` ≤ 5000, `max_steps` ≤ 5000). Returns the report below. |
| `POST` | `/studio/api/simulate/trace` | Replays one sample with every event: `{"dot", "scenario", "seed", "run_index": 0, "max_steps", "context"}`. |
| `POST` | `/pipelines` with `"simulation"` | A simulated *managed* run (status, events, questions, cancel, and resume work as for real runs). |

Both `simulate` endpoints also accept a raw DOT body (empty scenario, defaults). Errors are `400 {"error", "diagnostics"}`
with the diagnostic shape of `POST /pipelines`: DOT syntax errors, validation errors, and scenario errors (rule
`simulation_scenario`, message prefixed with the JSON path, `node_id` when it concerns a node). Validation uses the
server's lint rules and transforms; custom handler types are simulated like codergen stages. 500 samples of
`examples/factory.dot` take under a second on a 16-core machine.

### Scenarios

```json
{
  "seed": 123,
  "step_delay_ms": 0,
  "defaults": {
    "codergen": { "outcomes": { "success": 0.9, "fail": 0.1 } },
    "tool":     { "outcomes": { "success": 0.7, "fail": 0.3 } }
  },
  "nodes": {
    "test":    { "script": ["fail", "fail", "success"], "outcomes": { "success": 0.6, "fail": 0.3, "retry": 0.1 },
                 "context_updates": { "success": { "tests_passed": true }, "fail": { "tests_passed": false } },
                 "duration_ms": 1200 },
    "review":  { "labels": { "Approve": 0.8, "Fix": 0.2 } },
    "signoff": { "choices": { "[S] Ship": 0.7, "rework": 0.3 } }
  }
}
```

| Field | Meaning |
|---|---|
| `seed` | Every random draw is a pure function of (seed, run index, node id, execution number), so any sample replays exactly with `/trace`. Omitted: the server picks one and reports it. |
| `step_delay_ms` | Default simulated latency of every execution. |
| `defaults.<type>` | Behavior of every node of a handler type: `codergen`, `tool`, `stack.manager_loop`, `wait.human`, or a custom type (which falls back to the `codergen` entry). |
| `nodes.<id>` | Behavior of one node; overrides the defaults field by field. |
| `outcomes` | Weights over `success`, `partial_success`, `retry`, `fail`, `skipped` (normalized; a single status string means always). Default: always `success`, so the empty scenario follows the happy path. |
| `script` | Outcomes of successive executions; every attempt, including engine retries, takes the next entry, then `outcomes` applies. `"success:Approve"` adds a preferred label. On a human gate, `"success:<option>"` picks that option, `fail` means the human skipped, `retry` that the gate timed out. |
| `labels` | Preferred-label weights drawn on success and partial success, for stages whose outgoing edges are labeled. |
| `choices` | Human-gate option weights, matched against the option label, accelerator key, or target node id (default: uniform). Freeform gates answer `Simulated input`. |
| `context_updates` | Context values merged per resulting status (`"*"` for every status), to exercise conditions such as `context.tests_passed=true`. |
| `duration_ms` | Simulated latency per execution. Reports add it up (`simulated_duration_ms`); managed runs wait it out. A duration beyond the stage's `timeout` (or a gate's `human.timeout`) times the stage out, as in a real run. |

Keys may also be camelCase, numbers may be strings, and durations may carry units (`"1.5s"`); unknown statuses and
behavior fields are errors. Simulated stages set the context keys of the real handlers (`last_stage`, `last_response`;
`tool.output` = `[Simulated] <command>`, `tool.exit_code`, `tool.duration_ms`; `stack.child.status`/`outcome`), and each
decision is logged as a `StageLog` event (`[Simulated] fail (script entry 1 of 3)`). In reports and traces, parallel
branches run one at a time in edge order (with `first_success`, the first branch in edge order that succeeds wins) so
samples replay exactly; managed runs run them concurrently.

### Report

```json
{
  "runs": 500, "seed": 123, "duration_ms": 640, "max_steps": 200,
  "outcomes": { "success": 470, "fail": 20, "step_limit": 5, "goal_gate": 3, "dead_end": 2, "error": 0 },
  "success_rate": 0.94, "success_ci": [0.9156, 0.9577],
  "steps": { "min": 12, "mean": 24.6, "p50": 23, "p90": 40, "p95": 51, "max": 104, "bucket_size": 4,
             "histogram": [ { "steps": 12, "count": 210 }, { "steps": 16, "count": 0 } ] },
  "simulated_duration_ms": { "mean": 0, "p50": 0, "p90": 0, "max": 0 },
  "nodes": { "test": { "visits": 1500, "runs": 500, "mean_visits": 3.0, "max_visits": 9,
                       "outcomes": { "fail": 600, "success": 900 }, "retries": 30 } },
  "edges": [ { "from": "test", "to": "fix", "count": 600, "runs": 300, "kind": "edge" },
             { "from": "signoff", "to": "docs", "count": 500, "runs": 500, "kind": "edge", "label": "[S] Ship" } ],
  "failures": [ { "kind": "step_limit", "reason": "Maximum step count (200) exceeded.", "count": 5,
                  "sample": { "seed": 123, "run_index": 17 }, "last_node": "fix" } ],
  "samples": {
    "first_failure": { "seed": 123, "run_index": 17, "status": "fail", "kind": "step_limit", "failure_reason": "...", "steps": 230, "path": ["start", "..."] },
    "longest": { "...": "same shape" },
    "typical": { "...": "same shape" }
  },
  "warnings": [ "nodes.reviw: the pipeline has no node 'reviw'." ],
  "diagnostics": [ ]
}
```

- `outcomes`: `success`; `fail` (a stage failed and nothing routed the failure); `step_limit` (`max_steps` top-level node
  executions exceeded, typically a loop that does not terminate); `goal_gate` (the exit was reached with an unsatisfied
  goal gate and no retry target); `dead_end` (no eligible outgoing edge); `error` (the engine threw).
- `success_ci`: Wilson score 95% interval. `steps`: stage executions per run (every `StageStarted`, so retries and
  parallel-branch stages count), with nearest-rank percentiles and at most 30 histogram buckets of `bucket_size`.
- `nodes` (every node of the pipeline): `visits` (first attempts), `runs` (runs that visited it), `mean_visits` (per run,
  over all runs), `max_visits` (in one run), `outcomes` (final status of each visit), `retries` (engine retry attempts).
- `edges`, most traversed first; `kind` is `edge` (along a graph edge, including parallel fan-out and branch steps),
  `jump` (not along an edge, e.g. a parallel node continuing at its join), `retry_target`, or `goal_gate` (an unsatisfied
  goal gate sending the run from the exit back to its retry target).
- `failures`: failed runs grouped by kind and reason, each with the first sample to replay and the node it ended at.
- `samples`: the first failed run (`null` when all succeeded), the longest, and the one closest to the median length,
  with their paths (node visits in order, parallel branches in branch order).
- `warnings`: scenario entries that cannot apply (unknown nodes or handler types, labels or choices that match nothing,
  entries for start/exit/conditional/parallel/fan-in nodes, which always run for real). `diagnostics`: lint warnings.

The trace response is `{"seed", "run_index", "status", "kind", "failure_reason", "steps", "simulated_duration_ms",
"path", "events"}`; `events` holds every engine event of the sample in emission order, as in the event stream, each
with a 1-based `seq`.

### Simulated managed runs

`POST /pipelines` with JSON `{"dot": "...", "simulation": {"mode": "stochastic", "scenario": {...}, "seed": 123}}`
(`"simulation": true`, `"stochastic"`, or `"interactive"` are shorthands; any other non-object value is a `400`, never a
real run):

- `stochastic` (default): outcomes and human gates come from the scenario. `step_delay_ms` defaults to 600 so clients
  can animate the run (the scenario's value wins).
- `interactive`: every simulated stage asks `Simulate the outcome of '<label>'` through the questions API, with options
  `[S] Success`, `[F] Fail`, `[R] Retry`, `[P] Partial success`, and `[1] → <label>`, `[2] → ...` for each outgoing edge
  label (success with that preferred label); answering `skip` lets the scenario decide. Human gates are asked for real.

Simulated runs are stored under `<working directory>/.reactor/simulations/`, never in the runs directory (so the run
history and telemetry never include them; their `studio.json` has `"simulated": true`), and
`GET /pipelines` and `GET /pipelines/{id}` add `"simulated": true` and `"simulation_mode"` (the detail also has
`"simulation_seed"`; replay its draws with `/studio/api/simulate/trace`, run index 0). Resuming a simulated run keeps its
settings and per-node execution counts, so scripts continue where they stopped.
