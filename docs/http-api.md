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
| `POST` | `/pipelines` (alias `/run`) | Start a run. Body: DOT source (`text/vnd.graphviz` or `text/plain`), or JSON `{"dot": "...", "context": {...}, "working_directory": "..."}`. `201` with `{id, status, logs_root, links}`; `400` with `error` and `diagnostics` for parse/validation errors. |
| `POST` | `/validate` | Parse and lint without running: `{valid, name, nodes, edges, diagnostics}`. |
| `GET` | `/pipelines` (alias `/status`) | All runs, newest first. |
| `GET` | `/pipelines/{id}` (aliases `/status/{id}`, `/status?id=`) | Status, current node, completed nodes, per-node statuses, pending questions, timing, result. |
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
`ParallelCompleted`, `InterviewStarted`, `InterviewCompleted`, `InterviewTimeout`, `CheckpointSaved`.

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

Simulated runs are stored under `<working directory>/.reactor/simulations/`, never in the runs directory, and
`GET /pipelines` and `GET /pipelines/{id}` add `"simulated": true` and `"simulation_mode"` (the detail also has
`"simulation_seed"`; replay its draws with `/studio/api/simulate/trace`, run index 0). Resuming a simulated run keeps its
settings and per-node execution counts, so scripts continue where they stopped.
