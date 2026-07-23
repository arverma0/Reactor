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
timeouts, threads, and fidelity. Cards trade detail for legibility as you zoom out.

**Routes are ports.** Every way out of a stage is a labeled port on its card — `✓ on success`, `✕ on fail`,
`◆ Approve` (a label the agent can choose), `ƒ condition`, `[A] Approve` (a human gate's options, in order), `⋔`
(parallel branches). Hover a card to see the ports it could have; dragging from one creates the edge with the right
`condition` or `label`, so you never type `outcome=fail`. Drop a connection on empty canvas to add a connected stage.

**Groups** are subgraphs, drawn as frames: drag a stage into or out of a frame to change its membership. A group's
label becomes a stylesheet class (`Deep thinking` → `.deep-thinking`), so the Models panel can give a whole group a
model.

**Artifacts.** Stages that declare `outputs` and `inputs` show pins; drag an output pin onto a stage to make it an
input, and the data flow is drawn as colored wires (toggle them with the controls). Pins whose input nobody produces
are marked.

**Panels.** The **Library** has every stage kind and factory patterns to drop in (plan · build · verify, test–fix
loop, design fight, review panel, human sign-off, critic loop, best of three, spec first, supervisor). The
**Inspector** edits the selection — prompts, tool commands, conditions (with a builder), retries and retry targets,
goal gates, timeouts, fidelity and threads, artifacts, models — or the pipeline itself (goal, defaults, retry targets,
hooks). **Models** edits the `model_stylesheet` as rules and shows which stages each rule reaches. **Outline** lists
stages, artifacts, and threads. **Files** opens and saves pipelines.

**Code and prompts.** The DOT drawer (`Ctrl+\``) is a two-way editor: edit the text and the canvas follows, edit the
canvas and the text follows. **Prompt preview** shows the exact prompt a stage will receive — preamble, task,
artifact sections, and the status-file instructions — for a chosen history of completed stages.

**Record lens.** When a pipeline has flown, the gauge button in the canvas controls puts its flight record on the
cards: how often each stage is reached, succeeds, how long it takes, what it costs, and how many retries it needed,
with cable thickness showing where the traffic goes.

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

- **Scenario.** Pick a preset (*Happy path*, *Realistic*, *Stress*, *Chaos*), tune the dials (how often agents
  succeed or ask for a retry, how often tools pass), or give a stage its own behavior: success odds, an exact script
  of outcomes (`✓ ✕ ✕ ✓`), how an agent chooses among its labeled routes, what a person picks at a gate, and how long
  the stage takes.
- **Calibrated.** When the pipeline has a flight record, the *Calibrated* preset fits the scenario to it: every stage
  that has flown gets the outcome mix, retry share, route choices, and typical duration it really had. The results
  then show **forecast vs. record** — the tunnel's success rate and cost per flight next to the real ones — so you can
  try a change in the tunnel and see what it should do to the numbers that matter.
- **Results.** A readiness gauge (success rate with its confidence interval), how flights go down (failed, never
  converged, goal gate unmet, dead end), stage executions per flight, hot spots, and traffic on the canvas. Every
  failure mode has a ▶ that replays one such flight on the canvas, step by step.
- **Animate one** plays a single simulated flight; **Walk through** starts a simulated run where you decide each
  stage's outcome on its card, to rehearse a path by hand.

## Flights

The flight list has every run: live runs on this server, and the run history on disk — runs of earlier servers and
`reactor run` from the command line — grouped by day, with duration and cost. Filter it to the pipeline open in
Design. The Flights tab shows a count when a run is waiting for a person.

**Mission control.** A live flight lights up the canvas: the running stage shows its elapsed time and latest log
lines, cables pulse as the engine takes them, and a human gate's question appears on its card with its options —
answer it there. A running agent stage (with the `agent` or `copilot` backend) can be **steered**: type an
instruction and the agent gets it in the middle of its turn. Cancel and resume from the run panel.

**Flight recorder.** The timeline under the canvas has a row per stage and a bar per execution; drag the playhead to
see the run at any moment, or replay it at 1–120× speed. The run panel has the flight log, and each stage has its
files (prompt, response, status, transcripts) for every visit, with the model, tokens, and cost of each.

**Re-fly.** Fork any flight from the snapshot saved just before one of its stages and fly on from there — as flown,
or with the pipeline as it is now in Design (the dialog shows what changed) and optional context overrides. Earlier
stages are not flown again; their outcomes and context are restored. The re-flown flight links to its original and
can be compared with it stage by stage. The working directory is not rewound.

**Record.** The *Record* lens aggregates a pipeline's finished real flights (simulated runs and forks are left out):
how many landed, how long they take, what they cost, the trend flight by flight, where it breaks and why (the most
common failure reasons), and per stage its reach, success share, durations, cost per visit, and models. From there,
*Calibrate wind tunnel* carries the numbers into the tunnel.

## Architect

The Architect turns a description into a pipeline, or improves the one that is open:

- **Draft new** — describe the factory: what goes in, what comes out, what "done" means.
- **Revise** — say what should change.
- **Critique** — the Architect reads the pipeline and the **evidence** (its flight record and the latest wind tunnel
  report for this exact design), finds what matters most, ties each finding to numbers, and proposes a revision.

Choose the model it designs with, then watch it work: each draft is checked by Reactor's parser, validator, and wind
tunnel — every flight must land when every stage succeeds, and loops that do not converge under realistic failure
rates are flagged — and a failing draft goes back with the findings, up to three drafts. The proposal comes with its
rationale, the changes against the open pipeline, and a **forecast**: the current design and the proposal flown
1,000 times each under the same (calibrated, when there is a record) scenario and the same random draws, so the
difference you see is the design. *Preview* shows the proposal on the canvas with new and changed stages marked;
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
