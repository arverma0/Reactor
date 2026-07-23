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
`ParallelCompleted`, `InterviewStarted`, `InterviewCompleted`, `InterviewTimeout`, `CheckpointSaved`.

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
- `source_path`, `forked_from`: from `studio.json`. `simulated`: the run was started with a `"simulation"` object
  (recorded in `studio.json`), or its `studio.json`/`manifest.json` says so, or it has a `simulation.json`.

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
