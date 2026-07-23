# Spec conformance

Reactor implements the three Attractor specifications. This page maps each Definition-of-Done section to the code
and tests that cover it, records the decisions taken where a spec is silent or ambiguous, and lists what was
validated against real agents.

## Attractor spec (section 11)

| DoD section | Implementation | Tests (`tests/Reactor.Tests`) |
|---|---|---|
| 11.1 DOT parsing | `Graphs/DotParser.cs` | `Graphs.DotParserTests` (linear, graph attrs, multi-line blocks, chained edges, defaults, subgraphs, classes, quoting, comments, errors with line/column) |
| 11.2 Validation | `Validation/PipelineValidator.cs`, `Rules.cs` (23 rules, 4 of them for Reactor's artifact pins) | `Graphs.ValidationTests`, `Graphs.ArtifactValidationTests` |
| 11.3 Execution engine | `Execution/PipelineRunner.cs`, `EdgeSelector.cs` | `Engine.EngineTests`, `Engine.EdgeSelectionTests` |
| 11.4 Goal gates | `PipelineRunner` terminal check | `EngineTests.Goal_gate_*` |
| 11.5 Retry logic | `NodeRetryPolicy.cs` (presets, backoff + jitter, `Retryable` classification) | `EngineTests.Retries_*`, `Retry_exhaustion_*`, `StateTests.Backoff_*`, `Retry_policy_presets_*` |
| 11.6 Node handlers | `Handlers/*` | `EngineTests`, `HumanGateTests`, `ParallelTests`, `ResumeAndSupervisorTests`, `Custom_handlers_can_be_registered_by_type` |
| 11.7 State and context | `PipelineContext`, `Checkpoint`, `ArtifactStore` | `StateTests`, `Checkpoint_resume_produces_the_same_result_as_an_uninterrupted_run` |
| 11.8 Human-in-the-loop | `Human/*`, `WaitForHumanHandler`, server `WebInterviewer` | `HumanGateTests`, `Reactor.Server.Tests` |
| 11.9 Conditions | `Conditions/*` | `Graphs.ConditionTests`, `Conditional_branching_*` |
| 11.10 Model stylesheet | `Stylesheets/ModelStylesheet.cs` | `Graphs.StylesheetTests` (universal/shape/class/id specificity, explicit overrides) |
| 11.11 Transforms, HTTP mode | `Transforms/*`, `Reactor.Server` (`POST /run`, `GET /status`, `POST /answer` + the full 9.5 API) | `Graphs.TransformTests`, `Reactor.Server.Tests.ServerApiTests` |
| 11.12 Parity matrix | all of the above | each row has a named test, e.g. `Edge_selection: Condition_match_wins_over_weight`, `Weight_breaks_ties_*`, `Pipeline_with_more_than_ten_nodes_completes`, `Fan_out_runs_branches_concurrently_*`, `Checkpoint_resume_*`, `Orphan_node_is_reported` |
| 11.13 Integration smoke test | `examples/smoke.dot` | `ExampleTests.Smoke_example_meets_the_spec_integration_assertions`, `Spec_integration_smoke_test_with_a_scripted_llm`, and live runs (below) |

## Coding Agent Loop spec (section 9)

| DoD section | Implementation | Tests (`tests/Reactor.Agent.Tests`) |
|---|---|---|
| 9.1 Core loop | `Session/Session.cs` | `Session.SessionLoopTests` (natural completion, tool rounds, limits, concurrency) |
| 9.2 Provider profiles | `Profiles/ProviderProfiles.cs`, `SystemPrompts.cs` | `Session.ProviderProfileTests` |
| 9.3 Tool execution | `Session.ExecuteSingleToolAsync` (schema validation, unknown tools, errors as results, parallel calls) | `SessionLoopTests.*tool*`, `Tools.*` |
| 9.4 Execution environment | `Environments/LocalExecutionEnvironment.cs` + wrappers | `Environments.*` (timeouts kill process trees, env filtering, grep/glob) |
| 9.5 Output truncation | `Abstractions/Truncation.cs` | `TruncationTests`, `Large_tool_output_is_truncated_for_the_model_but_full_in_events` |
| 9.6 Steering | `Session.Steer`/`FollowUp` | `Steering_*`, `Follow_ups_*` |
| 9.7 Reasoning effort | `SessionConfig.ReasoningEffort` read per request | `Reasoning_effort_changes_apply_to_the_next_call` |
| 9.8 System prompts | `Prompts/PromptContext.cs` (layers, environment block, git context, AGENTS.md discovery with 32 KB budget) | `System_prompt_layers_*`, `Git_context_*` |
| 9.9 Subagents | `Subagents/SubAgentManager.cs` | `Session.SubagentTests` |
| 9.10 Events | `Session/Events.cs` (all 15 kinds) | assertions throughout `SessionLoopTests` |
| 9.11 Error handling | `Session.ProcessInputAsync`/`ShutdownAsync` | `Authentication_error_*`, `Retryable_errors_*`, `Context_length_error_*`, `Abort_*` |
| apply_patch (Appendix A) | `Tools/ApplyPatch/*` | `Tools.PatchParserTests`, `Tools.ApplyPatchToolTests` |

## Unified LLM spec (section 8)

| DoD section | Implementation | Tests (`tests/Reactor.Llm.Tests`) |
|---|---|---|
| 8.1 Core infrastructure | `Client.cs`, `Middleware.cs`, `Catalog/ModelCatalog.cs` | `FoundationTests`, `HighLevel.ClientTests`, `Providers.ClientRoutingTests` |
| 8.2 Provider adapters | `Providers/{OpenAI,Anthropic,Gemini,OpenAICompatible}/*` | one test class per adapter, against recorded request/response/stream payloads |
| 8.3 Message model | `Models/*` | `FoundationTests`, adapter round-trip tests |
| 8.4 Generation | `HighLevel/Ai.cs` | `HighLevel.GenerateTests`, `StreamTests`, `ObjectGenerationTests` |
| 8.5 Reasoning tokens | thinking parts with signatures / encrypted reasoning / `thoughtSignature` round-trip | adapter tests |
| 8.6 Prompt caching | Anthropic automatic `cache_control` breakpoints; cached-token usage for all providers | `AnthropicAdapterTests` |
| 8.7 Tool calling | `HighLevel/ToolExecution.cs`, per-adapter translation | `GenerateTests`, adapter tests |
| 8.8 Errors and retry | `Errors.cs`, `Retry.cs`, `Utilities/ProviderHttp.cs` | `FoundationTests`, adapter error tests |

## Decisions where the specs leave room

**Engine**

- *FAIL routing*: a failed stage uses normal edge selection (so unconditional edges carry failures forward, as in
  the spec's section 3.2 pseudocode), then `retry_target`, `fallback_retry_target`, else the run fails. Human gates,
  parallel, and fan-in nodes follow only explicit routes when they fail, so a skipped gate cannot loop forever.
- *Final status*: as specified, reaching the exit with goal gates satisfied is success even if earlier stages failed
  and were routed onward; `reactor run` lists such stages.
- *Retries* apply to `retry` and `fail` outcomes (DoD 11.5) and to exceptions whose `Retryable` is true.
- *Conditional nodes* pass the previous stage's outcome through and never retry.
- *Dead ends*: a node with outgoing edges but none eligible fails the run with a clear reason; a node with no
  outgoing edges ends the run (after the goal-gate check).
- *Exit node* is executed and checkpointed, so `checkpoint.current_node` is the exit node (11.13).
- *Context bookkeeping*: the engine always sets `outcome`, `preferred_label`, and `failure_reason` (clearing stale
  values), plus `current_node`, `internal.*`, and a `graph.*` mirror.
- *Reachability*: an unreachable node is an error, per the rule table in section 7.2 (the parity matrix mentions a
  warning; the rule table is the normative definition).
- *Threads*: without an explicit thread, a `full`-fidelity stage continues the previous stage's thread, so a chain of
  `full` stages shares one conversation. Backend sessions for a thread live for one engine execution (a resume or
  `loop_restart` starts new ones); a stage whose thread session was lost (timeout, eviction, parallel fork) gets a
  `summary:high` preamble rather than starting blind.
- *Parallel*: branches run on cloned contexts until they reach a fan-in, the exit, or a dead end; the parallel
  outcome always jumps to the join (fan-in decides how to treat failures); `first_success` cancels the rest.
- *Human gates*: `human.timeout` (else the node timeout) and `human.default_choice` (matched against target id,
  accelerator key, or label); `human.input="freeform"` stores text in `human.gate.text`.
- *Codergen*: non-`full` fidelity prepends a context preamble; agent backends get the status-file protocol appended;
  an agent-written `status.json` wins over the default outcome; revisited stage directories archive earlier files to
  `history/visit-N/`.
- *Stylesheet*: applies to attributes not set explicitly on the node (node-default blocks count as not explicit).
- *Reasoning effort*: the `high` default is sent only to models the catalog knows to reason; unknown models get no
  effort parameter unless the node or stylesheet sets one (OpenAI-compatible servers often reject it), and the
  Copilot and CLI backends pass only explicit values.
- *Loop restart*: a fresh context in `<run>-restart-N`, at most 100 restarts by default.

**Agent loop**

- The full assistant `Message` is kept in history, preserving thinking signatures and provider metadata.
- `max_turns` counts assistant responses across the session; `max_tool_rounds_per_input` counts tool rounds per
  input. Hitting either emits `TURN_LIMIT` and returns to idle.
- A reply ending in `?` without tool calls moves the session to `AWAITING_INPUT`.
- Context-window overflow emits a warning and leaves the session usable; other non-retryable errors close it.
- Subagents get a non-owning view of the parent's environment, so closing one never cleans up the parent.
- `apply_patch` accepts the Appendix A grammar plus deviations that real models produce: stacked `@@` lines that
  narrow the location step by step (as in OpenAI's reference implementation), concatenated patches and stray or
  space-prefixed `*** End Patch` lines between file sections, a first hunk without `@@`, heredoc and markdown-fence
  wrappers, and unified-diff `@@ -1,3 +1,4 @@` headers. Lines added with no context go right after the innermost
  `@@` line, or at the end of the file when there is none. Errors give the patch line and the fix (for example, use
  `*** Add File` for a file that does not exist).

**Additions beyond the specs**

- Backends for the GitHub Copilot SDK, the Copilot CLI, and Claude Code; a per-node `backend` attribute and router.
- Artifact pins: `inputs`/`outputs` node attributes declare the artifacts a codergen or tool stage reads and produces
  (text, or files relative to the working directory). Prompts list them, a successful stage that did not write a
  declared file fails (and is retried and routed like any failure), produced artifacts are recorded as versioned
  `artifacts.<name>` context values with an `ArtifactProduced` event, and four lint rules (`artifact_syntax`,
  `artifact_inputs`, `artifact_order`, `artifact_handlers`) check the wiring. Branch contexts are still not merged at
  a fan-in, so artifacts made inside parallel branches stay in their branch. See
  [pipelines.md](pipelines.md#artifacts-reactor-extension). (Unrelated to the spec's `ArtifactStore`.)
- A default budget of 500 (CLI) / 1000 (server) stage executions per run against runaway loops, and 200 tool rounds
  per stage on the `agent` backend (the session-level default stays unlimited, as specified).
- Copilot model verification against the account's model list (the runtime otherwise substitutes models silently).
- `reactor doctor`, `reactor render` with status colors, the web console, stage artifact endpoints.

## Live validation

The following ran against real agents on Windows, with the Copilot CLI 1.0.71 that the Copilot SDK bundles with the CLI
(the runtime of the `copilot` backend, and the executable of the `copilot-cli` backend):

| Scenario | Backend(s) | Result |
|---|---|---|
| `examples/smoke.dot` (spec 11.13): artifacts, goal gate, `checkpoint.current_node == done` | copilot | pass (35 s) |
| Two `full`-fidelity stages: the second recalls a secret word from the first | copilot, copilot-cli | pass; one session per thread |
| `test-fix-loop.dot` with real `pytest`; a variant with a deliberate bug exercising fail → fix → pass | copilot | pass; the fixer received the test output |
| `parallel-review.dot`: three concurrent sessions, fan-in, summary | copilot | pass |
| `stylesheet.dot`: Claude Sonnet 5, GPT-5.6 Luna, GPT-5.6 Sol per stylesheet | copilot | pass; requested == actual model per stage |
| `reactor serve`: token auth, JSON submission, human gate answered over HTTP, SSE replay, SVG graph | copilot | pass |
| Claude Code when not logged in | claude-code | clean non-retried failure with the CLI's message |

The native `agent` and `direct` backends and the OpenAI-compatible adapter ran against a local llama.cpp server
(b10068, CPU) with Qwen2.5-Instruct 1.5B and 7B (Q4_K_M):

| Scenario | Model | Result |
|---|---|---|
| `reactor agent` one-shot: create a file with a tool | 1.5B | pass; streaming and tool calls |
| Two `full`-fidelity stages: the second recalls a secret word from the first | 1.5B | pipeline mechanics pass (`thread_context` new → continued, no `reasoning_effort` sent; the transcript carried the word); the model answered it with a doubled letter |
| `examples/smoke.dot` on the `agent` backend | 1.5B | pass (the model answered without using tools) |
| `examples/smoke.dot` on the `direct` backend with `--max-tokens 300` | 7B | pass (20 s) |
| Forced fail → fix loop with real `pytest` | 7B | pipeline mechanics pass (the failing test output reached the fixer, tool errors were actionable, `--max-tokens` and stage `timeout`s bounded the run); the model itself could not write a passing fix |
| Tool calls cut off by `--max-tokens` (forced with a 40–60 token limit) | 7B | pass; each cut-off call returns a tool error explaining the limit, and the follow-up requests are accepted |

These runs drove several fixes: the status-file footer was reworded (a model wrote only `status.json` and reported
the task done), agent stages got a default tool-round budget, `apply_patch` learned the patch shapes the model
produced and gives errors that say how to fix the patch, its description uses a concrete example (the model copied
placeholder lines verbatim), `--max-tokens` with a truncation warning was added after a repetition loop ran until
the context window filled, and malformed tool-call arguments are replayed to Chat Completions servers as `{}`
(llama.cpp rejected the whole request otherwise).

Not validated live in this environment: the OpenAI (Responses API), Anthropic, and Gemini adapters against their
real endpoints (no API keys were available, and GitHub Models was blocked by the network). They are covered by
recorded-payload adapter tests and by full pipeline runs of the agent loop with real tools and a scripted model.
The provider smoke tests in the Unified LLM (8.10) and Coding Agent Loop (9.13) specs need real keys:
`reactor agent "..." --provider anthropic` runs the loop against a live model once a key is set.
