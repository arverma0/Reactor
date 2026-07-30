# Reactor Studio

Reactor Studio is a visual workbench for agent factories: you design a pipeline on a canvas, fly it hundreds of times
in a wind tunnel before spending a token, launch and steer real flights, and read every flight back — while an
Architect drafts and critiques pipelines for you, checking each draft before you see it. Everything runs on the real
engine: the Studio's routing, validation, prompts, and simulations come from Reactor itself, not from a copy of its
rules in the browser.

```powershell
reactor studio                               # serve the current directory and open the Studio
reactor studio --backend copilot -C C:\repo  # with a backend and a workspace
reactor serve                                # the same server without opening a browser: http://127.0.0.1:5180/
```

The Studio is served at `/` by every Reactor server (the classic console moved to `/console`). With `--token`, the
Studio asks for the token once and keeps it in the browser. The workspace is the server's working directory: the
Studio lists its `.dot` files and saves next to them.

| Mode | What it is for | Costs tokens? |
|---|---|---|
| **Design** (1) | Build the pipeline: stages, routes, groups, artifacts, models, prompts | no |
| **Wind tunnel** (2) | Fly the design with simulated outcomes: does it land, where does it loop, what will it cost | no |
| **Flights** (3) | Run it for real, answer its questions, steer its agents, replay and measure every flight | yes |
| **Architect** | Draft, revise, or critique a pipeline from a description and the evidence | yes (the Architect's own run) |

## Design

**Stage cards.** Each stage is a card with its kind (part code and glyph: `AGT` agent, `TOL` tool, `HMN` human gate,
`RTE` router, `FAN` fan-out, `JON` join, `SUP` supervisor), its task (the prompt, or the tool command), the effective
model (and where it comes from: the stage, a stylesheet rule, or a default block), and chips for goal gates, retries,
timeouts, loop bounds (`⟲≤3`: `max_visits`), threads, and fidelity. Cards trade detail for legibility as you zoom out.

**Routes are ports.** Every way out of a stage is a labeled port on its card — `✓ on success`, `✕ on fail`,
`◆ Approve` (a label the agent can choose), `ƒ condition`, `[A] Approve` (a human gate's options, in order), `⋔`
(parallel branches). Hover a card to see the ports it could have; dragging from one creates the edge with the right
`condition` or `label`, so you never type `outcome=fail`. Drop a connection on empty canvas to add a connected stage.
A stage with a loop bound also has an `⟲ after N visits` port: where the run goes once the stage has run `max_visits`
times. It is not an edge but the stage's `on_max_visits`, drawn as a dashed blue connector; drag the port to a stage
to set it (without one, the run fails at the limit).

**Conditions.** The condition builder writes the engine's language, Reactor's numeric comparisons included: `=`, `≠`,
`<`, `≤`, `>`, `≥`, and "is set", over `outcome`, `preferred_label`, `context.<key>`, `visits.<stage>` (how many times
the stage has run) and `metrics.<name>` (the latest outcome metric). The key field suggests `visits.` for the
pipeline's stages and `metrics.` for the metrics its conditions use, its wind tunnel scenario reports, or its flight
record has seen; a clause the engine would reject (a comparison with a non-number, `==`) is flagged. Canvas labels
read them out: `ƒ polish visits < 3`, `ƒ metric qa_failed > 0`.

**Groups** are subgraphs, drawn as frames: drag a stage into or out of a frame to change its membership. A group's
label becomes a stylesheet class (`Deep thinking` → `.deep-thinking`), so the Models panel can give a whole group a
model.

**Artifacts.** Stages that declare `outputs` and `inputs` show pins; drag an output pin onto a stage to make it an
input, and the data flow is drawn as colored wires (toggle them with the controls). Pins whose input nobody produces
are marked.

**Panels.** The **Library** has every stage kind and factory patterns to drop in (plan · build · verify, test–fix
loop, design fight, review panel, human sign-off, critic loop, best of three, spec first, supervisor). The
**Inspector** edits the selection — prompts, tool commands (and whether they may print in color), conditions (with a
builder), retries and retry targets, goal gates, timeouts and what a timeout does (retry, fail, or wrap up first, with
the soft deadline and grace period), flow control (whether an unrouted failure stops the run or carries on, and loop
bounds: max visits and where to go after them), outcome metrics (a metrics file), fidelity and threads, artifacts,
models, standing instructions, and the workspace settings (a fan-out's branch workspaces, a join's *promote the
winner*, a stage's write scope and concurrency group) — or the pipeline itself (goal, defaults, retry targets, what unrouted failures do, the
budget per run, pipeline instructions and the built-in house rules, hooks). Durations are checked as the engine
reads them. **Models** edits the `model_stylesheet` as rules (model, effort, provider, instructions) and shows which
stages each rule reaches; model pickers show each model's price per 1M input/output tokens. **Outline** lists stages,
artifacts, and threads. **Files** opens and saves pipelines.

**Quick fixes.** The validator's flow-control findings come with one-click fixes, in the Problems panel and on the
stage in the Inspector — each one an ordinary, undoable edit: a failure that flows onward (`fail_flows_onward`) —
stop the run on unrouted failures (`default_on_fail=stop`), stop if this stage fails (`on_fail=stop`), route failures
explicitly (condition the only way on on `outcome=success`, offered only where that cannot change where successes go),
or mark it intended (`on_fail=continue`); a default decided by id order (`default_route`) — pick the route that should
be the default (a person's gate or a rework route first), which gets the highest weight; a loop without a bound
(`unbounded_loop`) — pick the stage in the loop that gets `max_visits=3`; a fixer that does not read the failing
evidence (`failure_evidence`) — add the output to its inputs; and `on_max_visits` without `max_visits`.

**Code and prompts.** The DOT drawer (`Ctrl+\``) is a two-way editor: edit the text and the canvas follows, edit the
canvas and the text follows. **Prompt preview** shows the exact prompt a stage will receive — preamble, task,
artifact sections, and the status-file instructions — for a chosen history of completed stages.

**Record lens.** When a pipeline has flown, the gauge button in the canvas controls puts its flight record on the
cards: how often each stage is reached, succeeds, how long it takes, what it costs, how many retries it needed, how
often its failure flowed on, and the mean of each metric it reports, with cable thickness showing where the traffic
goes.

**Saving.** A pipeline is a `.dot` file (the source of truth, readable and diffable) plus a `name.layout.json`
sidecar with positions, notes, and colors. The Studio writes attributes in a stable order and keeps the file's
leading comment block; comments inside the graph body are not kept (the code drawer warns when a file has them). When a
file changed on disk since you opened it, saving stops and asks before overwriting.

| Keys | |
|---|---|
| `Ctrl K` | command palette |
| `Ctrl S` | save |
| `Ctrl Z` / `Ctrl Shift Z`, `Ctrl Y` | undo / redo |
| `A` `T` `H` `R` `P` `J` `S` | add an agent, tool, human gate, router, fan-out, join, supervisor |
| `N` | sticky note |
| `Ctrl C` / `Ctrl V` / `Ctrl D` | copy / paste / duplicate |
| `Ctrl G` | group the selection |
| `Delete` | delete the selection |
| `L` / `F` | auto-arrange / fit to screen |
| `1` `2` `3` | Design, Wind tunnel, Flights |
| ``Ctrl ` `` | DOT code drawer |

## Wind tunnel

The wind tunnel flies the pipeline through the real engine — routing, conditions, retries, goal gates, retry
targets, parallel branches, human gates — with simulated stage outcomes. Nothing is executed: no model calls, no tool
commands, no child pipelines. Five hundred flights take a fraction of a second.

- **Scenario.** Pick a preset (*Happy path*, *Realistic*, *Stress*, *Chaos* — the same presets as
  `reactor simulate --scenario`), tune the dials (how often agents succeed or ask for a retry, how often tools pass),
  set the **typical durations** by stage kind that time forecasts assume (agent stages 10 min, tools 1 min,
  supervisors 30 min by default; never longer than a stage's own timeout), or give a stage its own behavior: success
  odds, an exact script of outcomes (`✓ ✕ ✕ ✓`), how an agent chooses among its labeled routes, what a person picks at
  a gate, how long the stage takes (up to 24 hours; longer than its `timeout`, it times out as in a real run), what one
  run of it **costs** (USD), and the **metrics** it reports — `4`, `1..5`, `3, 4, 5`, `3:0.2, 4:0.8`, or per status
  `success: 0; fail: 1..5` — which the engine records as `metrics.<name>` so conditions such as `metrics.score=5` route
  on them.
- **Calibrated.** When the pipeline has a flight record, the *Calibrated* preset fits the scenario to it: every stage
  that has flown gets the outcome mix, retry share, route choices, typical duration, and cost per run (from `cost_usd`)
  it really had. The results then show **forecast vs. record** — the tunnel's success rate, time, and cost per flight
  next to the real ones — so you can try a change in the tunnel and see what it should do to the numbers that matter.
- **Results.** A readiness gauge on **clean landings** (flights that reach the exit with no failed stage behind them,
  with the confidence interval) and, beside it, the share that **landed with failed stages**: a stage failed, nothing
  routed the failure, and the flight went on along an unconditional edge as if it had succeeded (a later successful
  visit of the stage, such as a goal gate sending the flight back, makes it good). Then how flights go down (failed,
  never converged, goal gate unmet, dead end — each pointing at the stage where it happens, or the stages where a loop
  was cut off), which stages' failures flowed into landed flights, stage executions, simulated **time and cost per
  flight** (mean, p90, max), **metrics at landing** (the distribution of each metric's final value), hot spots, and
  traffic on the canvas (stages whose failures flow on are marked). Every failure mode has a ▶ that replays one such
  flight on the canvas, step by step.
- **Animate one** plays a single simulated flight; **Walk through** starts a simulated run where you decide each
  stage's outcome on its card, to rehearse a path by hand.

## Flights

The flight list has every run: live runs on this server, and the run history on disk — runs of earlier servers and
`reactor run` from the command line — grouped by day, with duration and cost. Cost is shown in dollars (`cost_usd`,
which every backend records) so runs on different backends compare, with the native units — AI units, premium
requests, tokens — alongside; it counts every attempt, the ones that timed out or failed and were retried included.
Runs that stopped at their budget are tagged `budget`, and runs that landed with a failed stage behind them (its failure
flowed on along an unconditional edge) `failed stage`. Filter it to the pipeline open in Design. The Flights tab shows
a count when a run is waiting for a person.

**Mission control.** A live flight lights up the canvas: the running stage shows its elapsed time and latest log
lines, cables pulse as the engine takes them, and a human gate's question appears on its card with its options —
answer it there. A running agent stage (with the `agent` or `copilot` backend) can be **steered**: type an
instruction and the agent gets it in the middle of its turn. Cancel and resume from the run panel. A real run can be
launched with a budget of its own (US dollars; otherwise the server's default, else the pipeline's `max_cost`).

**Flight recorder.** The timeline under the canvas has a row per stage and a bar per execution; drag the playhead to
see the run at any moment, or replay it at 1–120× speed. The run panel has the flight log, and each stage has its
files (prompt, response, status, transcripts, standing instructions, metrics) for every visit, with the model, tokens,
and cost of each. Terminal escape sequences in logs, failure reasons, and files are stripped; a tool's raw capture
(`stdout.raw.txt`) shows them as control pictures (`␛[31m`).

The engine's flow-control events are part of the record. A failure that **flowed on** (nothing routed it, so an
unconditional edge took the run on) is a warning in the log, a striped bar in the timeline, and a "failed, went on"
readout on the stage; a run that landed with such a stage behind it says so in the run panel. A **visit limit**
(`max_visits` used up) shows as a reroute: the dashed connector lights up, the log says where the run went, and the
timeline marks it `⟲`. **Outcome metrics** appear on the stage (its latest values) and in its panel as a table by
visit — the quality trajectory through a polish loop; the run's final values head the run panel. A **budget stop**
marks the stage the run stopped before and the timeline, and the run panel shows what was spent of which budget, with
**Resume with a higher budget** for a run this server knows (or the `reactor resume … --max-cost` command for others).
The run panel lists the run's usage totals over every attempt — dollars first, then AI units, premium requests,
tokens, cached tokens, model calls, and tool calls.

**Attempts.** When the engine retries a stage (a timeout, an error, a failure), it keeps the attempt's files in
`attempts/attempt-N/`. The stage panel lists each visit's attempts — how each ended (timed out, errored, failed) and
what it cost, with the visit's total — and picking one shows its own prompt, transcript, `backend.json`, and **last
words** (`response.partial.md`: what the agent was saying when it was stopped), which the retry was handed.

**Files changed.** Each stage that changed its working directory shows `files +2 ~1 −0` on its card, and its panel
lists what that visit added, modified, and deleted (from its `changes.json`, see
[Workspaces](pipelines.md#workspaces-reactor-extension)), with sizes, which branch workspace it ran in, and whether
other stages were changing the same directory at the time. Files outside the stage's `write_scope` are listed first
and in red; a fan-in that promoted a branch says which one (or which files conflicted).

**Re-fly.** Fork any flight from the snapshot saved just before one of its stages and fly on from there — as flown,
or with the pipeline as it is now in Design (the dialog shows what changed) and optional context overrides. Earlier
stages are not flown again; their outcomes and context are restored. The re-flown flight links to its original and
can be compared with it stage by stage. By default the working directory is not rewound: files written since stay as
they are (each stage's `changes.json` names the snapshot taken before it, to restore by hand).

When the flight saved git snapshots of its working directory (it ran in a git work tree with workspace tracking on),
the dialog offers **"Restore the working directory to how it was after ‹stage›"**: before the re-flight starts, files
git tracks or would track are overwritten to match the snapshot saved after the stage the re-flight continues from,
and files the snapshot does not have are deleted; ignored files (`node_modules`, build output) stay, and your git
index, `HEAD`, and branches are not touched. The directory's current state is snapshotted first: the toast after
starting gives its commit and the command that brings it back (`git restore --source <commit> --worktree -- .`), and
the re-flown flight's `manifest.json`/`studio.json` keep them (`workspace_restore`). The server refuses the rewind —
the dialog shows why — when the re-flight runs in another repository or subdirectory, while another flight is still
working in that directory, or when the snapshot is gone (see [pipelines.md](pipelines.md#rewinding)).

The re-flown flight works in **the original flight's working directory** — the one its agents and tools used, shown in
the run panel and in the dialog (a `reactor run -C <dir>` flight carries on in `<dir>`, not in the server's
directory). A server started elsewhere may use that directory only with `--allow-client-workdir`; otherwise the dialog
says so and offers the server's own directory. Re-flying anywhere other than the original directory (the server's,
or another one when the server allows it) asks you to confirm first, because the files the flight worked on may not
be there. Flights from older run directories that did not record their working directory re-fly in the server's,
and the dialog says so.

**Record.** The *Record* lens aggregates a pipeline's finished real flights (simulated runs and forks are left out):
how many landed clean (and how many landed with failed stages behind them), how long they take, what they cost, how
many stopped at their budget, the trend flight by flight, where it breaks and why (the most common failure reasons,
and how often a stage's failure flowed on), and per stage its reach, success share, durations, cost per visit (retried
attempts included), models, and the metrics it reported (mean, min, max, last). When stages report outcome metrics,
the **quality trajectory** charts each metric's final value flight by flight next to each flight's time and cost —
does the factory ship better products than it used to, and at what price? From there, *Calibrate wind tunnel*
carries the numbers into the tunnel.

## Architect

The Architect turns a description into a pipeline, or improves the one that is open:

- **Draft new** — describe the factory: what goes in, what comes out, what "done" means.
- **Revise** — say what should change.
- **Critique** — the Architect reads the pipeline and the **evidence** (its flight record and the latest wind tunnel
  report for this exact design), finds what matters most, ties each finding to numbers, and proposes a revision.

Choose the model it designs with, then watch it work: each draft is checked by Reactor's parser, validator, and wind
tunnel — every flight must land clean when every stage succeeds; loops that do not converge under realistic failure
rates, and stages whose failures flow on into landed flights, are flagged — and a failing draft goes back with the
findings, up to three drafts. A draft that passes is then reviewed by the model that drafted it, against the brief;
the panel shows this **self-review** (its verdict and reasoning) and the checker's findings by kind — design rules
(with the stage and the fix), **contract** findings (a path or name the brief states that the draft contradicts),
**forecast** findings (the forecast against a time budget read from the brief, for the self-review to weigh), and wind
tunnel findings. A revision the self-review asked for never costs you the checked draft: if it cannot pass the checks
before the drafts run out, the earlier draft stands and the review's changes are listed as not applied. The proposal comes
with its rationale, the changes against the open pipeline, and a
**forecast**: the current design and the proposal flown 1,000 times each under the same (calibrated, when there is a
record) scenario and the same random draws, so the difference you see is the design — clean landings, landings with
failed stages, time per flight (mean, with p90), cost per flight when the record has costs, and stage executions. *Preview* shows the proposal on the canvas with new and changed stages marked;
*Apply* makes it the design (undoable), or opens a draft as a new pipeline.

The Architect is itself a Reactor pipeline, run by the server like any other, so it appears in Flights with its
stages, prompts, answers, and cost. It needs a real backend (`--backend copilot`, `agent`, `direct`, …). To change how
it works, copy its pipeline or guide into the workspace as `.reactor/studio/architect.dot` or
`.reactor/studio/architect.md` ([http-api.md](http-api.md#studio-architect) describes both).

## Where things are

| Path (under the workspace) | |
|---|---|
| `name.dot`, `name.layout.json` | a pipeline and its layout sidecar |
| `.reactor/runs/` | real runs (the history and the flight record read these) |
| `.reactor/simulations/` | simulated runs started from the Studio (walkthroughs, animated flights) |
| `.reactor/studio/architect/` | the Architect's scratch working directories |
| `.reactor/studio/architect.dot`, `architect.md` | optional overrides of the Architect's pipeline and guide |

## Building the Studio

The Studio is a React and TypeScript app in `src/Reactor.Studio` (React Flow for the canvas, CodeMirror for code,
Vite to build). `dotnet build` builds it with Node.js 20.19 or later and embeds it into `Reactor.Server`; the build is
incremental, and without Node it embeds a placeholder page that says how to build the Studio. Skip it with
`-p:SkipStudio=true` or `REACTOR_SKIP_STUDIO=1`.

```powershell
cd src/Reactor.Studio
npm install
npm run dev     # Vite dev server on :5173, proxying the API to REACTOR_URL (default http://127.0.0.1:5180)
npm test        # unit tests (Vitest)
npm run build   # production build into dist/
```
