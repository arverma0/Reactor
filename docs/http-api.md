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
