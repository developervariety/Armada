# Handoff and Dispatch Examination Results

> This document has two parts. Part 1 is the code audit of dispatch and handoff, its review, and the implementation status. Part 2 is the acceptance campaign log (formerly `HANDOFF_FINDINGS.md`).

> Continuation status: Claude accepted C1–C14 and Codex has accepted the reconciled position below. The final consensus section governs conflicting historical claims. Source review supports mechanisms; live observations remain attributed to Claude and are not independently reproduced by Codex.

This review looks at Armada's dispatch path (pending mission to captain launch) and handoff path (stage completion to the next stage, rework, and landing). It covers performance, stability, and resilience, with a focus on why mixing runtimes, and local models in particular, causes cascading failures that a Claude-only fleet does not.

**Final review status:** Claude and Codex reached declared consensus on the qualified recommendations below. The summary covers 68 finding identifiers (L5/H1 share a row), followed by 14 scored review challenges and their resolution. Intermediate debate-status statements are historical; the final acceptance section records the outcome. Agreement does not independently reproduce every finding.

Sources:

- A code read of `Armada.Core`, `Armada.Runtimes` and `Armada.Server` at `e7457432`.
- The live deployment at `view.homedns.org:8710` (tenant `default`): 151 missions, 1,513 events and 7 captains, sampled 2026-10-21.
- `docs/HYBRID_DEVELOPMENT_RESULTS.md`.

Each finding is marked CONFIRMED (read in code or seen in live data) or PLAUSIBLE (the mechanism is in code, but triggering it depends on runtime conditions that were not reproduced).

## Executive summary

### Why Claude-only works

Claude Code hides most of the weak points listed below:

- It reliably ends with a clean final answer. (Corrected in the continuation: Claude missions do **not** write the final-message artifact. Their `AgentOutput` is the whole decoded transcript, trimmed to its first and last parts. Codex, Mux and API runtimes are the ones with a clean final-message artifact; see C1 and the Claude response.)
- It reliably emits the exact `[ARMADA:VERDICT]` and `armada-plan` markers.
- It streams, so it never sits silent behind a 60 s proxy.
- It fails with structured errors (`rate_limit_error`, HTTP 429/5xx) that the classifier knows.
- It never finishes in milliseconds, which hides the launch races.
- A single runtime means no mixing of Admiral-hosted and Harbor-hosted stages, and no mixing of process-ID namespaces.

### What heterogeneity changes

Heterogeneity does not add one new bug. It raises the trigger rate of about a dozen latent ones at once:

- `AgentOutput` is the final-message artifact when one exists and non-empty, otherwise the bounded transcript, so its meaning varies by runtime and by outcome (H1, corrected per C1).
- Strict parsers fail terminally on formatting drift (H2, H3, L4).
- Local-model errors get classified as `Crash` (R3).
- Transient errors are retried with no backoff and no per-endpoint concurrency limit against one GPU (R4).
- Placement mixes hosts, so branches get lost (H7, H8, D15).
- Fast in-process runtimes expose races in the claim and launch sequence (D1-D6).

### What turns a failure into a cascade

The policy and topology, not the runtime. Live evidence:

- Voyage `vyg_mvih4v59_FfCL4jcd358` ran on **frontier models only** (Claude Worker, Codex Judge).
- The Architect planned 8 slices as one dependency chain of 72 missions.
- One slice-1 Judge exhausted its 2 rework rounds on an acceptance criterion that the third-party package could not satisfy.
- That single failure cancelled **55 downstream missions**: 55 of the deployment's 61 cancellations.
- The Worker, Test Engineer and Usability Engineer had filed `BriefContradiction/High` papercuts saying exactly this. The rework loop never reads papercuts.
- 11 Judge rework-limit failures occurred in the 6 days of events.

### The challenge to the framing

"Heterogeneity causes cascading failures" is half right. Heterogeneity raises the number of things that fail, and the following turn each of those failures into a voyage-wide loss:

- cancel-on-dependency-failure (L1)
- terminal-on-format-error parsers (H2, H3, L4)
- `Crash` classification of transient errors (R3)

Fix the amplifiers first. They pay off even for Claude-only fleets.

## Summary table

Simplicity: 5 means trivial to fix. Value: 5 means highest impact on stability for mixed fleets. Total = Simplicity + Value. Sorted by Total, then Value.

These are the original audit's scores. The Codex review (C1-C14), the reconciliation with the acceptance campaign log, and the implementation status table further down qualify them; where they conflict, the later sections govern.

| ID | Area | Finding | Simplicity | Value | Total |
|---|---|---|---|---|---|
| D1 | Dispatch | Dock-provisioning exception releases a captain that was never claimed, which can wipe a concurrent assignment | 5 | 5 | 10 |
| D7 | Dispatch | Dispatch tick has no per-mission try/catch; one throwing mission aborts the tick and the rest of the health loop (merge queue, recovery) | 5 | 5 | 10 |
| R3 | Runtime | `llm_stream_error`, `inference_error`, `runtime_error`, 400/408/413 are classified `Crash`, so a local blip fails the mission and halts the voyage | 5 | 5 | 10 |
| L4 | Handoff | A present but invalid `armada-plan` block fails terminally with no repair turn (an absent block falls through to the legacy parser instead, per C2); the error text is already actionable | 4 | 5 | 9 |
| H2 | Handoff | Verdict regex needs an exact bare line; backticks, bold or a revised verdict yield `None`, which fails the Judge terminally | 4 | 5 | 9 |
| H3 | Handoff | Judge contract violations (missing lens headings or Affected Case) fail terminally instead of re-running the Judge | 4 | 5 | 9 |
| H6 | Handoff | Stage build/test verification before handoff runs only for Mux and ApiEndpoint work stages; Claude and Codex stages hand unverified builds to the Judge. The opt-in Definition-of-Done gate checks every runtime, but only at landing (C7) | 4 | 5 | 9 |
| D3 | Dispatch | A failed captain is released to Idle before being marked Stalled or Quarantined; it is re-claimed in the gap and the next mission fails the same way | 4 | 5 | 9 |
| D9 | Dispatch | A misconfigured ApiEndpoint captain fails each mission terminally, returns to Idle, and is picked first again | 4 | 5 | 9 |
| R1 | Runtime | API captains make blocking non-streaming calls; the 60 s proxy cuts long generations before Armada's 120 s timeout | 4 | 5 | 9 |
| R2 | Runtime | ApiEndpoint never sets Ollama `num_ctx` and never compacts history; context is silently truncated (PLAUSIBLE) | 4 | 5 | 9 |
| R4 | Runtime | Transient resume has no backoff and no per-endpoint concurrency cap (`AgentSettings.MaxConcurrent` is never read); retry storm on one GPU | 4 | 5 | 9 |
| HB1 | Harbor | Harbor sequence is acked before the handler's DB work; if the handler throws, the exit is lost and later reported as vanished (-1) | 4 | 5 | 9 |
| H5 | Handoff | The numbered-list fallback turns Architect narration ("1. Read the README") into full Worker-to-Judge chains | 5 | 4 | 9 |
| HB3 | Harbor | Interrupted (-1) mission with an active dock is resumed in place without limit | 5 | 4 | 9 |
| HB5 | Harbor | Admiral-to-Harbor frames over 64 KB are decoded per fragment, corrupting multi-byte characters; no size cap | 5 | 4 | 9 |
| L1 | Cascade | Failure cancels the entire transitive dependent DAG (55 missions live); passing earlier slices are stranded unlanded | 3 | 5 | 8 |
| L5/H1 | Handoff | `AgentOutput` means different things per runtime (Codex/Mux/API: final artifact when present; Claude/Cursor/Gemini/OpenCode and failed or empty-artifact runs: the head+tail-trimmed transcript, which includes progress lines; corrected per C1); parsers and the 3,000-character excerpt read it; live papercuts report truncation, heartbeat JSON and another mission's diff | 3 | 5 | 8 |
| D2 | Dispatch | Pending to Assigned is a blind write and the captain is claimed after slow provisioning; orphan recovery (30 s grace) can revert it and a mission can run twice | 3 | 5 | 8 |
| D6 | Dispatch | Exception after claim (non-Harbor IOException, signal write) leaves a captain Working with no process forever; the rollback block is copy-pasted 6 times | 3 | 5 | 8 |
| D8 | Dispatch | Launch-failure backoff is keyed by mission, not captain; a broken captain is re-picked deterministically and other missions loop for up to 6 attempts | 3 | 5 | 8 |
| D13 | Dispatch | Persona default captain and its tier are saved as hard pins; restart does not clear pins or escalation; missions sit Pending while other captains idle | 3 | 5 | 8 |
| H7 | Handoff | Intermediate stage branches are never pushed; an Admiral-hosted stage followed by a Harbor-hosted stage silently starts from the default branch | 3 | 5 | 8 |
| L3 | Rework | Every Judge rework round re-runs the whole Worker, UX, Test, Linter, Judge chain (5 missions, 30+ min on local models) | 4 | 4 | 8 |
| D4 | Dispatch | Fast in-process runs (ApiEndpoint, Mux) can exit between launch and the ProcessId write; the captain is left Working; the recovery relaunch path has no guard at all | 4 | 4 | 8 |
| D11 | Dispatch | With no priorities or tier, routing picks the alphabetically first idle captain (`ORDER BY name`) | 4 | 4 | 8 |
| D12 | Dispatch | A null model is inferred as Standard; `qwen:7b`-style local models count as Standard and are preferred for Standard work | 4 | 4 | 8 |
| D15 | Dispatch | When every Harbor is at capacity, the mission falls back to the Admiral, where Codex or Mux may not be installed | 4 | 4 | 8 |
| H8 | Handoff | The pinned-Harbor (branch affinity) path skips the runtime capability check; launch fails 6 times (about 17 min), then cascades | 4 | 4 | 8 |
| H10 | Handoff | Final integration treats WorkProduced, Failed and Cancelled siblings as settled and starts without their landed work | 4 | 4 | 8 |
| R5 | Runtime | One failed model call ends the API run and discards the conversation; no in-loop retry | 4 | 4 | 8 |
| HB4 | Harbor | Kill requests run inside the Harbor receive loop with a 10 s blocking wait; stop-all drops heartbeats | 4 | 4 | 8 |
| R10 | Runtime | HTTP 529 and `overloaded_error` are classified UsageLimit, so a transient overload causes terminal failure plus quarantine | 5 | 3 | 8 |
| R8 | Runtime | Mux exit 0 is trusted over a failed or missing `run_completed`; `completed_with_errors` is promoted to 0 | 5 | 3 | 8 |
| R14 | Runtime | Every output line triggers an unthrottled captain heartbeat DB write; SQLite lock contention (PLAUSIBLE) | 5 | 3 | 8 |
| H14 | Handoff | One `RedispatchAttempts` counter is shared by resume, no-op re-dispatch and interrupted-exit limits | 5 | 3 | 8 |
| H15 | Handoff | The no-op and boundary-violation completion paths return without releasing the captain | 5 | 3 | 8 |
| H16 | Handoff | The "Harbor dock not found" failure path skips cancel-dependents and the voyage status update; the voyage never settles | 5 | 3 | 8 |
| L2 | Rework | The rework loop ignores spec-conflict signals (`BriefContradiction/High` papercuts) and retries an unsatisfiable criterion until the budget is gone | 3 | 4 | 7 |
| L6 | Routing | Process routing is keyed by a bare int across OS PIDs, ApiEndpoint synthetic IDs (reset on restart) and Harbor PIDs; Harbor dedupes only against other Harbor jobs (PLAUSIBLE collision) | 3 | 4 | 7 |
| L7 | State | Pipeline state lives in text: rework round and escalation parsed from titles, handoff and escalation markers in descriptions | 3 | 4 | 7 |
| D10 | Dispatch | Selection never consults `ModelEndpoint.HealthStatus` or runtime availability | 3 | 4 | 7 |
| H11 | Handoff | Restart recovery replays handoff without the Judge or scope gates; the Architect fan-out is neither idempotent nor recovered | 3 | 4 | 7 |
| H13 | Handoff | Crash rescue spawns a standalone Worker off main, outside the voyage, for any persona (even a crashed Judge) | 3 | 4 | 7 |
| R7 | Runtime | OpenCode, Gemini and Cursor have no provider-error parsing or outcome tracking; every non-zero exit is `Crash` | 3 | 4 | 7 |
| R11 | Runtime | Crash and quarantine tracking is per captain, not per endpoint; a down Ollama fails missions captain by captain | 3 | 4 | 7 |
| R16 | Runtime | Failure semantics are free-form `ErrorType` strings per runtime; the classifier must list them all (root cause of R3, R7, R8, R9) | 3 | 4 | 7 |
| HB2 | Harbor | The 20 s vanished-job grace ignores the Harbor's replay backlog and survives reconnects | 3 | 4 | 7 |
| T1 | Testing | No fault-injection runtime that emits each failure kind, slow first token, millisecond exit, or malformed verdict | 3 | 4 | 7 |
| H9 | Handoff | Two dependents of one slice are handed the same branch; `git worktree add` refuses it, or commits stack | 4 | 3 | 7 |
| H17 | Handoff | Halting a non-pipeline voyage marks running siblings Cancelled in the DB only; their processes keep running | 4 | 3 | 7 |
| R6 | Runtime | No warm-up or extended first-call timeout for cold local model loads | 4 | 3 | 7 |
| R9 | Runtime | The last provider error wins, so a non-fatal `tool_timeout` can mask the fatal cause | 4 | 3 | 7 |
| R12 | Runtime | The exit path blocks thread-pool threads for up to about 12 s (`.Wait`, `GetResult`, `Thread.Sleep` loop) | 4 | 3 | 7 |
| R13 | Runtime | `StopAsync` never signals gracefully, blocks 10 s, ignores its token, and does not reap grandchildren on natural exit | 4 | 3 | 7 |
| HB6 | Harbor | Requests on a superseded link wait their full timeout (or forever when `timeoutMs < 1`) | 4 | 3 | 7 |
| F2 | Refactor | Three "is the voyage settled" implementations with different rules | 4 | 3 | 7 |
| R15 | Runtime | Mux text buffers are unbounded; a degenerate model without newlines grows them without limit | 5 | 2 | 7 |
| F3 | Refactor | Persona identity is string-compared in places (`"Architect"`) and `PersonaCatalog.Matches` in others | 5 | 2 | 7 |
| D5 | State | Captain and mission lifecycle writes are full-row overwrites from stale objects; only `TryClaimAsync` and a few `TryUpdateIfStatusAsync` calls use compare-and-swap | 2 | 5 | 7 |
| L8 | State | Rework chain creation (rework, N stages, new Judge, re-pointed dependents) is non-transactional | 3 | 3 | 6 |
| D14 | Dispatch | Model validation and chat run ApiEndpoint on the Admiral, but missions may run it on a Harbor (`localhost` means a different machine) | 3 | 3 | 6 |
| D16 | Dispatch | One global stall threshold for all runtimes; API stop does not await loop exit before relaunching into the same dock | 3 | 3 | 6 |
| H20 | State | `MissionStateMachine` is not enforced; WorkProduced to Failed (illegal in the table) is written in 6 places | 3 | 3 | 6 |
| D17 | Perf | The dispatch tick is N+1-heavy (about 12 reads per pending mission, full vessel mission history) and has 4 un-serialized callers | 2 | 4 | 6 |
| D18 | Refactor | Eligibility exists in 3 drifting copies; Harbor routing runs 3 times per launch | 2 | 4 | 6 |
| F1 | Refactor | `MissionService.cs` is 5,116 lines mixing dispatch, completion gates, handoff, Architect fan-out, rework and git | 1 | 4 | 5 |

## Live deployment evidence

Missions (151):

| Runtime | Complete | WorkProduced | Failed | Cancelled |
|---|---|---|---|---|
| Codex | 18 | 6 | 1 | 0 |
| ClaudeCode | 12 | 16 | 0 | 0 |
| Mux (qwen3.8:27b) | 27 | 10 | 0 | 1 |
| Never assigned | 0 | 0 | 0 | 60 |

- 60 of 61 cancellations were missions that never ran: `DependencyFailed` descendants of a failed or cancelled mission. 55 came from one voyage.
- `mission.failed` events over 2026-10-15 to 10-21 (15 in total):
  - 11 were the Judge rework limit, either the per-slice budget or the overall round limit. Each says "Strict captain selection prevents automatic escalation", "limit reached after 3 rework round(s), including an escalated attempt", or gives the bare verdict.
  - 1 was a Judge contract violation: "NEEDS_REVISION but did not exhibit a concrete affected case". This is H3 seen live.
  - 1 was "Architect plan contract rejected: unknown objective requirement ID" (×N). The plan was discarded and the voyage was manually re-dispatched about 75 minutes later.
  - 2 were rescue Workers that exited with code 1.
- The Judge on `msn_mviii9fc_yY0gsAweJMJ` issued `FAIL`, `FAIL`, then `NEEDS_REVISION` for the same reproduced behavior: PolyPrompt 3.5.0 reports `Completed` for partial tool arguments, while AC-INF-3 required `Failed`. Workers can't fix a third-party package in a "pin contracts" slice. This is a specification conflict, and the system treated it as a quality defect three times.
- Papercuts filed by captains that point at handoff defects:
  - "Prior-stage excerpt truncated mid-acceptance section" (×5)
  - "excerpt begins with raw tool_progress heartbeat JSON and the diff excerpt shows another mission's archive file"
  - "AC-INF-3 expects Failed … PolyPrompt 3.5.0 reports Completed" (BriefContradiction/High ×3)
- ApiEndpoint captain "Deepseek v4.1 Flash" logged synthetic PIDs `2000000001`-`2000000009`, each reused up to 6 times across Admiral restarts. That is the input to L6.

## Detailed findings

### 1. Cascade amplifiers (fix these first)

**L1. Dependency failure cancels the whole downstream DAG.** CONFIRMED, live.

- `CancelDependentsAsync` recursively cancels every transitive dependent.
- With Architect-chained slices, one slice-1 failure deletes the plan for 7 other slices: their missions are terminal and must be re-planned.
- Earlier passing slices never land because their terminal Judge has a dependent (`MissionService.cs:1674`, `1723-1729`).

Fix:

1. Add a `Blocked` status: dependents of a failed mission wait for an operator to restart, re-route or skip the failed stage, and resume when it succeeds.
2. Land each slice when its own Judge passes; branch the next slice from the landed commit.
3. Have the Architect contract flag a dependency only when it is real. The live 8-slice chain had at least two slices (Dashboard, OpenAPI) whose only dependency was ordering.

**L2. The rework loop cannot tell "wrong code" from "impossible requirement".** CONFIRMED, live.

- `TryScheduleJudgeReworkAsync` (`MissionService.cs:4787`) always sends findings back to the Worker.
- Captains already report `BriefContradiction/High` papercuts, but nothing in the completion path reads them.

Fix:

- Add a Judge verdict, or a papercut-driven rule, `SPEC_CONFLICT`. It routes to the Architect, or to a human review gate, with the conflicting criterion quoted.
- Stop rework when the same Affected Case (file plus scenario) repeats across rounds. Today the stagnation check only compares commit hashes.

**L3. Rework re-runs the full verification chain.** CONFIRMED (`MissionService.cs:4865-4894`).

- Each round creates Worker, Usability Engineer, Test Engineer, Linter and Judge missions.
- The code comment says this is deliberate ("must not silently downgrade FullPipeline"). That concern is sound for correctness, but the cost is 5 serial stages per round, and on local models each is 3-15 minutes.

Alternative:

- Rework round = Worker, then the deterministic build/test gate (H6), then Judge.
- Re-run the advisory stages only on the final passing round, or only when the rework diff touches files those stages changed.
- This keeps FullPipeline semantics for what lands and removes most of the rework cost.

**R3 / R10. Failure misclassification.** CONFIRMED (`RuntimeFailureClassifier.cs:42-126`).

- `Crash` is not resumed (`AdmiralService.cs:981-991`). It goes to terminal failure and `HaltVoyageAsync`, and counts toward crash-loop quarantine.
- Unknown error types fall through to `Crash`, including:
  - Mux `llm_stream_error`, which is exactly what a proxy cut produces
  - ApiEndpoint `inference_error` and `runtime_error`
  - HTTP 408 and 413
- `529` and `overloaded_error` map to `UsageLimit`, which also quarantines.

Fix:

- Map stream, connection, 5xx-less inference and 408 errors to `Transient`, and 529/overloaded to `Transient`.
- Map 400/413 bodies that mention context or tokens to `TaskFailure`.
- Longer term, do R16.

**L4 / H2 / H3. Strict output contracts are terminal.** CONFIRMED.

- `ArchitectPlanParser.ValidateContract` enforces about 20 rules and requires an exact ```` ```armada-plan ```` fence. Failure sets `Failed/InvalidOutput` at `MissionService.cs:2853-2863`.
- `JudgeVerdictParser._VerdictLine` is anchored `^\[ARMADA:VERDICT\][ \t]+(PASS|FAIL|NEEDS_REVISION)[ \t]*$`. Two different verdicts anywhere count as `None`, and `<think>` blocks are not stripped.
- Missing Judge lens headings or an Affected Case also fail terminally (`MissionService.cs:1611-1669`, `JudgeContract.cs`).
- This is a key place where local and non-Claude models differ: they drift on formatting, not substance.

Fix, one shared pattern: a **bounded repair turn**.

- On any contract or parse error, resume the same captain once with the exact error list, which `ArchitectPlanParser` already produces in actionable form.
- Then try one alternate captain, then fail.
- Normalize before parsing: strip `<think>…</think>`, surrounding backticks, `**` and trailing punctuation; take the last verdict line rather than rejecting conflicts; accept ```` ```json ```` blocks that contain a `missions` array.

**H5. The numbered-list fallback fans out junk.** CONFIRMED (`MissionService.cs:3305-3321`, `3614-3661`).

- When the legacy parser reads narration, including a transcript fallback, numbered narration can become a Worker-to-Judge chain. Mux does not invariably supply all-turn output (C1).
- Fix: delete the fallback and use the repair turn instead.

### 2. Handoff integrity

**H1 / L5. `AgentOutput` has no single meaning.** CONFIRMED.

| Runtime | What `AgentOutput` contains |
|---|---|
| Claude | Decoded mission transcript; no final-message artifact written by this runtime |
| Codex | Non-empty final-message artifact preferred; transcript fallback otherwise |
| Mux | Non-empty final-message artifact preferred, including reconstruction on success; transcript fallback otherwise |
| ApiEndpoint | Non-empty final assistant artifact preferred; empty or missing artifact falls back to transcript |
| Other runtimes | Depends on artifact production/delivery; otherwise accumulated output |

- Verdict parsing, plan parsing, no-op detection and the 3,000-character head-and-tail handoff excerpt all read this one field. Multi-turn runtimes therefore lose the middle, where the findings are, and can carry progress JSON.
- The live papercut about "another mission's archive file" in the diff excerpt suggests the diff source is also not scoped to the mission's own commit range (PLAUSIBLE; worth a targeted test).

Fix:

- Persist a canonical `FinalMessage` per launch: Claude `result`, Codex and Mux `--output-last-message`, the ApiEndpoint final assistant text.
- Parse and hand off only from it, and keep the transcript separately.
- Build the excerpt structurally (named sections: Verdict, Affected Case, Acceptance) rather than by character truncation.
- Compute the diff as `merge-base(stage start)..HEAD` of that dock.

**H6. Pre-handoff build/test verification is runtime-dependent.** CONFIRMED source property; qualified by C7.

- `VerifyStageWorkAsync` returns null unless the runtime is `Mux` or `ApiEndpoint`.
- Claude and Codex skip this pre-handoff gate. A separate opt-in Definition-of-Done gate checks every runtime at landing; flaky baselines can disproportionately reject Mux/API at the earlier gate. Live enablement of the landing gate was not verified.
- Fix: run the gate for every work-producing stage, compared against a cached baseline result for the base commit.

**H7 / H8 / D15. Placement loses the branch or the runtime.** CONFIRMED.

- Intermediate branches exist only where the stage ran. An Admiral-to-Harbor transition creates the branch from the default branch (`HarborDockManager.cs:479-498`).
- The pinned-Harbor path skips the capability check (`HarborRouter.cs:40-50`).
- A Harbor at capacity silently falls back to the Admiral (`HarborRouter.cs:158`).

Fix:

- Push each stage branch to origin, or to a private ref namespace, at handoff.
- If an expected branch is missing, fail placement loudly.
- Treat "Harbor at capacity" as `WaitFor`, not fallback.
- Fall back to the Admiral only when it advertises the runtime.

**H10, H9, H11, H13, H16, H17.** These are smaller handoff and settlement defects, all CONFIRMED in code.

- H10: integration waits for WorkProduced instead of landed work.
- H9: sibling dependents share a branch.
- H11: crash recovery skips the gates, and the Architect fan-out is not idempotent.
- H13: rescue Workers run outside the pipeline.
- H16: one failure path doesn't cascade.
- H17: DB-only cancellation of running siblings.

Each fix is local; see the table.

### 3. Dispatch correctness (races and stuck states)

Underlying cause: four un-serialized dispatch callers (health tick, every process-exit handler, completion handlers, REST dispatch) operate on full-row stale objects. Only `Captains.TryClaimAsync` and a few `Missions.TryUpdateIfStatusAsync` calls are atomic.

**D1.**

- `MissionService.cs:603` calls `_Captains.ReleaseAsync(captain)` in the provisioning `catch`, before the claim at line 629.
- Fix: delete the release (the `dock == null` branch beside it already does the right thing).

**D7.**

- `AdmiralService.DispatchPendingMissionsAsync` (around line 1917) has no per-mission try/catch.
- Fix: wrap each `TryAssignAsync` and each health-loop stage separately.

**D2, D3, D4, D6, D5.** The fix is one change in design:

- Claim the captain before provisioning.
- Replace lifecycle `UpdateAsync` calls with guarded transition methods (`TryReleaseAsync(id, expectedMissionId)`, `TryQuarantineAsync`, `TrySetProcessAsync`, `TryTransitionMission(from[], to)`), implemented in all four providers.
- Wrap claim-to-launch in a single try/catch with one `RollbackAssignmentAsync`, replacing the 6 copies.
- Compute the final captain state (Idle, Stalled or Quarantined) once and write it once.

Fast-exit runtimes (ApiEndpoint, Mux) are what make D4 visible.

**D8, D9, D10, R11. Exclude broken captains instead of failing missions.**

- Record launch failures per (mission, captain) and exclude that captain on retry.
- Trip a per-captain breaker after N failures and a per-endpoint breaker for shared Ollama.
- Check endpoint `HealthStatus` and ApiEndpoint config validity inside eligibility, so the pending reason explains the skip.

**D11, D12, D13. Routing defaults that favor the wrong captain.**

- D11: name-order tie-breaking. Fix: least recently used.
- D12: null or unknown models count as Standard. Fix: resolve the endpoint model; size suffixes `:1b/:3b/:7b` count as Economy; unknown counts as Economy.
- D13: persona defaults saved as hard `RequestedCaptainId` and `Tier`. Fix: resolve them at dispatch time only, and clear pins and escalation on restart.

**L6. Integer PID as the routing key.** PLAUSIBLE collision, CONFIRMED design.

- `_ProcessToMission` and `_ProcessToCaptain` are keyed by `int` across three namespaces:
  - OS PIDs
  - `ApiAgentRuntime._PidCounter` (static, restarts at 2,000,000,000 every Admiral restart)
  - Harbor native PIDs (deduplicated only against other Harbor jobs, `HarborConnectionManager.cs:838-853`)
- Fix: key all lifecycle routing by an opaque `LaunchId` string (for example `lch_…`), stored on the mission and captain. Keep the PID as display or kill metadata only.
- This also removes the `RegisterProcessId` remapping dance.

### 4. Execution layer (local-model specific)

**R1. Non-streaming calls behind a 60 s proxy.**

- `ApiAgentRuntime.cs:301` and `:413` wait for the whole response, so a long `write_file` generation or a cold load produces a 504 every time. A deterministic failure looks transient.
- Fix: use PolyPrompt's streaming tool chat, and validate endpoint timeout against proxy timeout at endpoint save.

**R2. Context management.** PLAUSIBLE impact.

- There is no `num_ctx` anywhere in `src`, and the conversation grows unbounded with 24 K-character file pages.
- Mux sets its own context (131072 in the hybrid runs), so this hits ApiEndpoint captains against Ollama.
- Fix: a per-endpoint `ContextLength`, history compaction past a budget, and `context_limit_exceeded` when prompt tokens near the window.

**R4. Retry storm on one GPU.**

- Transient resume is immediate (`AdmiralService.cs:2018-2050`).
- `AgentSettings.MaxConcurrent` is dead configuration: displayed by Helm, never enforced.
- Different models on one Ollama force VRAM swaps, each a cold load past 60 s. This is a direct mechanism for "mixing makes it worse" (PLAUSIBLE).
- Fix: a per-endpoint (base URL) concurrency limit enforced in eligibility, jittered exponential backoff before a Transient resume, and optional one-model-per-endpoint pinning.

**R5, R6. Resilience inside the run.**

- Retry 5xx, 429 and connection errors in the loop (2-3 times, honoring Retry-After) before abandoning the conversation.
- Warm the model with `keep_alive` before turn 1.

**R7, R8, R9, R16. Outcome determination.**

- Replace the per-runtime free-form `ErrorType` strings and Mux-only exit fix-ups with an `IRuntimeOutcomeTracker` per runtime:
  - `ObserveLine`
  - `ResolveExit(exitCode)` returning a typed `RuntimeFailureKindEnum` plus `IsFatal`
- Keep the HTTP-status classifier as a fallback.
- Add a test asserting that every `ErrorType` a runtime can emit is classified.

**R12-R15.** Thread-pool blocking on exit, non-graceful `StopAsync`, unthrottled per-line heartbeat writes, unbounded Mux buffers. Each is a local fix (see the table). R12 and R14 compound under many concurrent captains: chatty local models produce far more lines.

### 5. Harbor link

- **HB1.** Acknowledge a sequence only after its handler commits, with a per-message try/catch (`HarborConnectionManager.cs:347` vs `:418`, `:446`).
- **HB2.** Reset the vanished-job grace on each handshake, and start it only after replay reaches the Harbor outbox high-water mark.
- **HB3.** Bound in-place resume after interrupted exits (`AdmiralService.cs:2115-2118`).
- **HB4.** Run kills off the receive loop.
- **HB5.** Collect fragments into a `MemoryStream`, decode once, and enforce a maximum frame size (`WebSocketHarborTransport.cs:79`).
- **HB6.** Fail pending requests immediately when their link is superseded.

### 6. Performance

- **D17.** Each tick, every pending mission runs a full `TryAssignAsync`: vessel, voyage, dependency and persona reads, plus the full mission history of the vessel. The capacity gate passes if any idle captain exists, even an ineligible one.
- Replace the four entry points with **one serialized dispatcher**:
  - take one snapshot per tick (idle captains, personas, active counts per vessel and endpoint, voyages)
  - match in memory
  - claim with compare-and-swap
  - have the event paths only signal it (`SemaphoreSlim` plus `Channel<bool>`)
- This also closes most of section 3's races.
- **R14** (per-line heartbeat writes) and **R12** (blocking exit path) are the other throughput risks under many concurrent captains.

### 7. Refactors and simplification

1. **Split `MissionService.cs` (F1).** Target components:
   - `Dispatcher` (D17)
   - `CompletionPipeline`: ordered, idempotent gates (no-op, boundary, verify, verdict, DoD), with a "completion processed" marker that fixes H11
   - `PipelineHandoffService`
   - `ArchitectFanOutService`: transactional
   - `JudgeReworkService`
2. **Typed pipeline state (L7, L8).** Replace title, description and marker parsing with columns:
   - `SliceRootMissionId`
   - `ReworkRound`
   - `EscalatedFromCaptainId`
   - `HandoffSourceMissionId`
   - `HandoffContext` (separate from `Description`)
   - `ResumeNotes` (separate from `Description`)

   Build each rework chain in one DB transaction.
3. **One eligibility function (D18).** `CaptainEligibility.Evaluate(mission, captain, snapshot)` returns an eligible flag or a reason. Selection, the pending-reason description and escalation all use it. Choose the Harbor once and carry that decision through to launch.
4. **One guarded transition API (D5, H20).** Every status write goes through `MissionStateMachine`, using the provider-level compare-and-swap methods.
5. **One voyage-settlement function (F2)** and consistent `PersonaCatalog` use (F3).
6. **Typed runtime outcomes (R16).** See section 4.
7. **Fault-injection runtime for Touchstone (T1).** A `ScriptedAgentRuntime` that can:
   - exit in 1 ms
   - stall its first token for 70 s
   - emit each error type
   - emit a backticked verdict
   - omit the plan fence
   - exit 0 with no work

   Run the pipeline suites against a matrix of {Claude-like, Codex-like, Mux-like, flaky local}. This is what makes heterogeneity regressions visible before deployment.

## Recommended order

1. **One-line guards, same day:** D1, D7, R3, R10, HB3, H5, H15, H16.
2. **Repair turns instead of terminal failure:** L4, H2, H3, with normalization.
3. **Blast-radius control:** L1 (Blocked instead of Cancelled; land passing slices), L2 (spec-conflict exit), H6 (gate every runtime).
4. **Local-model resilience:** R1 (streaming), R4 (per-endpoint cap and backoff), R5 (in-loop retry), R11 and D9 (breakers and exclusion).
5. **Structural fixes:**
   - guarded transitions and claim-first ordering: D2-D6, D5, H20
   - canonical `FinalMessage`: H1
   - branch propagation: H7
   - `LaunchId`: L6
   - single dispatcher: D17
6. **Refactor:** F1, L7, D18, R16, T1.

Steps 1-3 alone would have turned the live 55-mission cancellation into one blocked slice awaiting a decision. (Corrected per C4: the dependent slices stay blocked behind it; they do not proceed independently.)

## Points open to challenge

- **The L3 trade-off.** Re-running every verifier per round is a defensible correctness choice. My position is that the deterministic build/test gate (H6) plus Judge covers rework, and the advisory stages should run once on the final candidate. If the advisory stages have caught regressions introduced by rework in practice, keep them; that evidence would change the ranking.
- **"Heterogeneity is the cause."** The largest live cascade had no local model in it. Heterogeneity raises the trigger rate (H1, H2, R1-R4). The amplifiers (L1, L4, H3, R3) decide whether a trigger becomes a cascade. A fix plan aimed only at the local-model runtime would leave the 55-mission failure mode intact.
- **R2 and L6** are mechanisms confirmed in code but not reproduced live. Treat them as high-probability, not proven.


## Continuation: Codex review, 2026-10-21

**Position: substantial engineering risks are real, but the initial audit overstates several mechanisms and recommends some changes that weaken correctness. I do not endorse the report unchanged. Claude must respond before any consensus declaration.**

### Scope and evidence

I inspected the current source at `e7457432`, the same revision cited above. This was a targeted adversarial source review, not independent reproduction of all 68 findings. I did not re-query the live deployment, reproduce the reported failures, or run tests; the live counts and papercuts remain Claude's evidence. No Armada MCP tools were exposed in this session. Existing parser, runtime, routing, and service test suites exist, including final-artifact preference (`AgentLifecycleHandlerSuite.cs:762`) and conflicting-verdict rejection (`JudgeVerdictParserSuite.cs:76`); T1 should mean missing targeted fault scenarios, not absence of testing. Only this report was edited.

The original CONFIRMED label combines very different evidence: a source-code property, a reachable failure mechanism, and an observed incident. These must be separated before treating the table as a verified defect inventory. A reported papercut establishes that an agent reported a symptom; it does not independently establish its root cause.

### Challenges and replacement recommendations

The following table qualifies the original summary. Scores refer to the replacement action, not the original proposed shortcut; 5 is simplest/highest value, and total is their sum.

| Review | Original items | Code evidence and challenge | Replacement action | Simplicity | Value | Total |
|---|---|---|---|---|---|---|
| C1 | H1/L5, executive summary | `AgentLifecycleHandler.cs:419-453` prefers a final-message file over streamed output. Local launches register that file at 924; Harbor reattachment registers an artifact path at 842. `CodexRuntime.cs:148` supplies `--output-last-message`; `MuxRuntime.cs:168-174` reconstructs a missing artifact after successful completion. The unconditional claim that Codex/Mux hand off every turn is contradicted by this path. | Verify artifact production/delivery for each runtime and recovery route. Make transcript fallback explicit and observable; require a validated final artifact for structured control decisions. Preserve transcripts separately. | 3 | 5 | 8 |
| C2 | L4, H5 | `ArchitectPlanParser.cs:61-90` returns false with **no error** when there is no plan block. `MissionService.cs:2850-2863` fails only when `planError != null`, then invokes `ParseArchitectOutput`, including legacy markers and numbered summaries. Therefore a missing fence is not uniformly terminal; an invalid present block is. | Remove numbered-summary inference, decide explicitly whether to retain legacy markers, and add bounded repair for absent or invalid structured plans. Preserve contract validation. | 4 | 5 | 9 |
| C3 | H2/H3 repair proposal | `JudgeVerdictParser.cs:9-17,42-65` deliberately excludes fenced examples and rejects conflicting verdicts. Taking the last verdict and stripping backticks can turn quoted examples or ambiguous output into acceptance. Whitespace and casing are already tolerated. | Parse one final structured response; retain ambiguity rejection and quoted-content isolation. Repair once with precise diagnostics, then block/escalate. Do not globally strip formatting or select the last verdict. | 4 | 5 | 9 |
| C4 | L1 and final recommendation | Blocking failed descendants preserves recoverability, but does not remove real dependencies. On the reported eight-slice chain, seven other slices cannot simply proceed. Cancellation is a defensible fail-fast policy; inability to resume the graph is the stronger resilience concern. | Preserve blocked descendants and restart lineage. Allow independent slices only after dependency and workspace isolation checks. Make fail-fast versus recoverable-block policy explicit. | 2 | 5 | 7 |
| C5 | L1 slice landing | Passing a slice Judge does not establish that the slice is independently releasable. Early landing can expose incomplete migrations, incompatible API changes, or partial features. | Retain voyage-level atomic integration by default; support explicit independently releasable slices, or checkpoint accepted commits without landing to main. | 3 | 4 | 7 |
| C6 | R3/R10/R5 | `RuntimeFailureClassifier.cs:42-126` confirms gaps and 529/overload misclassification. But `runtime_error` includes arbitrary exceptions (`ApiAgentRuntime.cs:506`), and generic `inference_error` does not establish transience. HTTP 400/413 can be deterministic configuration/input failures. Retrying all of these can amplify faults. | Carry typed cause, retryability and scope from the adapter; retry only supported transient causes with bounded jitter and a shared endpoint budget. Resolve uncertain tool outcomes before replaying side effects. | 3 | 5 | 8 |
| C7 | H6 | `VerifyStageWorkAsync` is Mux/API-only and also excludes Judge/Architect/PM/Recorder. However `MissionService.cs:1276-1309,1746` has a separate runtime-independent, configurable Definition-of-Done landing gate. “Only local models are build-checked” is too broad. | Specify consistent pre-handoff verification for work-producing stages and retain final landing checks. Cache by commit, environment and command; distinguish baseline failure, regression, timeout and unavailable infrastructure. | 3 | 5 | 8 |
| C8 | D15/H8/H7 | `HarborRouter` returns no selection at capacity; the actual Admiral fallback is in `AgentLifecycleHandler.cs:277-364`, conditional on placement policy and branch affinity. RequireHarborForLaunch and a Harbor-owned branch can make it wait. `HarborDockManager.cs:479-498` does confirm unsafe default-branch fallback if the expected stage branch is absent. | Preserve the expected commit SHA across handoff and verify it before launch. Defer on capacity; validate host capabilities. Publish private refs or transfer commit bundles where pushing origin is unsuitable. | 3 | 5 | 8 |
| C9 | R1/R6 and Claude-only framing | Non-streaming API calls are confirmed; the 60-second proxy setting is reported in `docs/HYBRID_DEVELOPMENT_RESULTS.md:14,96-97,193`, not independently verified deployment configuration here. Streaming does not prevent a timeout during cold loading or prompt evaluation before the first event. A single runtime can also span multiple hosts. | Measure queue, model-load, first-token and total deadlines separately. Fix proxy/model deadlines and endpoint admission. Streaming helps only when the proxy deadline is idle-based and bytes actually arrive. | 3 | 4 | 7 |
| C10 | D12 | Model-name or parameter-count inference is not a reliable capability benchmark. Unknown-to-Economy can quietly route critical work to an unqualified model or starve it. | Use explicit operator capability declarations and measured per-persona qualification; treat unknown capability as unqualified for constrained work. Model size can suggest configuration, not certify it. | 3 | 4 | 7 |
| C11 | L3 | Deterministic build/tests cannot replace UX or adversarial verification. The proposed “final passing round” must rerun advisory stages before acceptance; if those stages modify code, Judge must review the resulting commit. | Retain FullPipeline semantics; optimize with parallel read-only review of an immutable candidate and invalidate results when inputs change. Reduce stages only through an explicit policy. | 3 | 4 | 7 |
| C12 | D5/D17/HB1/L8 | Serialization is useful locally but cannot make provider writes or crash recovery atomic. Moving a Harbor acknowledgement after processing still permits duplicate delivery after commit and before ack. | Use assignment leases/launch identity and guarded versioned transitions; persist idempotent completion/inbox records with the state changes, then ack. Transactional fan-out or a resumable durable saga must enforce uniqueness. | 2 | 5 | 7 |
| C13 | HB5 | `WebSocketHarborTransport.cs:66-80` decodes each receive chunk independently, which can split UTF-8 at **any** chunk boundary, even below 64 KB. The missing total-message limit is a separate concern. | Decode assembled bounded bytes once, or use a stateful UTF-8 decoder and enforce a total byte limit. | 5 | 3 | 8 |
| C14 | R2/L6 | Missing explicit context configuration and shared integer namespaces support plausible risks. Neither proves truncation, collision frequency, or “high probability.” Ollama defaults and endpoint settings require observation; PID namespaces require a collision test. | Keep these as unobserved mechanisms. Add context/token telemetry and a deterministic cross-host ID-collision test; adopt opaque per-attempt launch identity for attribution and deduplication. | 3 | 4 | 7 |

### Findings I concede

- **D1 is a concrete ownership bug:** `MissionService.cs:603` releases the captain before the claim at 629. Remove that release, but also guard the stale mission rollback; deleting one line does not fix the whole race.
- **D7 is a real isolation gap:** the pending dispatch loop has no per-mission exception boundary. Catch recoverable errors per mission and isolate health-loop phases, while propagating shutdown cancellation and exposing failures in telemetry.
- **HB3 is supported:** `AdmiralService.cs:2112-2118` resumes an interrupted active dock without the recovery-budget check used by other callers. `CaptainService.TryRecoverAsync` increments attempts but does not impose an entry ceiling. Bound this path and persist its budget across restarts.
- **H7's commit continuity problem and H8's capability omission warrant priority.** Missing expected work must never silently become a fresh branch from default.
- **R10 needs correction:** overload should not be conflated with exhausted billing quota. Endpoint-level backpressure is more appropriate than repeatedly quarantining individual captains.
- **The main resilience diagnosis is sound:** transport/runtime variation can expose dispatch and handoff assumptions, while graph policy determines the blast radius. A frontier-only incident is a useful counterexample to blaming local models alone. It does not prove the relative mixed-runtime failure rate.
- **L2 warrants a specification-conflict route**, but a model-authored papercut must request review, not silently waive an acceptance criterion. Repeated-case detection should consider evidence and changed inputs, rather than unconditionally stopping on a repeated filename/scenario.

### Revised order and requested Claude response

Prioritize ownership-safe assignment and completion, exact commit continuity, and per-mission isolation first. Then make structured-output repair bounded and fail-closed, enforce endpoint admission/backoff, and preserve recoverable dependency graphs. Extract services only as these invariants become explicit; splitting a large file alone does not repair lifecycle behavior.

Before endorsement, Claude should address C1-C14, especially the existing final-message artifact path, absent-versus-invalid Architect plans, the unsafe last-verdict proposal, and the claim that blocked chained slices could proceed independently. The original summary remains a useful investigation index, but its confidence labels, scores and recommendations need reconciliation with these qualifications.

**Debate status: Codex review complete; Claude response pending. No substantive consensus declared.**

## Continuation: Claude response, 2026-10-21

**Position: I accept C1 through C14. I re-checked C1, C2, C3, C7 and C13 against the source at `e7457432`, and each holds. I withdraw the last-verdict proposal and the claim that blocked slices could proceed. C1 is correct, and it also points the opposite way from what I originally claimed. That change sharpens the main output-contract recommendation rather than weakening it. I corrected the executive summary and rows L4, H6 and L5/H1 in place.**

### C1 corrected: Claude has no clean final message; Codex, Mux and API do

- `GetAndClearMissionOutput` (`AgentLifecycleHandler.cs:416-446`) returns the final-message artifact only if the file exists and is non-empty. Otherwise it returns the streamed buffer.
- That artifact is written by Codex (`--output-last-message`, `CodexRuntime.cs:146-149`), Mux (`MuxRuntime.cs:173`, only on success), API (`ApiAgentRuntime.cs:319,484`) and Remote/Harbor (`RemoteAgentRuntime.cs:444,552`). Claude, Cursor, Gemini and OpenCode take a `finalMessageFilePath` parameter and never use it.
- So a Claude mission's `AgentOutput` is its whole decoded transcript. Every line passes into the buffer *before* `ProgressParser` runs (`AgentLifecycleHandler.cs:1318-1324`), so progress-signal lines are included. The buffer is cut to its first and last parts with a marker in the middle (`BoundedTextBuffer.Trim`, 256 KB).
- That trimming and the included progress lines are consistent with the live papercuts reporting a cut-off excerpt and raw progress JSON. I can't prove from the papercuts alone that this was the root cause, as Codex notes.
- When an API inference call fails, the runtime writes an empty artifact (`ApiAgentRuntime.cs:309`), so the parsers silently fall back to the transcript.
- Revised claim: Claude-only works because Claude's discipline about markers survives transcript parsing, not because its output is clean. A local model with weaker marker discipline that falls back to the transcript is the fragile case.
- Revised action: every runtime produces a validated final-message artifact. For Claude, that is the `result` event, which stream-json mode already emits.
  - Control decisions (verdict, plan) read only that artifact.
  - A missing artifact should be a classified `InvalidOutput` with a repair turn, not a quiet fallback to the transcript.
  - The transcript stays stored for audit and is never parsed.
  - Progress-protocol lines should not be added to the handoff buffer.

### Remaining concessions and qualifications

- **C2:** Correct. `ArchitectPlanParser.TryParse` returns no error when there is no block (`ArchitectPlanParser.cs:61-90`), so `MissionService.cs:2853-2866` falls through to `ParseArchitectOutput`. H5 is the real risk on the absent-block path, and L4 applies only to a present but invalid block. Both get the same fix: one bounded repair turn, then fail closed.
- **C3:** Correct, and I withdraw "take the last verdict / strip backticks". The parser's ambiguity rejection is a safety property. The C1 fix also lowers how often ambiguity happens, because a final artifact contains one message rather than a full transcript of drafts.
- **C4/C5:** Conceded. Blocking keeps the graph recoverable but does not satisfy real dependencies. My live evidence shows the Architect *declared* a fully serial 8-slice chain. It does not show the slices were actually independent. Revised action: block rather than cancel, allow resuming from the failed node, and have plan validation warn when every slice depends on the one before. Landing stays atomic at the voyage level by default.
- **C6:** Conceded. Only 429/overload errors and declared-transient errors should be retried, with jitter and a shared budget per endpoint. Treating `runtime_error` and generic `inference_error` as retryable could repeat side effects.
- **C7:** Conceded, with one addition. The Definition-of-Done gate also calls `CancelDependentPipelineStagesAsync` (`MissionService.cs:1301`), so it is a second cascade trigger that acts the same for every runtime. I did not check whether the live vessels enable it.
- **C8-C14:** Conceded as written. The 60 s proxy, the unset Ollama context size (R2) and process-ID collisions (L6) remain mechanisms that are possible but not observed. The live-evidence column should label them that way.

### Reconciled priority (both reviews)

1. Make captain ownership safe: remove the D1 release and guard the stale mission rollback. Catch errors per mission and per phase in the dispatch loop (D7). Cap the Harbor resume loop with a persisted budget (HB3).
2. Carry the exact commit across handoffs: pass the expected SHA from stage to stage, and never fall back to a fresh branch off the default branch (H7). Check capabilities when choosing a Harbor (H8).
3. Make output contracts fail closed: a final artifact for every runtime, with control parsing reading only that artifact (C1). Allow one repair turn for verdicts and plans (C2, C3). Delete the inference that turns numbered lines into missions (H5).
4. Add per-endpoint admission control and typed retryability (C6, R4, R10).
5. Block dependents instead of cancelling them and allow resuming from the failed node (C4). Route specification conflicts to a human or the Architect for review (L2).
6. Use a lease and launch ID per attempt, and guarded state changes (C12, L6). Split up `MissionService` only as these rules become explicit.

**Debate status: Claude response complete. I have accepted all of Codex's challenges and proposed the reconciled position above. I am not declaring consensus until Codex confirms it agrees with this reconciled version, especially the corrected C1 direction.**


## Continuation: Codex acceptance, 2026-10-21

Claude has now participated and explicitly accepted C1–C14. I accept Claude's reconciled priority list with the qualifications already accepted in C1–C14. In particular, the corrected output-artifact direction matches the source: Claude missions accumulate decoded output, while Codex/Mux/API prefer a non-empty final artifact. This does not establish why Claude-only operation succeeds; marker discipline is a plausible explanation requiring comparative measurements. An empty artifact on a failed API call demonstrates fallback behavior, not proof that a failed call reaches successful pipeline handoff.

Repair means a bounded output-contract repair after checking attempt state, not blindly replaying tool side effects. A transport failure, incomplete artifact transfer, or uncertain execution outcome first requires reconciliation. Explicit terminal cancellation remains available; recoverable blocking is the recommended policy for failed prerequisites. HTTP status alone does not establish retryability, including quota-related 429 responses. These are applications of the agreed C6/C12 constraints, not additional objections.

WE HAVE CONSENSUS

Runtime heterogeneity exposes inconsistent output, placement, admission and lifecycle contracts; graph policy can amplify one failure across a voyage. The reported frontier-only cascade shows local models are not necessary for that failure mode, but does not establish comparative failure rates. Prioritize ownership-safe guarded assignment/completion and isolated dispatch/recovery; preserve and verify exact commits and host capabilities across handoffs; parse validated final artifacts with bounded, fail-closed repair; enforce typed retries and shared endpoint budgets; preserve recoverable dependency graphs and escalate specification conflicts without waiving acceptance criteria. Use durable attempt identities, leases and idempotent transitions, then extract services around those invariants. Preserve ambiguity rejection, real dependency ordering and atomic integration by default. Add targeted mixed-runtime fault tests and telemetry. Code mechanisms, attributed live observations and unobserved risks must remain distinct. Agreement covers this qualified engineering position and the C1–C14 corrections, not independent validation of every historical finding or score. Agreement is an agent declaration, not proof of truth.

## Reconciliation with the acceptance campaign log, 2026-10-21

Part 2 of this document is the acceptance campaign log (formerly `HANDOFF_FINDINGS.md`, merged here and deleted). The audit above drew its live evidence from that campaign's test cohorts without citing it. This section reconciles the two. Where this section and Part 1 conflict, this section governs.

### Context the audit lacked

- **The live failures were deliberate failure-path testing.** The cohorts exercised failure and handoff paths on purpose. Live counts establish that a mechanism exists, not how often it occurs in normal use.
- **Mission counts are post-cleanup.** Every finished cohort was archived and then deleted (missions and voyage read back 404). The audit's 151 missions and "55 of 61 cancellations" describe what survived cleanup. Part 2 records dependent cancellation at the end of nearly every failed cohort (37, 31, 43, 49 and 57 missions).
- **The cascading cohort's cause is unverified.** Part 2 stops at the 19:13 UTC checkpoint for `vyg_mvih4v59_FfCL4jcd358`. The audit's AC-INF-3 diagnosis (PolyPrompt 3.5.0 reporting `Completed` for partial tool arguments) has to be checked against HF-24, which records that 3.5.0 reports `State=Failed` for incomplete streams. PolyPrompt is owned by the same maintainer, so "unsatisfiable" holds only within a single slice.
- **Claude Code has its own failure modes.** HF-29 (native wakeup timers keeping a finished CLI alive) was fixed; it contradicts the framing that Claude simply hides the weak points.

### Deliberate design choices that qualify audit items

| Audit item | Deliberate choice recorded in Part 2 | Effect on the item |
|---|---|---|
| L1 (cancel the dependent DAG) | Bounded fail-fast is treated as correct: "The budget correctly halted work and cancelled 43 dependent missions; no rejected work was approved for landing." | Keep cancellation as a supported policy. Add recoverable blocking as an option, not a replacement (C4/C5). Lower priority. |
| L3 (full chain per rework) | "FullPipeline Judge rework repeats the original intervening verification stages", verified in HF-02. | Do not shorten the chain by default (C11). Optimize only through explicit policy. |
| D13 (pins keep missions Pending) | Strict assignments ("Wait for selected captain", `strictCaptain`) exist so tests control model identity. | Strict waiting is correct. D13 applies only where persona defaults are silently persisted as hard pins. |
| D9 (misconfigured API captain) | HF-30 deliberately fails the mission, releases the captain and stops the voyage instead of retrying forever. | Remaining defect: the broken captain is not excluded from later selection. |
| L4/H2/H3 (strict contracts) | HF-34: "retaining strict matching and no automatic plan normalization". | Repair turns with precise diagnostics are compatible; normalization is not (C3). |

### Cross-reference

| Audit item | Campaign item | Relationship |
|---|---|---|
| L6 | HF-33 | HF-33 deduplicates Harbor handles against other Harbor jobs. `_ProcessToMission`/`_ProcessToCaptain` remain `Dictionary<int, string>` and `ApiAgentRuntime._PidCounter` still restarts at 2,000,000,000; HF-33 lists the mixed local/remote allocator as unverified. L6 is HF-33's open remainder. |
| R2 | HF-06, HF-13 | Measured prompt growth from 35k to 193k tokens; "There is no context compaction in ApiAgentRuntime." Truncation itself remains unobserved. |
| R1, R6 | HF-24, HF-28, HF-07 | Non-streaming calls confirmed; Mux probe deadline separated (HF-28); per-phase deadline telemetry is HF-07. |
| L2 | HF-05, HF-25 | Same need: plan contradictions, and known blockers passing verification stages. Design one structured stage-outcome / spec-conflict route. |
| L4 | HF-34 | Diagnostics named and deduplicated since `d4752eeb`. |
| H1/L5 | HF-11 | Canonical final message for control decisions plus a stored diagnostic transcript for audit. |
| H7 | HF-17 | Guarded fast-forward ref updates and `refs/armada/stage-commits/<SHA>` exist; SHA propagation across hosts builds on them. |
| R13, HB4 | HF-31, HF-36 | Stop paths changed after the audit's reading; re-check before changing. |
| H6 | HF-14 | Validation exit status can be masked by shell composition; the deterministic gate must use argument-vector execution. |
| (not in audit) | HF-23, HF-22, HF-32, HF-35, HF-37 | Revision-aware vessel context, Harbor headless start, Harbor job and RPC ownership, tenant-scoped stop-all. |

## Implementation status

Goal: handoff and dispatch must hold up regardless of model or runtime configuration. Each item is marked when implemented, with its test coverage. Items not listed here are still open.

| Item | Status | Change | Tests |
|---|---|---|---|
| D1, D2, D6 | Done | `TryAssignAsync` now claims the captain atomically before provisioning, moves the mission Pending to Assigned by compare-and-set, and runs provisioning, instruction writing and launch inside one rollback boundary (`RollbackAssignmentAsync`, `FailAssignmentAsync`). The six copied rollback blocks are gone. Rollback runs without cancellation so a shutdown cannot strand a claimed captain. | `Services.HandoffResilience`: provisioning failure, launch failure |
| D5 (captain side) | Done | New guarded provider methods in all four databases: `TryReleaseFromMissionAsync`, `TrySetDockAsync`, `TrySetProcessAsync` (each applies only while the captain still holds the given mission; release never lifts a quarantine). `TryClaimAsync` accepts a null dock for claim-first. | `Database.Captain`: claim without dock, guarded release, quarantine kept |
| D4 | Done | Launch recording on both the assignment and the recovery relaunch path (`CaptainService.TryRecoverAsync`) uses compare-and-set, so a millisecond agent's exit outcome and released captain are never overwritten. | `Services.HandoffResilience`: fast exit during launch |
| D7 | Done | Every health-check phase runs through `RunHealthPhaseAsync`; dispatch and orphan recovery isolate each mission. Shutdown cancellation still propagates. The orphan revert to Pending is now compare-and-set on the observed status. | `Services.HandoffResilience`: throwing phase does not stop dispatch |
| R3, R10, R16 (partial) | Done | Stream, timeout, 408 and overload (529, `overloaded_error`) are `Transient`; 413 and context-length 400s are `TaskFailure`; a generic `inference_error`/`llm_error` uses its HTTP status, and without one is `Transient` (bounded by `MaxRecoveryAttempts` and escalation). Arbitrary `runtime_error` stays `Crash` (C6). `RuntimeFailureClassifier.KnownErrorTypes` lists every emitted type. Deliberate deviation from C6: a status-less inference failure is retried because the alternative is a terminal voyage halt plus crash-loop quarantine, and the retry is bounded. | `Services.RuntimeFailureClassifier`: every known type has an explicit expected kind |
| HB3 | Verified, no change | `CaptainService.TryRecoverAsync` already counts each relaunch in the persisted `RedispatchAttempts`, and every caller checks `InterruptedRedispatchLimit`. An added increment double-counted and was reverted. | Existing `Startup counts relaunch` case |
| H5 | Done | Numbered and markdown narration is never inferred into missions. Legacy `[ARMADA:MISSION]` markers remain. The dead summary parser was removed. | `Services.PipelineDispatch`: narration gets repair, not fan-out; fails closed when repair is exhausted |
| L4, H2, H3 | Done | Bounded output-contract repair turn (`TryScheduleOutputRepairAsync`): an invalid or absent Architect plan, a missing or conflicting verdict, an unsubstantiated PASS, or a block without an affected case re-runs the same stage with the exact problem and contract quoted. The count is a typed column (`missions.output_repair_attempts`, migration 88 on all four providers; `ArmadaSettings.MaxOutputRepairAttempts`, default 1, range 0-3). The note replaces any earlier note. Review-gated stages are never repaired. Verdict ambiguity rejection is unchanged (C3). | `Services.HandoffResilience`, `Services.MultiRuntimeFlow`, `Services.PipelineDispatch` |
| H15 | Done | The no-op and boundary-violation completion paths release the captain; every completion release is compare-and-set (`ReleaseCaptainAfterCompletionAsync`). | Covered by completion suites |
| H16 | Done | A launch that finds its Harbor dock missing goes through `FailAssignmentAsync`, which cancels dependents and settles the voyage. | `Services.HandoffResilience`: missing Harbor dock settles dependents and voyage |
| H7 (C8) | Done | Every finished stage records the exact commit it produced (`Mission.CommitHash`, replaced by the landed commit at landing). Assignment refuses a later stage whose new dock does not contain the previous stage's commit (`CheckStageCommitContinuityAsync`), with a reason naming both commits and the remediation, instead of silently starting from the default branch. Dock placement now has Admiral-side branch affinity (`DockPlacementRequest.BranchOnAdmiral`): a branch built on the Admiral keeps its next stage there unless Require Harbor for launch is on. Recording the commit also makes the existing rework stagnation check (same rejected commit) effective. | `Services.HandoffResilience`: refused without upstream commit, accepted with descendant HEAD, commit recorded; `Services.HarborDockLifecycle`: Admiral-built branch stays on the Admiral |
| H8 | Done | Dock affinity no longer pins a launch to a Harbor that is disabled or does not advertise the captain's runtime or required capabilities; the reason says what to install. Placement then waits with that reason instead of launching into six failures. | `Services.HarborRouter`: incapable and disabled owners refused |
| C1 (H1/L5) | Done for Claude | Claude Code missions now write the terminal `result` event's text to the launch's final-message file on a clean exit, so verdicts, plans and handoff excerpts come from the final answer rather than the trimmed transcript, matching Codex, Mux and API. Gemini, Cursor and OpenCode still fall back to the transcript. Making a missing artifact a classified `InvalidOutput` is open. | `Runtimes.MissionStreaming`: Claude mission writes the final-message file |
| R4 | Done | `AgentSettings.MaxConcurrent` is enforced per runtime, and the new `InferenceOptions.MaxConcurrentMissions` (1-64, stored in the existing endpoint options JSON, no migration) caps concurrent missions per endpoint base URL across every captain that uses it. Disabled endpoints are excluded from selection. A transient resume now waits `TransientResumeDelaySeconds` (default 5, doubling per attempt with jitter, capped at 60 s). The wait is safe because the captain keeps holding the mission and its old process stays marked as handled; after the wait the resume re-reads both and stands down if the mission was cancelled, stopped or reassigned. A restart during the wait falls back to the bounded interrupted-exit path. | `Services.HandoffResilience`: endpoint cap shared across captains, runtime cap, resume waits and stands down when cancelled |
| D8 | Done | A launch failure records the captain; the retry prefers any other idle captain and does not wait out the backoff when one exists. Strict assignments keep their only captain. | `Services.HandoffResilience`: broken captain not re-picked |
| D9 | Done | A deterministic launch configuration failure quarantines the captain for `CaptainQuarantineMinutes`, so the next mission waits instead of failing the same way. | `Services.HandoffResilience`, updated HF-30 case in `Services.PipelineDispatch` |
| HB1 (C12) | Done | A Harbor job message's sequence is acknowledged only after its handler finishes (`ApplyMessageAsync`, then `AcceptJobSequence`). A handler that throws leaves it unacknowledged and ends the link, so the Harbor replays it on reconnect rather than dropping it from its outbox. Delivery is at-least-once; handlers are expected to be idempotent on replay. | `Services.HarborJobOutbox`: failed handler leaves the sequence unacknowledged |
| HB5 (C13) | Done | `WebSocketHarborTransport` assembles each message's bytes and decodes once, with a 128 MB ceiling that closes the link with MessageTooBig. | `Services.HandoffResilience`: split multi-byte characters decode intact |
| R5 | Done | `ApiAgentRuntime` retries an inference call up to `InferenceRetries` (default 2) times with jittered exponential delay for no status, 408, 429, 5xx, 529 and transport exceptions, keeping the conversation. 4xx rejections are not retried. Retries appear as `[retry]` diagnostics. | `Runtimes.ApiAgentRuntime`: transient 503 retried and completes; 400 not retried |
| R8 | Done | Mux exit 0 is reported as 1 when `run_completed` reported a failed status or a fatal error arrived. Exit 0 with no structured outcome (older mux, text mode) stays 0. | `Runtimes.MuxRunOutcome`: failed run overrides exit 0 |
| R9 | Done | Per-process provider errors keep the more significant error (`RuntimeFailureClassifier.KeepMoreSignificant`): non-fatal tool errors, then unclassified, transient, task failure, then captain-unavailable. | `Services.RuntimeFailureClassifier` |
| R14 | Done | Captain heartbeat writes are throttled per captain on the same 15 s interval as mission heartbeats. | Existing lifecycle suites |
| R15 | Done | Mux's rebuilt final turn keeps at most `MaxTurnTextChars` (1M characters), dropping the oldest text. | Existing Mux suites |
| H13 | Done | Autonomous rescue opens only for standalone work-producing missions. Review, planning and recording stages, and any pipeline stage (one with an upstream or a dependent), are left to their voyage's own rework and repair. | `Services.MissionRecoveryCoordinator`: pipeline and review stages not rescued |
| T1 | Done | `StubCaptainRuntime` gains a per-launch fault script (`StubCaptainBehavior.OnMission`, `StubMissionScript`): exit code, millisecond exit, provider error, transcript lines, final message, commit or not. `Services.RuntimeFaultMatrix` runs real missions through the real lifecycle handler, Admiral, mission service, docks and git for a millisecond exit, a lost stream, overload, an unclassified crash, and a Judge whose format drifts. Running it exposed the voyage-settlement race below. | `Services.RuntimeFaultMatrix` (stable over repeated runs) |
| Voyage settlement race (new, found by T1) | Done | Completion writes WorkProduced before deciding what follows (handoff, rework, repair, landing). A voyage check in that window saw every mission done, closed the voyage, and the follow-up stage was then cancelled. `MissionService.IsCompletionInFlight` now marks such missions active in both settlement functions (`VoyageService.CheckCompletionsAsync`, `MissionService.UpdateVoyageTerminalStatusAsync`); dangling-handoff recovery also skips them, so a handoff never runs twice. | `Services.HandoffResilience`: voyage stays open while a completion is in flight |
| H11 | Done (gates) | Dangling-handoff recovery re-checks the Judge gate (a substantiated PASS) before handing a Judge's work on, and skips completions still in flight. Architect fan-out idempotency is open. | `Services.HandoffResilience`: recovery keeps the Judge gate |
| H17 | Done | Halting a voyage of independent missions recalls the captain of each running sibling through the owner-aware stop path; the Cancelled status is kept. | `Services.HandoffResilience`: halted voyage stops running siblings |
| HB2 | Done | A reconnect restarts the vanished-job grace for that Harbor's jobs, so the replayed outbox can deliver an exit before a job is declared lost. | `Services.HarborJobOutbox`: reconnect restarts the grace |
| HB6 | Done | A handshake that replaces a Harbor's link fails the requests still pending on the old link at once. | `Services.HarborJobOutbox`: superseded link fails pending requests |
| D11 | Rejected after testing | Least-recently-used selection spreads contributor stages across captains and leaves no independent captain for the Judge (which excludes the Worker and every intervening contributor), so a two-captain pipeline stalls. Name order is kept; a better fix must account for Judge independence. | Existing `Services.PipelineDispatch` fan-out case caught it |
| Migration 88 parity | Done | `run-db-parity-tests.sh` found the PostgreSQL UPDATE had a duplicated parameter; fixed. SQLite, MySQL and SQL Server passed. | Database parity script |
| L2 | Done (verdict route) | New Judge verdict `[ARMADA:VERDICT] SPEC_CONFLICT` with a required `## Spec Conflict` section quoting the criterion and evidence. It never spends the rework budget and never waives anything: the stage goes to review, where a person approves the work as is, re-runs the Judge, or stops the pipeline. A SPEC_CONFLICT without the quoted section is a contract violation and gets a repair turn. The contract text is in `JudgeContract.OutputContract()` (generated per mission, so no template migration). Repeated-affected-case stagnation detection is open. | `Services.HandoffResilience`: conflict goes to a person; unquoted conflict is repaired |
| L1 | Mostly covered by existing code | `RestartAsync` already reopens the voyage and restores every stage cancelled because of the failed one, so a cancelled chain is recoverable by restarting the failed stage. A distinct Blocked status (visibility before restart) is open. | Existing `restart_later_stage_keeps_branch` case |
| HB4 | Done | The Harbor client runs job stops off its receive loop (tracked with other in-flight commands), so a stop that waits for its process no longer holds up heartbeats and requests. | `Services.HarborJobOutbox`: slow stop does not block the link |
| R13 | Done | `BaseAgentRuntime.StopAsync` sends SIGTERM on Unix (the old stdin close never applied to a process obtained by id), waits asynchronously with the caller's token, and kills the process tree only if it is still running at the deadline. | `Services.HandoffResilience`: stop signals gracefully, then forces |
| R12 | Done | The local process exit handler is async (output drain no longer blocks a thread-pool thread), with an outer catch so a fault cannot escape an async event handler. The lifecycle handler's wait for a late PID mapping runs on a background task with `Task.Delay` instead of blocking the exit thread with `Thread.Sleep`. | Existing lifecycle, streaming and fault-matrix suites (repeated runs) |
| Leftover-commit scope loop (new, found live) | Done | Found on the live deployment with a Mux/Qwen Worker and a Codex Judge on Timestamps: the model committed only README.md, its build regenerated tracked `src/Timestamps/Timestamps.xml`, and `CommitLeftoverWorkAsync` committed that under Armada's name. The Judge correctly rejected the out-of-scope file, the model reverted it, rebuilt, and Armada re-committed it, exhausting both rework rounds on Armada's own commit. Armada now commits leftovers only for a captain that made no commit this attempt (HEAD still at the dock's start commit). | `Services.HandoffResilience`: leftover changes kept off after the captain committed; still rescued for a captain that never commits |
| H6 (C7) | Done | The pre-handoff build/test check runs for every runtime (`ArmadaSettings.VerifyAllStageRuntimes`, default true), skipping a frontier stage that committed nothing. A failure is re-run on the stage's base commit in a throwaway detached worktree, and a check that fails there too is a pre-existing failure that does not block (cached per vessel, base commit and command). Tracked files the check's own build rewrites are restored afterwards, so the gate never adds out-of-scope changes. | `Services.HandoffResilience`: regenerated file restored, regression blocks, pre-existing failure ignored, no-commit stage skipped |
| D3 | Done | A failed captain's next state is decided before it is released and written once: quarantine-worthy failures (usage limit, auth, crash loop) go straight from Working to Quarantined, model-unavailable straight to Stalled, so dispatch never sees it Idle in between. | `Services.HandoffResilience`: quota failure quarantines without an Idle gap |
| D15 | Done | When every connected Harbor that can run the work is at capacity, placement waits for a slot (`HarborRoutingDecision.CapableHarborsAtCapacity`) instead of creating the dock on the Admiral, where the runtime may be missing. | `Services.HarborRouter`: capacity flag set only for capable Harbors |
| R11 | Done | Per-endpoint circuit breaker (`EndpointBreakerState`, keyed by base URL across every captain that uses it): three consecutive endpoint-level failures (transient, model unavailable) stop new missions going to that endpoint for two minutes; a clean run closes it. | `Services.HandoffResilience`: breaker holds missions while a shared server is down |
| H14 | Done | No-op re-dispatches have their own persisted counter (`missions.noop_redispatch_attempts`, migration 89), so resumes and interruptions no longer use up the no-op budget, and no-ops no longer use up the interruption budget. | `Services.HandoffResilience`: no-op budget not spent by earlier resumes |
| R2 | Partial | `InferenceOptions.ContextLength` (512 to 2,097,152) is sent as Ollama `num_ctx` on Ollama and Ollama Cloud clients. When the reported prompt reaches 95% of that window, an API captain stops with `context_limit_exceeded` (TaskFailure) instead of acting on truncated history. It is editable in the dashboard and TUI endpoint forms; the TUI form also now keeps `MaxConcurrentMissions`, which an edit there used to wipe. History compaction is not done. | `Runtimes.ApiAgentRuntime`: window-full typed failure and control; factory sets num_ctx on Ollama clients only |
| Placement visibility (new, found live) | Done | Live R2 retest: a context-window endpoint correctly refused the old Mac Harbor and, with `requireHarborForLaunch` on, waited. But the blocker said "AwaitingDispatch: a captain is free", the log said "no idle captain matches", and the router blamed `runtime ApiEndpoint`, which the Harbor has. The blocker now reports `PlacementWaiting` with the placement reason, the pending log uses it, and the router names the missing capability with an update hint. The dangling-handoff refusal warning, which repeated every 12 s for six historical Judge missions, is now logged once per mission. | `Services.MissionAssignmentBlocker`: placement reason surfaced and cleared; `Services.HarborRouter`: missing capability named |
| Rescue cascade (new, found live) | Done | A test mission's landing was refused by a dirty operator checkout. Recovery classed that as a LandingConflict and dispatched two rescues, which failed the same way. Purging the original then deleted its incident (incidents are events) and set the rescues' `parent_mission_id` to NULL. Each orphaned rescue then looked like a new failure, and two "[Rescue] [Rescue]" missions were created in one pass. Now rescues carry `missions.is_rescue` (migration 90, all four providers) and are never rescue-eligible, and dirty-checkout and push-blocker landing refusals are recorded as `Infra`. | `Services.MissionRecoveryCoordinator`: orphaned rescue not re-rescued; environment landing failure not rescued. `Database.Mission`: flag round-trips |
| R1, R6 | Done, live verified | `InferenceOptions.Streaming` streams tool chat and plain chat through PolyPrompt 3.5.0. A watchdog turns the endpoint's request timeout into an idle deadline (reset on every chunk), and the client timeout becomes a one-hour outer bound, so a generation that keeps sending is never cut by the request timeout or an idle-based proxy. `InferenceOptions.FirstResponseTimeoutMs` (1,000 to 1,800,000) is the deadline for the first chunk (cold model load, long prompt evaluation); without streaming it extends each call's timeout. A stall throws a `TimeoutException` naming the phase, which the in-loop retry covers and which surfaces as transient `llm_timeout`. A stream that ends without the provider's completion signal is a retryable failure, and its partial tool calls never run. Either setting requires the new Harbor capability `api-inference-streaming-v1`. Dashboard and TUI forms carry both. The .NET client also stopped dropping a nondefault `ContextLength` or `MaxConcurrentMissions` set through the options getter (`HasNonDefaultSettings`). Live, through Conductor at view.homedns.org:8900 (endpoint `TimeoutMs` 60 s, which bounds time to response headers, not the body): the same 5,200-token generation failed with 502 at 60.2 s blocking and completed at 90.6 s streamed through `ApiAgentRuntime`; streamed tool calls (write_file, read_file) worked against real Ollama. Cold loads (R6): Ollama sends no bytes while loading, so the first-response deadline only governs Armada's own wait. llama3.3:70b (43 GB) loaded in 41.5 s and gpt-oss:120b (65 GB) in 38.4 s, both under the proxy limit; qwen3.5:122b (81 GB) was cut at 60.1 s, and Ollama abandoned the load, so every retry restarts it and fails. Armada cannot fix that from the client; a 502 or 504 after at least `GatewayDeadlineHint` (10 s) now names the proxy deadline and the change to make. On 2026-10-22 streaming was turned on for the live API endpoints (glm-5.3, deepseek-v4.1-flash, gpt-oss:20b); test fixtures were left as they were. | `Runtimes.ApiAgentRuntime`: slow_gateway_error_names_the_proxy_deadline, streaming_outlasts_request_timeout, streaming_idle_stall_fails_typed, first_response_deadline_covers_cold_load, incomplete_stream_never_runs_tools, streamed_tool_calls_execute, streaming_plain_chat_and_blocking_deadline, streaming_options_validated; `Services.HarborInteractiveLaunch`: endpoint_streaming_requires_capable_harbor |
| R7 | Done (text-mode signals) | Gemini, Cursor and OpenCode run in text mode with no structured error channel, so every non-zero exit was `Crash`. `RuntimeProviderErrorParser.TryParseCliStderrLine` reads their stderr (never stdout narration) for explicit signals only: an HTTP 4xx/5xx written as a status (`status 429`, `status code: 503`, `HTTP 401`, `HTTP/1.1 502`, `429 Too Many Requests`; never a bare number), a Google RPC status name, or a network error code (`ECONNREFUSED`, `fetch failed`, and so on). Each runtime overrides `TryParseProviderError`, and `RemoteAgentRuntime` applies the same parser to Harbor jobs. Formats come from the CLIs' documented error shapes, not captured output, because none of the three is installed on the test hosts; outcome tracking (a trusted exit 0) remains unaddressed for them. | `Services.RuntimeProviderErrorParser`: cli_stderr_statuses_classified, cli_stderr_ordinary_lines_ignored, cli_runtimes_report_stderr_provider_errors (real processes for all three runtimes) |
| D13 | Done (persona defaults) | Of the three pin sources, operator overrides and escalation targets were already intended to persist (strict assignments wait by design; escalation waits for its chosen alternative). The defect was the persona default: `ResolvePreferredCaptainAsync` wrote `Persona.DefaultCaptainId` into `RequestedCaptainId` and copied that captain's tier into `Mission.Tier`, so a busy Premium default left a Premium floor that excluded idle Standard captains. The default is no longer persisted. `FindAvailableCaptainAsync` and `IsAnyCaptainEligibleAsync` prefer it each tick when it is idle (a nonempty priority hierarchy still supersedes it), and otherwise route normally. Missions persisted with a default pin before this change keep it. | `Services.CaptainRouting`: persona_default_applies_at_assignment (no pin, no tier), busy_persona_default_falls_back_without_tier_floor |
| D10 | Done (endpoint health) | Selection already skipped disabled endpoints and open circuit breakers. It now also skips a captain whose endpoint is `Unhealthy` as of a health check within the last 15 minutes. Older results are ignored, because probes may be off and a stale result says nothing about now. Runtime availability on the host is still decided at launch (launch-failure backoff, D8, and quarantine, D9). | `Services.HandoffResilience`: recently_unhealthy_endpoint_is_skipped |
| Unfinished Worker (new, found live) | Done | Live campaign voyage C (Mux qwen3.8 Worker, Codex Judge, Timestamps): the Worker exited 0 after three minutes while still reading files, with no commit and no `[ARMADA:RESULT] COMPLETE` line, and was recorded WorkProduced at the base commit. The Codex Judge caught it, but only by spending a review round. The no-op detector required a fast exit and trivial output. `NoOpCompletionDetector.IsUnfinishedWork` now also rejects a Worker stage that ends on its dock's start commit with no diff and no completion line, however long it ran, through the same bounded re-dispatch (`NoOpRedispatchAttempts`). Review and planning stages are exempt, and an unknown start or produced commit never rejects. | `Services.HandoffResilience`: worker_that_stops_mid_task_is_redispatched; Worker stubs in ReviewGate and PipelineDispatch now end with the completion line, as real Worker prompts require |
| Strict escalation (new, found live) | Done | In the same voyage, the rework Worker hit four Conductor 502s (Conductor was being redeployed), used its resumes, and `TryEscalateToPlannerAsync` handed it to the DeepSeek captain despite the voyage's strict Worker = Mux assignment. Escalation now returns false for a strict assignment (`IMissionService.HasStrictCaptainAssignmentAsync`), so the mission fails with its typed cause instead of finishing on a different model. | `Services.AdmiralService`: strict_worker_is_not_escalated_to_another_captain |
| H9 | Done | Normal handoff gives every dependent the completed stage's branch, which is right for a linear chain but put two dependent slices (both built on one earlier slice) on one branch at once: git refuses a second worktree, so the second launch failed and backed off. `TryAssignAsync` now defers a mission while another Assigned or InProgress mission on the vessel holds its branch, and the assignment blocker reports `BranchInUse` with the holder. The slices then stack on the voyage branch in order, which matches the voyage's atomic integration. A finished (WorkProduced) holder does not block, so ordinary stage handoff is unaffected. | `Services.MissionAssignmentBlocker`: branch_in_use_waits |
| F2 | Done (voyage terminal status) | `VoyageService.CheckCompletionsAsync` and `MissionService.UpdateVoyageTerminalStatusAsync` now both call `VoyageSettlement.DecideAsync` (in-flight completions, merge-queue entries, Failed versus Complete) and `VoyageSettlement.ApplyAsync`, so the background check and the completion path cannot drift. The third rule, the final-integration slice's barrier, is H10's `IntegrationBarrier`, which reuses the same in-flight and merge-queue checks. | Existing voyage settlement, merge-queue and in-flight completion cases in `Services` (2,152 passing) |
| Outage ride-through (new, found live) | Done | Campaign round 2, voyage D: Conductor returned Watson's HTML 500 page for every request from 17:27:16 (it was being redeployed). The Mux Worker had run eight and a half minutes; its three resumes came 5, 12 and 20 s later, so it failed 38 s into the outage, labelled `Crash` (which is auto-rescued). Transient failures now keep resuming with the capped backoff while the run of transient failures is younger than `ArmadaSettings.TransientOutageGraceMinutes` (default 10, 0 restores the MaxRecoveryAttempts limit); the resume note is added once per distinct failure; and a mission that still fails from a transient cause is `Infra`, which the recovery coordinator leaves to a person. The strict-assignment fix held in the same voyage: the Worker was not escalated to another model. | `Services.AdmiralService`: transient_outage_is_ridden_out_past_max_recovery; handle_process_exit_async_resumes_in_place_on_transient_model_error (grace 0, asserts Infra) |
| D16 (API stop) | Done | `ApiAgentRuntime.StopAsync` cancelled the run and returned at once, so a relaunch into the same dock could start while the old loop was still inside a tool call. Each run's loop task is tracked, and `StopAsync` waits for it (bounded at 15 s and by the caller's token) after cancelling. | `Runtimes.ApiAgentRuntime`: stop_cancels_running_loop (asserts the exit has happened when StopAsync returns) |
| F3 | Done | The ten remaining literal persona comparisons (`"Worker"`, `"Judge"`, `"Architect"` in MissionService, AdmiralService and MissionPromptBuilder) use `PersonaCatalog.Matches`, which is case-insensitive and resolves aliases, like the rest of the code. | `Services`, `Runtimes` (2,340 passing) |
| L2 (stagnation) | Done | Rework already escalated early when a Worker produced the same rejected commit. A Worker that changes code every round but never reaches the defect was not caught. `JudgeContract.AffectedCaseKey` normalizes the first concrete affected case (lowercased, whitespace collapsed, file line numbers dropped), and `TryScheduleJudgeReworkAsync` treats a rejection with the same key as the previous round's Judge as stagnant: it escalates now (or, under a strict assignment, stops with the reason) instead of spending another full round. | `Services.JudgeContract`: affected_case_key_ignores_line_moves; `Services.MultiRuntimeFlow`: repeated_affected_case_escalates_early |
| HF-22 Harbor headless (found live) | Done | Restarting the Mac Harbor after a deploy failed in Avalonia's render timer (-6661) because the display was asleep, leaving split mode without its host until the display was woken. The link loop moved from `MainWindow` into a UI-free `HarborLinkHost`, which the window now drives through callbacks; `Program.Main` falls back to `HarborHeadlessRunner` when the UI fails before it has started, and `--headless` runs it on purpose. A UI failure after startup still surfaces. Verified live: `--headless` connected and served docks; with the display put to sleep, a normal start reproduced -6661, fell back, and connected; the GUI start afterwards was unaffected. | Live verification; `Services` (2,155 passing). No suite covers the Harbor app project |
| D12 | Done (zero-config) | `Captain.Tier` already let an operator declare a tier, so no new field was added and nothing needs configuring. `CaptainTierSelector.ClassifyModel` now classes a parameter count of 8B or less (`:7b`, `-3b`, `phi3:3.8b`) as Economy. A Claude Code, Codex or Gemini captain with no model and no tier runs the vendor's default flagship, so the new `CaptainTierSelector.CanHandle` lets it meet any tier floor (mission tier, `minimumTier`, escalation) while it still ranks as Standard; before this, Premium missions stayed Pending when the only strong captains were CLI captains on their defaults. C10's "unknown is unqualified" was not adopted: it would shut those captains out of tiered work. An unknown model stays Standard. API captains still classify from the captain's own model, not the endpoint's (open). | `Services.CaptainTier`: small parameter counts, vendor default handles any tier, ranking unchanged, other runtimes not Premium, explicit tier wins (2,160 passing) |
| H10 | Done (A3 + B2 + C2 + D1, chosen 2026-10-22) | The barrier moved from `MissionService.ShouldDeferArchitectSequencedMissionAsync` into `IntegrationBarrier`, used by dispatch, the assignment blocker and the failure cascade. It groups the voyage into slices (a non-rework Worker plus its stages and reworks). (A3) It waits while another slice's mission is running, while a WorkProduced mission's completion is in flight, or while one has an unsettled merge-queue entry, reusing `VoyageSettlement`'s checks; WorkProduced with no queue entry is settled, so manual landing and `None` cannot deadlock. (B2) After provisioning, `IntegrateSiblingSlicesAsync` merges each settled sibling commit into the integration branch through the new `IGitService.MergeCommitAsync` (fetches the sibling branch from origin when the commit is missing; aborts a conflicting merge so the dock is never left mid-merge); conflicts are listed for the Worker with their files, and a commit that cannot be reached refuses the stage. (C2) A Failed or LandingFailed mission in another slice cancels the integration and its stages as `DependencyFailed` at dispatch and from `CancelDependentPipelineStagesAsync`; `RestartAsync` on the named mission restores it with its branch. (D1) An operator-cancelled slice is left out and listed as dropped. Live on 2026-10-22 (image `b7ee0cc6`, Timestamps, landing `None`): a Claude Architect planned two parallel README slices and an integration slice, with DeepSeek Workers and Codex Judges. Each slice ran on its own branch. The integration Worker started only after both slice Judges finished, and its branch held two Armada merge commits containing both slice commits (verified with `git merge-base --is-ancestor`). Its Judge reviewed the combined README, and the voyage completed in 349 s. | `Services.IntegrationBarrier` (7 cases); `Services.GitService`: merge_commit_merges_skips_contained_and_aborts_conflicts; `Services.MultiRuntimeFlow`: failed_slice_cancels_integration_and_restart_restores_it, integration_merges_sibling_work_before_launch, integration_refused_when_sibling_commit_missing (2,171 passing) |
| D16 (stall threshold) | Done (liveness instead of per-runtime thresholds) | Stall detection marks a captain stalled after `StallThresholdMinutes` (10) without an output line. An API captain wrote lines only between model turns, so a single generation longer than the threshold on a slow open model was treated as a stall even while tokens streamed. Rather than a per-runtime threshold (which would also delay catching real hangs), `ApiAgentRuntime` writes `[inference] waiting for the model (N s so far)` every `LivenessInterval` (60 s) while a call is in flight. A call that is genuinely hung is still ended by its own deadlines (request timeout; with streaming the first-response and idle deadlines), so the stall backstop still applies to everything else. CLI runtimes already write events throughout a turn. | `Runtimes.ApiAgentRuntime`: long_model_call_reports_liveness (188 passing) |
| D14 | Done (honest result) | Validation and Ask chat still run on the Admiral host. For an Admiral-local address (`localhost`, a loopback IP, `*.localhost`) the validation result now carries `Note`: a Harbor-hosted mission calls that address on the Harbor's computer, which the check did not reach. Shown in the dashboard and TUI validation dialogs. Remote addresses (Conductor, Ollama Cloud) get no note. Running the check on the Harbor itself would need a new Harbor protocol request; see the queue. | `Services.ModelEndpointService`: validation_notes_admiral_local_addresses |
| D12 remainder | Not adopted | Classifying API captains from the endpoint's model name would put `deepseek-v4.1-flash` in Economy (the `flash` family pattern), dropping a strong open coding model out of every Standard or Premium mission on the live fleet. Name patterns are not a capability benchmark (C10). API captains therefore stay Standard unless the operator sets `tier`, which already overrides everything. | n/a |
| TUI parity cases | Done | `parity.json` regenerated: `duplicateModelEndpoint` (already implemented in the TUI client) added, and `settings.permissions.timeoutDecision` implemented in the TUI Server Settings screen ("Default answer on timeout"). The export-count assertion moved from 334 to 335. | `Tui` (593 passing) |
| R2 (compaction) | Done | New `ApiContextCompactor`: with `ContextLength` set, before each model call the larger of the provider's last prompt count and a local estimate (about 4 characters per token, so Ollama's missing cached-token count cannot hide growth) is compared with 80% of the window; above it, every tool output except the six most recent and shorter than 600 characters is replaced with a one-line note naming the tool and size. Every tool call keeps its answer, so provider tool-call pairing stays valid. When a reply reports a prompt at the 95% tripwire but still below the window, the history is compacted and the reply is used; the run stops with `context_limit_exceeded` only when nothing is left to compact or the window was already exceeded. No extra model call. | `Runtimes.ApiAgentRuntime`: context_compactor_replaces_oldest_tool_output, context_pressure_compacts_and_continues, context_window_full_fails_typed (190 passing) |
| H11 (Architect fan-out) | Done | `RecoverDanglingHandoffsAsync` skipped Architects because re-driving their fan-out was not idempotent, so a restart between the plan and the last fan-out write hung the voyage (template Worker never prepared) or ran it with slices missing. The fan-out now ends by appending `<!-- armada:fanout-complete -->` to the Architect's description (cleared when the Architect is restarted). `TryRecoverArchitectFanOutAsync` handles an unmarked WorkProduced Architect in a running voyage: if the template Worker was never prepared it runs the normal fan-out; otherwise it reconciles against the parsed plan, finding each slice by its unique `<title> [Worker]`, creating a missing slice through the shared `CreateArchitectSliceAsync`, cloning a missing stage chain, then applying dependencies. `ApplyArchitectMissionDependenciesAsync` now rewires only slices still Pending on the Architect, so recovery never clears a started slice's branch. Old voyages with no marker reconcile to a no-op and are then marked. | `Services.PipelineDispatch`: interrupted_architect_fanout_is_finished_without_duplicates (2,173 passing) |
| C1 remainder (L5/H1) | Done (text-mode reply) | Gemini (`--approval-mode`, stdin prompt), Cursor (`-p --output-format text`) and OpenCode (`run`, no `--format json`) all run in plain-text mode: stdout is the agent's reply, stderr its tool progress. New `CliTextFinalMessage` collects stdout (bounded at 512 KB, keeping the end) and, on exit 0, writes it to the final-message file through `MuxRuntime.WriteFinalMessageIfMissing`, so a file the CLI wrote is kept and a failed run writes none. The handoff, verdict and plan readers already prefer the final-message artifact. A missing or empty reply is not a separate classification: the existing contract checks catch it (no verdict gets a Judge repair turn; a Worker with no reply and no commit is an unfinished Worker). JSON event modes were not adopted because none of the three CLIs is installed here to verify their event schema. | `Services.RuntimeProviderErrorParser`: text_cli_reply_becomes_final_message (2,364 passing in Services and Runtimes) |
| HF-35 Admiral restart with Harbor missions (found live) | Done | Round 4 restarted the Admiral container mid-voyage. A DeepSeek Worker on the Harbor had exited 0 six seconds earlier. `RestoreHarborMissionsAsync` reattached it and replayed the recorded exit immediately, before the Harbor's link was back, so `VerifyStageWorkAsync` threw "Harbor is not connected", which was reported to the captain as a check to fix; the resume's relaunch was refused (Harbor offline) and, by design, waited for the next stall check, 10 minutes. Fixes: `ReplayRecordedExitAsync` replays a recorded exit once the Harbor is connected (bounded by `RecordedExitReplayWait`, 10 min); `VerifyStageWorkAsync` waits up to `HarborReconnectWait` (3 min) for the dock's Harbor; a relaunch refused with `HarborLaunchUnavailableException` is remembered and `RetryRelaunchesAwaitingHarborAsync` (a new health phase) runs it as soon as that Harbor reconnects. The mission still finished in the live run, after the 10-minute gap. Re-tested live in round 5: the Admiral restarted while a Claude Test Engineer and a Mux Worker ran on the Harbor; both kept their processes, were reattached with no redispatch, and finished. The exact finished-just-before-restart timing was not reproduced. | `Services.HarborDockAffinityLaunch`: refused_recovery_relaunches_as_soon_as_the_harbor_reconnects; `Services.AgentLifecycleHandler`: restore_harbor_mission_durable_exit_*; live |
| HF-36 Harbor killed mid-mission (found live) | Done | Round 5 killed the Mac Harbor (`kill -9`) while a Mux Worker and a DeepSeek API Worker ran on it. The API loop ended with the Harbor; the `mux` process did not, and after the Harbor reconnected the Admiral (correctly, from what it could see) reported the job interrupted and relaunched it into the same dock, so two `mux` processes worked one worktree until the orphan was stopped by hand. New `HarborProcessLedger` (`running-agents.json` in the Harbor settings folder): `LocalHarborJobRunner` records each job's operating-system process (id and start time) and removes it on exit; `HarborLinkHost` stops every recorded process still running, whose start time matches, before the first connect. In-process API loops (synthetic ids) are never recorded. The DeepSeek Worker recovered with one redispatch and produced its work. Verified live after the fix: a qwen3.8 Mux Worker was running (pid recorded in `running-agents.json`), the Harbor was killed with `kill -9`, the `mux` process survived it, and the restarted Harbor stopped it before connecting; the Admiral relaunched the mission once, at most one `mux` process existed throughout, the voyage completed, and the ledger was empty afterwards. | `Runtimes.HarborProcessLedger`: orphan_from_earlier_run_is_stopped, reused_process_id_is_left_alone; live |
| L1 (Blocked status) | Done (opt-in, chosen 2026-10-23) | New `MissionStatusEnum.Blocked` (stored as text, so no migration) and `ArmadaSettings.BlockDependentsOfFailedStages` (default false). When on, `CancelDependentPipelineStagesAsync`, `CancelIntegrationForFailedSliceAsync` and the pipeline branch of `HaltVoyageAsync` mark waiting stages Blocked (`DependencyFailed`, reason naming the failed mission, no completion time) instead of Cancelled. Blocked counts as active in `VoyageSettlement` and the halt check, so the voyage stays open; `RestartAsync` restores Blocked stages like cancelled ones; every voyage cancel path (REST, MCP, WebSocket, fleet actions) cancels Blocked missions; `MissionStateMachine` allows Pending to Blocked and Blocked to Pending or Cancelled. Dashboard and TUI: status type, badge, description, list and recent-mission filters, transition list, notification severity. HF-25 (structured blocking outcomes for non-Judge verifiers) stays queued. | `Services.PipelineDispatch`: failed_stage_blocks_dependents_when_configured; `Services.MultiRuntimeFlow`: failed_slice_blocks_integration_when_configured |
| HF-25 | Done | New `VerifierContract`: Test Engineer, Linter and Usability Engineer prompts (`MissionPromptBuilder`) gain an instruction to end with `[ARMADA:STAGE-OUTCOME] BLOCKED` and an `## Affected Case` (the Judge's format) when they find a product defect their role may not fix. Completion routes a substantiated block through the generalized `TryScheduleReworkAsync` (previously Judge-only): a rework Worker on the same branch, the intervening stages repeated, a new verifier of the same persona, and the downstream stages re-pointed to it; stagnation compares the same persona's previous affected case. An unsubstantiated block gets an output repair turn; a review-gated verifier, or one whose rework is unavailable, fails as `JudgeRejected` with the reason. `VerifyStageWorkAsync` skips a blocked verifier (new non-consuming `PeekMissionOutput`), so a failing test that demonstrates the defect does not resume the verifier to fix it. `ApiAgentRuntime.IsCompleteResponse` accepts the blocked line. Verified live in round 8b (image `8cd3c2d9`): a documentation-only Worker (DeepSeek) documented a post-Dispose contract the code did not honor; the Test Engineer (Claude Opus), limited to test files, reported `[ARMADA:STAGE-OUTCOME] BLOCKED` with an exact affected case (`Timestamp.cs` Messages getter, AddMessage and Dispose; ArgumentNullException where an empty dictionary was required). Armada sent the work back to the Worker, which fixed the product code and added regression tests; a new Test Engineer verified it; the Judge (Codex), which had waited for the new Test Engineer, passed it; the voyage completed in 7 minutes. | `Services.MultiRuntimeFlow`: blocked_verifier_sends_work_back_before_the_judge, blocked_verifier_without_affected_case_is_repaired; live |
| L8 | Done | New `IMissionMethods.CreateChainAsync(created, repointed)` in SQLite, MySQL, PostgreSQL and SQL Server: inserts the missions and applies the dependency moves in one transaction (the INSERT moved into a shared `InsertMissionAsync(conn, tx, ...)`, which `CreateAsync` also uses). `TryScheduleReworkAsync` builds the rework, its repeated stages and the new reviewer in memory (IDs are assigned at construction) and writes them with the re-pointed later stages in one call. Before this, an interruption between the writes could leave a Recorder or landing stage depending on the rejected review, where dangling-handoff recovery could prepare it. | `Database.Mission`: mission_create_chain_is_atomic (rollback on a failing insert) |
| HF-37 Stopped stage strands its pipeline (found live) | Done | Round 6 stopped a DeepSeek Worker's captain mid-run (`POST /api/v1/captains/{id}/stop`). `CaptainService.RecallAsync` fails the mission (`OperatorAction`) but, having no pipeline knowledge, does not cascade, so the Judge stayed Pending with `DependencyNotFinished` ("waiting for ... (Failed) to finish") and the voyage stayed open indefinitely; earlier stop tests used a Worker-only voyage and could not show it. New `IMissionService.SettleStagesBehindFailedDependenciesAsync`, run as the health phase "stages behind failed dependencies": every Pending stage whose dependency ended Failed, LandingFailed or Cancelled has the failure cascaded through `CancelDependentPipelineStagesAsync` (Cancelled, or Blocked when configured) and the voyage settlement re-checked. This covers any path that fails or cancels a stage without cascading. Verified live (image `e9625967`, `BlockDependentsOfFailedStages` on): the stranded Judge became Blocked naming the stopped Worker on the first health check, the voyage stayed open, restarting the Worker returned the Judge to Pending, and the voyage completed; the setting was turned back off afterwards. | `Services.MultiRuntimeFlow`: stopped_stage_settles_the_stages_after_it (both settings); live |
| D5 (mission side), H20 | Done (cancellation guard and documented internal table) | Of about 70 full-row mission writes, the consequential race is a long completion (the stage check alone runs up to 15 minutes) overwriting an operator's cancellation with WorkProduced, after which handoff or landing proceeded. Completion's first write was already a claimed exit plus compare-and-set; its later writes were not. Every provider's plain `UpdateAsync` now adds `AND (status <> 'Cancelled' OR @status IN ('Cancelled','Pending'))` and reflects Cancelled back to the caller when the write is refused, so the rest of that completion sees the real state; restart (to Pending) is unaffected. The full suite (3,485) passed unchanged, confirming no code relied on leaving Cancelled any other way. H20: the internal transitions Armada performs outside `MissionStateMachine` (rollback, recovery, escalation, repair and re-dispatch, gate failures, review, blocking, restart) are tabled in docs/PIPELINES.md. Converting the remaining full-row writes to explicit compare-and-set calls is not planned: the guard covers the cancellation race, and the other writers already run under a claimed exit. | `Database.Mission`: cancelled_mission_is_not_revived_by_a_stale_update |
| L6 (HF-33 remainder) | Done (non-overlapping handle ranges) | `HarborConnectionManager.RegisterLaunchedJob` gives every newly started Harbor job an Admiral handle from 1,000,000,000 upward, never the Harbor's own process id, so the Admiral's `_ProcessToCaptain`/`_ProcessToMission` maps cannot confuse a Harbor job with an Admiral-local OS process (Linux PIDs are bounded at 4,194,304, macOS at 99,999) or an API in-process id (from 2,000,000,000; the Harbor numbers its own API captains the same way). A known job keeps its handle on a replayed start, and restored jobs keep their persisted handle through `RestoreJob`. `ApiAgentRuntime`'s in-process counter now starts at 2,000,000,000 plus the epoch seconds modulo 100,000,000, so a restart does not reissue an id a stale record holds. A fully opaque launch identity through the Harbor protocol was not needed once the ranges cannot overlap. | `Runtimes.RemoteAgentRuntime`: launched_job_handles_never_overlap_local_ids, restored_native_pid_collision_gets_unique_handles; delegated launch and foreign-Harbor cases updated |
| D17, D18 | Done (dominant read removed); D18 not changed | The costly read in the dispatch tick was `EnumerateByVesselAsync` per pending mission in both `TryAssignAsync` and the assignment blocker: the vessel's entire mission history, with `agent_output` and `diff_snapshot`, to find the few Assigned or InProgress ones. New `ActiveMissionsOnVesselAsync` reads the two active statuses and filters by vessel. Serializing the four dispatch callers was not done: claim-first assignment (`TryClaimAsync`) and compare-and-set status writes already make concurrent attempts safe, and a global lock would slow every completion. D18 (eligibility in three places: assignment, the blocker report, escalation) was reviewed and left as is: the three differ on purpose (the blocker explains, escalation excludes the failed captain and requires an alternative), and the shared rules (tier through `CaptainTierSelector.CanHandle`, persona priorities through `PersonaCaptainPrioritySelector`) are already single functions. | `Services`, `Database`, `Runtimes`, `Tui` (3,486 passing) |
| F1 | Done (partial-class split, no behavior change) | `MissionService` (5,905 lines) is now a partial class across eleven files by concern: `MissionService.cs` (members, construction, assignment and the blocker report, 2,012 lines), `.Completion` (completion gates and the completion core), `.Review` (review approve/deny and restart), `.Instructions` (instruction generation, playbooks, templates), `.Handoff` (handoff, recovery, cascades, voyage settlement), `.Architect` and `.ArchitectPlan` (fan-out, recovery, plan parsing), `.IntegrationBarrier`, `.Rework`, `.CaptainRouting`, `.StageChecks` and `.StageCommands`. Methods moved verbatim in contiguous blocks; the class, its fields and its interface are unchanged, so no caller or test changed. Separate services with their own interfaces were not created: the methods share mutable state (in-flight completions, placement waits, the logger and database) and splitting them would change construction for every caller. | `Services`, `Database`, `Runtimes`, `Tui` (3,486 passing, unchanged) |
| Round 7 FullPipeline (live) | Verified | Wilson, `FullPipeline`, image `67277ac2`, one model per role: Claude Code Product Manager, Codex Architect, DeepSeek Workers, GLM 5.3 Usability Engineer and Recorder, Claude Opus Test Engineer, qwen3.8 (Mux) Linter, Codex Judge. The Architect planned two slices; all 14 stages ran with no redispatch and no output repair, both Judges returned a substantiated PASS, and the voyage completed in 44 minutes. No verifier reported BLOCKED (none found a defect outside its role). The integration slice found slice 1's work already on its branch; its merge list also named the Product Manager's base commit, so planning stages are now excluded from sibling commits. | Live; `Services.IntegrationBarrier` fixture now includes a Product Manager |
| Round 8 concurrency (live) | Verified | Six voyages dispatched at once across Timestamps and Wilson (both one mission at a time) with shared strict captains: Mux, DeepSeek, GLM 5.3, Claude Code, Codex and the acceptance Claude Opus and Codex captains in Worker, Test Engineer and Judge roles, including GLM judging a Codex Worker. All six completed with no redispatch or repair; missions queued correctly on the vessel locks and on captains another voyage held. A seeded wrong implementation note was ignored by the qwen3.8 Worker, which implemented the stated objective, so its Test Engineer correctly reported no production defect. |
| Database providers | Verified | The `Database` suites (519 cases) ran against real PostgreSQL 16, MySQL 8.4 and SQL Server 2022 containers: 509 passed and 10 skipped on each (the skips are SQLite-driver-specific cases), including migration 91, `CreateChainAsync` with its rollback case, and the Cancelled-mission update guard. A first attempt silently ran on SQLite because the provider name was mistyped; `TestDatabaseConfig` now rejects an unknown `ARMADA_TEST_DB_TYPE` instead of falling back. | `Database` on four providers |
| L7 | Done (rework state) | `Mission.ReworkRound` and `Mission.ReworkEscalated` (migration 91 on SQLite, MySQL, PostgreSQL and SQL Server) are set when a Judge rework is created; `IntegrationBarrier.IsRework` and `IsEscalatedRework` read them, falling back to the title for missions stored before the migration. The rework budget walk, escalation checks and the integration barrier use them. Handoff markers in descriptions (`ARMADA:ARCHITECT-HANDOFF`, `## Escalated to`, the fan-out-complete marker) stay: they are part of the persisted prompt text and are written and read only by Armada. | `Database.Mission`: rework fields round-trip; `Services.IntegrationBarrier`: rework_identity_survives_a_retitle |

### Live verification on the deployment, 2026-10-22

Deployed `de09aae4` and then `f2c52983` (`jchristn77/armada-server:v1.1.0`, digests `fb247527…` and `267de59a…`) through DocMon with the bind-preserving compose helper (mounts verified identical, both services healthy); the local Harbor was restarted on the matching Release build. A full backup and the remote refs of Timestamps and Wilson were captured first. Every test voyage used the Timestamps vessel with landing mode None.

| Voyage | Captains | Outcome |
|---|---|---|
| WorkerOnly | Claude Code | Complete in 60 s; produced commit recorded; handoff output was Claude's final answer (no progress JSON); captain released |
| Reviewed | Claude Worker, Codex Judge | Complete; the Judge started from the Worker's exact commit; verdict `PASS` with all three lenses read from Codex's final message |
| WorkerOnly | Mux qwen3.8:27b (local) | Complete in 135 s; correct change |
| WorkerOnly | DeepSeek (API endpoint) | Complete in 30 s |
| Reviewed | Mux Worker, Codex Judge | **Failed on the old build**: Armada's leftover-work commit kept re-adding a build-regenerated file, so both rework rounds were spent on Armada's own commit (fixed in `f2c52983`). **Rerun on the fix: Complete first time, one README-only commit.** |
| WilsonArchitectReviewed | Claude Architect, Mux Workers, Codex Judges | Complete across three slices. The Architect's first plan broke the contract (no final integration slice); the output repair turn quoted the error and the corrected plan fanned out. Previously this failed the voyage outright. |
| WorkerOnly, then captain stop | Mux | Stop returned in 0.3 s and the Mux process exited within about 1 s (SIGTERM); captain Idle; mission Failed (OperatorAction); voyage settled Failed on the next health tick |

Finding, not changed: the built-in mission prompt tells captains to commit and push, so captains publish their mission branches to the vessel's remote even when landing mode is None. Test branches were deleted afterwards.

### Open items queue

Every item still open, with its planned resolution, in the order it will be worked. Status: **Queued** (will be done in this order), **Awaiting decision** (needs the owner's approval), **External** (outside Armada), **Kept by policy** (deliberately not changed).

| Order | Item | What is left | Planned resolution | Status |
|---|---|---|---|---|
| 1 | D14 remainder | Endpoint validation still runs on the Admiral; for an Admiral-local address its result says it did not reach the Harbor. Ask chat is not affected: `CaptainChatService` already routes turns through `CaptainLaunchRouter`, as missions are. | Validate through the launch router as a one-turn probe ("reply with pong") on the Harbor that would run the endpoint's missions. Low priority: only `localhost`-style endpoints are affected, and none are configured on the live fleet. | Queued |
| - | R6 (qwen3.5:122b load) | Conductor VMRs are at 300 s and streaming is now on for the live API endpoints, but Ollama's own `OLLAMA_LOAD_TIMEOUT` (5 minutes) expires before the 81 GB model loads. | Raise `OLLAMA_LOAD_TIMEOUT` on the GB10 host or use a smaller model. Recheck that Conductor's patched 504 message reaches Armada's error text once Conductor is redeployed. | External |
| - | L3 | Each Judge rework round re-runs the whole verification chain. | Not shortened by default (C11); only an explicit policy may shorten it. | Kept by policy |
| - | D11 | Name-order captain selection. | Least-recently-used was rejected after testing (it starves the Judge of an independent captain); revisit only together with Judge independence. | Kept by policy |

# Part 2: Acceptance campaign log

> Merged from `HANDOFF_FINDINGS.md` on 2026-10-21. Content is unchanged apart from heading levels. Items are tracked as HF-xx; the reconciliation section in Part 1 maps them to audit findings.

### Current acceptance state

Campaign resumed on 2026-10-21. Live REST status at 17:12:46 UTC confirms seven idle captains, no working/stalled captains and no active voyages. No campaign work ran during the user-requested pause. Armada MCP tools are unavailable in this session; monitoring uses REST.

The version 3 frontier cohort failed at 16:05:12 UTC: 22 WorkProduced, one Failed and 49 Cancelled missions. The final independent Judge reproduced an unregistered `Unauthorization: abc` value being changed, violating preservation outside credential intervals despite 53 passing cases and a clean build. Earlier rounds found Token/Digest leaks, cross-CRLF matching and query over-redaction. These are implementation/verification defects, not established SDK defects.

All recorded work was archived on remote Wilson branch `armada/acceptance/archive-frontier-v3-polyprompt350-20261021` at `63cf889fe65c23e8a59d7c3c2e062f9a229adc7f`; the voyage and all 72 missions were deleted and read back 404 before pause. Prior frontier work remains archived at `32aad1b088e2a26c06f6aef0082027fae419008e`. Private evidence roots are `/tmp/armada-frontier-v3-retest-20261021` and `/tmp/armada-frontier-retest-20261021`. Do not monitor deleted cohorts.

Current full-pipeline cohort `vyg_mvih4v59_FfCL4jcd358` started at **18:57:14 UTC** on deployed source `e0753d8a`. All eight strict captain assignments, pinned Claude Worker `claude-opus-5-5`, Codex Judge `gpt-6.1-sol`, and ApproveAll/600s/Deny settings are retained. The objective is unchanged apart from the recorded marker-collision and unmatched-fragment preservation regressions and upstream-ID retention clarification. This is informed regression testing. Private 25-second REST collector/evidence: `/tmp/armada-frontier-v6-retest-20261021`. No acceptance pass is claimed; audit the emitted plan's satisfiability and upstream traceability before interpreting schema acceptance.

Checkpoint at 19:13 UTC: PM and Architect are WorkProduced; the first Worker `msn_mvih4x25_RREcyOApw03` is active on the exact requested captain. The Architect emitted ten slices, 47 decisions and 47 acceptance criteria. All 32 functional IDs (PRV/INF/HLT/SEC/DSH/API) appear in criterion references, with no uncovered declared ID. Seven UC identifiers are omitted as identifiers even though their behavior appears represented by the functional requirements; upstream semantic traceability is not certified. D32 explicitly gives secrecy precedence over preservation when replacement/deletion creates a registered secret. Track that as a contract exception requiring independent review against the original objective and actual behavior, not an acceptance pass. No operator plan modification/cancellation, runtime redispatch or captain substitution is recorded. Evidence: `architect-plan.json` and `architect-review.json` in the current private evidence root. The collector remains active.

Immediately before dispatch: dedicated local/remote acceptance ref clean at `8c3e8b5` (PolyPrompt 3.1.0), production main unchanged `c9ff616f`, context cleared/read back, 18 vessel memories preserved. NuGet still reports latest stable 3.5.0. Fresh owner-Harbor model validations passed in 2.75s and 5.79s. Source e0753d8a passed CI 39655560554. All rollout/native-stop evidence is below.

The v5 cohort `vyg_mviernqb_EnkauRAhR2g` was **operator-cancelled for independently proven plan contradictions**, not a runtime failure or acceptance pass. D28's required fixed marker exposes a complete registered credential `REDACTED`; its four-character-fragment prohibition also conflicts with preservation of unmatched input such as `abcd public` when only `abcdefgh` is registered. These were planning constraints introduced by the model, not original user requirements. The schema gate cannot establish satisfiability. All checks also referenced a single example-like `REQ-1`, so detailed upstream traceability remains unproven.

Cancellation stopped only this voyage's pending/assigned stages; its active Test Engineer was allowed to finish. Final counts: five WorkProduced, 57 Cancelled, no runtime redispatches/substitutions. No Judge accepted the first package slice and the redactor never started. All three distinct recorded commits and local source branches were preserved on remote `armada/acceptance/archive-frontier-v5-plan-conflict-20261021` at `dbb15ad111e2fd2a5022aa032e7ff307f33af355`. All 62 missions plus voyage subsequently read back 404. Production main `c9ff616f` and acceptance baseline `8c3e8b5` remain unchanged. Evidence: `/tmp/armada-frontier-v5-retest-20261021`, including `plan-semantic-counterexamples.json`, cancellation, terminal/archive and deletion proofs. No root Wilson implementation edits were made. No acceptance jobs remained at that cancellation checkpoint; the current v6 run is described above.

Next clean run must follow completion/validation/deployment of the stop-route and RPC owner fixes. Preserve the objective and all strict assignments; record new marker-collision/unmatched-fragment regressions as informed test conditions. Generated instructions now clarify that example IDs must not replace the upstream set and require counterexamples for interacting invariants. This is guidance, not a completed semantic validation or structured blocker protocol.

The dedicated local/remote ref was again reset/verified clean at `8c3e8b5` with an observed lease, production main `c9ff616f` preserved, context cleared/read back and 18 vessel memories preserved. Current image-source CI passed: https://github.com/jchristn/Armada/actions/runs/39646766320 . No Worker or full acceptance pass is claimed yet.

Fresh cohort `vyg_mvie4xff_SFw4LAZp71R` **failed at the Architect contract gate** at 17:41:55 UTC, before any Worker launched. Counts: one WorkProduced (PM), one Failed (Architect), six Cancelled; two launches, zero runtime redispatches/substitutions. Architect emitted 11 slices but declared `requirements` as `ID: description` strings and used bare IDs in `requirementIds`. The exact-match gate correctly rejected the mismatch. No Wilson implementation was produced. Full evidence is retained under `/tmp/armada-frontier-v4-retest-20261021`; work/evidence was preserved before owned cohort cleanup. Do not replay the same prompt unchanged.

HF-34: structural plan-ID usability failure. Current generated contract says `requirements:[stable objective IDs]` without a concrete exact-match example. The model supplied descriptive values while references used identifiers. Existing diagnostics repeat “unknown objective requirement ID” for every occurrence without naming the offending ID. Improve the generated instructions and diagnostic specificity/deduplication while retaining strict matching and no automatic plan normalization. This is a planning/output-contract failure, not a Worker, SDK, runtime or lifecycle failure. Root did not repair Wilson or edit the rejected plan into acceptance.

Clean local/remote acceptance baseline `8c3e8b5` (PolyPrompt 3.1.0), archived work `63cf889f` and unchanged production main `c9ff616f` were verified at 17:32:35. Acceptance push used the observed lease; only the dedicated ref was reset. ProjectContext/ModelContext were cleared and read back. Eighteen vessel memories remain preserved; objective prohibits reusing old implementation or tenant memory, so this is not an erased-memory/blind benchmark. NuGet still lists 3.5.0 latest stable. The saved objective is retained with explicit new Token/Digest/header/CRLF/query/word-boundary, status-URL and malformed-JSON secrecy regression expectations; exact changes and payload are archived.

Reviewed behavioral fixes `2adfcfbb` and documentation-only correction `f027b8dc` are pushed to main and deployed. Final behavioral checks: net10 2,247/2,247, affected net8 521/521. Fresh full Release Rebuild with warnings as errors: zero warnings/errors. Both services are healthy on final published image digests; all mounts/environment/ports/networks/restart policies preserved; 27 template contents verified (Worker/Judge migrated, others preserved), Harbor connected/enabled. No acceptance voyage was active during this rollout; no in-flight continuity claim is made. Details and limits follow.

### Continuous cross-model and workload campaign

User explicitly requested continued hardening on 2026-10-21, with autonomous build/deploy through DocMon. Keep this campaign active; one passing voyage is not completion. Current full pipeline remains Product Manager -> Architect -> Worker -> Usability -> Test -> Linter -> independent Judge -> Recorder, with strict assignments. Every fresh run archives evidence/work branches, deletes only its associated acceptance missions/voyage, resets local/remote acceptance branch to `8c3e8b5`, and clears stale project/model context before dispatch. Preserve production Wilson main/images and unrelated workloads. Record any reused memory, changed criteria, intervention or runtime/configuration change; never present an informed regression run as blind validation.

| Planned cohort | Worker | Independent Judge / contributors | Evidence gate |
| --- | --- | --- | --- |
| Hosted API baseline | Existing DeepSeek v4.1 Flash / OllamaCloud | Codex Judge; Claude planning/verification | Pending clean run after lifecycle rollout; independently validate every slice and rework |
| Local CLI model | Existing Mux qwen3.8:27b | Codex Judge; Claude planning/verification | Preflight actual endpoint/model/tool capability, then clean full-pipeline run; no fallback/substitution |
| Frontier Claude | Explicitly validated/pinned Claude model | Codex Judge; separate verification sessions | Version 6 active on deployed e0753d8a; PM/Architect complete, first Worker active. Prior failures/interventions are recorded above; no acceptance pass yet |
| Frontier Codex | Explicitly discovered/validated Codex model | Claude Judge; code contributors use a different captain/role assignment | Record exact model and keep review independent of implementation contributors |
| Second hosted API | Existing GLM 5.3 / OllamaCloud | Codex Judge; Claude planning/verification | Validate availability/contract, then clean run; unsupported/unavailable is not a pass |
| Local native API | Tool-capable local Ollama model, if available | Codex Judge; Claude planning/verification | Validate context/tool support separately from Mux; do not assume identical behavior |

Coverage must include the full Wilson upgrade objective and smaller held-out repository tasks, plus failure/limit/permission/stream fixtures and multi-user/tenant resource isolation. Confirm independent work artifacts and real HTTP/CLI execution rather than model self-reports. Target repeated successful runs across critical frontier/hosted/local combinations without infrastructure retries, hidden captain substitution, manual Wilson fixes, false completion or ownership/branch regressions. Track first-pass acceptance and automatic review rework separately; bounded review rejection is different from runtime failure. Repeat affected cohorts after fixes, then exercise previously unseen cases/workloads to avoid claiming generality from overfitting one objective. Test deployment/reconnect continuity and live MCP session recovery independently. List remaining unverified platform/device/provider limits explicitly; no finite matrix proves all possible users/models/workloads.

### Open work and acceptance gates

| ID | Item | Status / next evidence |
| --- | --- | --- |
| HF-04 | Reduce missed failure-path coverage and contradictory design decisions | Measure first-pass acceptance/reworks; require observable evidence, not passing counts alone |
| HF-05 | Semantic plan validation | Structural validation cannot establish objective completeness, resolve contradictions, or prove tests ran; further validation remains open |
| HF-06 | Worker context/tool exposure | Bounded default reads implemented, tested and deployed (appendix). Large-catalog schema deferral deployed and tested in a real local harness (appendix); full API-cohort quality impact and accumulated tool/process output remain open |
| HF-07 | In-flight phase observability | Provider request/tool/validation phases, wall-clock deadlines and attempts remain insufficiently exposed |
| HF-08 | Wilson configuration/SDK contract boundaries | Recheck cloud health-auth silent rewriting and nested C# health enablement parity on the fresh implementation; do not copy prior product changes |
| HF-09 | Independent final workflow verification | Repeat local HTTP/redaction/persistence/validation and C#/Python/JavaScript SDK checks after the full pipeline passes; hosted provider inference remains separately unverified |
| HF-10 | Mobile native coverage limits | Android phone/tablet flows passed before strict-selection UI; iOS native-device testing remains unavailable on this host |
| HF-11 | Complete CLI interaction diagnostics through API | New: mission and captain APIs expose abbreviated tool commands and omit tool-result payloads; retain an access-controlled, appropriately redacted diagnostic transcript alongside readable logs |
| HF-12 | Dependency connectivity-probe documentation | Package routes now independently verified; external README omission remains. Current verification details are in the completed appendix |
| HF-13 | Slice breadth/context efficiency | Optional scoped decision references are deployed; verify new plan uses them and measure rendered context. Semantic scope review remains open |
| HF-14 | Validation command status masking | Wrapper reproduction confirms exit 0 despite prior failure; guidance deployed; arbitrary shell exit-status enforcement remains open. No hidden product failure established |
| HF-15 | Focused Wilson test selection | Low-priority Test Engineer papercut: Test.Automated has no case filter; quantify cost before adding unrelated tooling to this acceptance task |
| HF-18 | Universal validation rules need boundary combinations | Three metadata reviews; five findings corrected but shifted-overlap redaction still failed; budget exhausted |
| HF-20 | Staggered credential-span overlaps | Prior DeepSeek helper accepted after one rework; frontier helper accepted after one rework; consumer boundaries/final integration remain unverified |
| HF-22 | Harbor native startup with unavailable display | Closed 2026-10-22: Harbor falls back to a headless link when the desktop UI fails to start, and `--headless` starts it on purpose. Reproduced live with `pmset displaysleepnow`: the GUI start hit -6661, fell back, and connected within a second. |
| HF-23 | Revision-aware vessel context | Recorder context survived Git reset with obsolete package/test facts; cleared before new Architect dispatch; automatic invalidation remains open |
| HF-25 | Known blocking defects pass through verification stages as COMPLETE | Test Engineer and Linter reported nested-Bearer leak, then Judge independently requested rework; need a structured blocking-outcome/earlier routing contract |
| HF-29 | Native Claude future timers outlive completed missions | Real noop timer reproduced a successful terminal result with a still-running CLI; bounded-mission guard and owned recovery diagnostics implemented/tested, 2,222 regression checks passed; deployed and verified; fresh no-retry run pending |
| HF-31 | Stop acknowledgement and recovery overlap | Implemented/tested/deployed actual owner-exit wait, durable intent, retained tracking and deferred replacement; overlapping waiters, replay and recall regressions pass. Expanded live fault matrix remains pending. No live duplicate-execution claim |
| HF-32 | Harbor lifecycle ownership | Isolated transport reproduction permitted another Harbor to mutate a known job; local ownership checks cover active/durable lookup before callbacks/metrics. Final isolated regression validation and image deployment verified; ordinary RPC isolation needs separate review |
| HF-33 | Cross-Harbor process identity collisions | Same native/synthetic PID can overwrite global mappings; unique-handle and restored-assignment persistence fix reviewed, regression-tested and deployed; mixed local/remote allocator and wider live boundaries remain unverified |
| HF-37 | Tenant-admin REST stop-all escapes tenant scope | Real loopback credential test reproduced stopping another tenant captain; local filter now scopes captains and planning/refinement sessions. Validated with real loopback credentials/foreign resource rows and deployed in e0753d8a; no production stop-all was invoked |
| HF-36 | REST stop bypass and false stop-all success | Isolated real HTTP tests reproduce local PID-runtime construction before owner recall, and all_stopped despite an unconfirmed recall. Correction validated (including real loopback HTTP) and deployed in e0753d8a; positive native owner-stop probe passed |
| HF-35 | Pending Harbor RPC reply ownership | Eight isolated cross-user/tenant cases reproduced forged Git/dock/file/deferred replies by known RequestId; Owner guard passed broad/affected checks and was deployed with e0753d8a. The v5 diagnostic cohort stayed on d4752eeb |
| HF-34 | Plan requirement identity mismatch | Fresh frontier plan rejected before Workers: requirements contained ID plus prose, criterion references bare IDs. Tighten generated shape guidance and actionable diagnostics; preserve strict matching and independently retest |
| HF-26 | DocMon native Compose path mapping | Native action still cannot access discovered Compose paths. Data-preserving helper pull/recreate was successfully exercised and is in the appendix; no DocMon product fix claimed |

### Resumed lifecycle review — 2026-10-21

The paused worktree survived intact at `cc9ccfe9`. Independent review reproduced three additional defects in that pending revision, all with failing-before/passing-after isolated tests:

- Stop intent existed only in `HarborMetricsRecorder` memory until exit. The lost-stop/restart probe failed before delivery because the durable row did not yet carry StopRequested. Persist intent before transport send (within the stop deadline), preserving an open EndedUtc until actual exit. A new recorder then classifies the late exit as Stopped.
- Overlapping stop callers shared an exit completion, but the first timeout removed it, orphaning the second caller. Keep the job completion until its actual owned exit; caller timeout/cancellation does not revoke another caller's wait.
- Reattached StopRequested jobs did not pass their intentional-stop state into their runtime. A late exit after restart invoked the normal crash/completion callback unless another stop call was in progress. Restore the intent and suppress that callback for intentionally stopped jobs while cleaning routing/liveness.

A further boundary regression checks that a lost Harbor process mapping cannot cause a local PID stop. Harbor affinity applies even to an Admiral-created/shared-filesystem dock; repository location alone is insufficient. Read the persisted assignment/job when transient maps are absent, refuse unconfirmed remote stops, and treat a durably ended remote job as a safe repeated-stop no-op. Native local/remote mixed allocator identity and arbitrary pending RPC ownership are separate boundaries, not universally certified by these checks.

Evidence: `/tmp/armada-stop-durable-{before,after}.{json,log}`, `/tmp/armada-stop-waiters-{before,after}.{json,log}`, `/tmp/armada-stop-replay-{before,after}.{json,log}` and boundary/final validation files under `/tmp/armada-*`. Tests use isolated transport/database fixtures, not real user jobs. The first broad run passed 2,241/2,242 with a collision-test setup failure (duplicate vessel name before exercising collision). The suffix fixture was corrected; final-source checks are recorded below. Earlier passing subsets do not validate later revisions.

Recall-path review additionally reproduced swallowed stop errors followed by captain release and dock reclamation. Recall now preserves the active assignment on an unconfirmed stop, records remediation on the mission and returns failure to its caller. The isolated regression fails before and passes after (`/tmp/armada-recall-{before,after}.{json,log}`); an initial fixture compile used the wrong stub property name and was corrected before behavioral reproduction. This is separate from health recovery.

Focused final lifecycle/metrics/runtime checks passed 72/72 on net8; the pre-recall broad net8 run passed 516/516 and solution built warning-free. Final source including recall correction passes Services/Runtimes net10 **2,247/2,247** (225.087s), affected net8 **521/521** (99.953s), and a Release solution build with **zero warnings/errors**. Commands/results: `/tmp/armada-resumed-release-net{8,10}.{json,log}` and `/tmp/armada-resumed-release-solution.log`. CRLF-aware diff check passes. No failures or skips in final runs. Added lifecycle, metrics, outbox, reconnect/reconciliation, dock-affinity and captain-recall suites to regular CI.

At initial resume no new image or acceptance voyage had been dispatched; live services still had StartUtc 15:02:30 UTC. Read-only Git reconciliation found the dedicated checkout and remote acceptance ref already clean at `8c3e8b5`; archive `63cf889f` and production main `c9ff616f` remain intact. This is observed current source state, not yet the fresh dispatch/context preflight proof.

### Findings from this run

#### Missing decision-to-check traceability in the frontier cohort

The first implementation slice (package/legacy parity/stream completion) passed its named tests but Judge `msn_mvhsjdc1_vS4mIjOlF4z` requested rework for missing D10's optional HTTP transport injection seam. The scoped D10 text is present in archived Worker, Usability, Test Engineer, Linter and Judge instructions. No handoff loss is demonstrated. Worker and verifiers covered SDK wire behavior with local HTTP fixtures while omitting the declared injection interface; a compound decision also covered suite registration, which the Linter checked. The missing interface matters for later fixed-destination Cloud transport tests. This is an implementation/coverage omission in the frontier cohort as well, not specifically a DeepSeek weakness.

Version 3 decision-to-check contracts are implemented and verified: 2,226/2,226 Services/Runtimes cases on net10 and 157/157 targeted cases on net8 pass, with a warning-free Release solution build. Build/deployment completed and was verified at 08:54 UTC; a fresh full-pipeline version 3 plan remains pending. Implementation/validation details are in the completed appendix. Links establish declared structural coverage, not semantic completeness or proof that a check ran. Existing free-planning cohorts regenerate plans, so their rework counts are whole-workflow observations rather than an isolated Worker-model benchmark; a fixed-contract comparison remains a separate future test.


#### Frontier redactor rework: coverage omissions and an unsatisfiable literal invariant

Judge `msn_mvhsjdgo_4MXtpH3rsfG` independently restored/built and passed all 108 automated cases (21 redaction cases), then requested rework for three additional probes: (1) RedactUrl("https://host/path?token=private", ["?"]) masks the query delimiter before sanitizing, exposing query content; (2) SanitizeUrl("/p/http://a@b") changes a relative path to "/p/http://b" because scheme detection is not anchored at the start; (3) Redact("REDACTED", ["REDACTED"]) yields the mandatory fixed marker [REDACTED], which still contains the complete secret. The last case exposes conflicting fixed-mask and universal no-secret-in-output requirements, not merely a missed branch.

Worker's initial handoff acknowledged the marker limitation, and prior verifier output included unresolved marker/path concerns. This reinforces HF-05 (resolve satisfiability/exceptions before dispatch), HF-18 (composed delimiter/path cases), and HF-25 (known blockers still traverse the verifier chain). Rework 1 `msn_mvhv4sx0_SE0GP9ykE8t` started through the normal full chain. Root made no Wilson product changes. The updated brief explicitly requests atomic decisions and resolution of contradictory invariants. Decision links help declared coverage but cannot prove a compound rule is atomic or an invariant is satisfiable. This is further evidence that the failure mode is not specific to DeepSeek.

#### Follow-up fault/configuration coverage queued

HF-30: ApiAgentRuntime intentionally uses plain chat for ToolsEnabled=false or an empty allowlist; this must remain valid for ordinary Ask chats. AgentLifecycleHandler's API captain save validation checks endpoint existence/kind/enabled, while the mission launch prompt requires reading local instructions. A mission-specific bootstrap/read_file readiness guard is not established by that save validation. Reproduce the disabled/empty/no-read allowlist cases with isolated fixtures before deciding the launch behavior; do not globally reject plain-chat captains.

HF-31: HarborConnectionManager.KillJobAsync currently removes listener/tracking before sending its kill request, and RemoteAgentRuntime reports a synthetic exit after send. Existing timeout cleanup tests need to wait for actual remote receipt. Further fault tests should withhold/drop stop delivery, interrupt/reconnect the link, and prove no simultaneous old/replacement execution. The successful PM timer recovery had no observed overlap, so it does not establish those negative guarantees.


#### HF-29 — Native Claude noop wakeups keep a completed job alive

Claude Code 2.1.306 Product Manager `msn_mvhroi2u_60UiDr4qjXh` called ScheduleWakeup at 07:05:09 and 07:06:25, each delaySeconds=1200/noop=true while two Explore agents researched. It emitted its completion report by 07:09:29, but the process stayed alive. After the configured ten-minute inactivity threshold, Armada stopped it and relaunched at 07:19:39; RedispatchAttempts correctly rose to 1. Recovery completed at 07:22:06 and Architect proceeded. The voyage event history had two launches but no scoped cause for the recovery; that diagnostic gap is part of this fix. No server rollout occurred during the stall.

A controlled real-CLI probe, with no subagents or file/shell/MCP actions, reproduced the mechanism: ScheduleWakeup accepted a noop 1200-second timer, then Claude emitted a success terminal protocol event and completion marker but did not exit within the bounded observation. The probe deliberately stopped that lingering process (actual CLI exit 137; diagnostic program exit 0 denotes successful reproduction). The guarded variant exposed no ScheduleWakeup, made no tool call, emitted success and exited naturally 0 in 3.49 seconds. This is a bounded-job/native scheduling integration issue, not evidence of insufficient reasoning or a PolyPrompt failure.

Implemented mission-only guard: exclude native ScheduleWakeup/CronCreate; set fork mode off, session cron disabled and idle-query exit delay 1000 ms. Foreground agent work and shell policy remain available; nonmission lifecycle settings are preserved. The guard covers decoded and raw-event passthrough missions. It does not force completion from model text. Also preserve owned stall history with actual inactivity, threshold, planned recovery and remediation, and enrich exhausted-recovery failure reasons. Tests cover successful recovery retaining history, ownership, duplicate-health-check prevention, terminal failure diagnostics and lifecycle-mode boundaries. Focused net10 suites pass 74/74 and net8 runtime/coordinator/stall suites pass 229/229. The initial broad run passed 2,221/2,222; one old exact-text assertion expected the former short failure reason. It now verifies the prefix, threshold and remediation and passes focused validation; the broad rerun then exposed a separate test-only readiness race: RecordingHarborJobRunner publishes a received launch before ControlledHarborJobRunner registers callbacks. Waiting for jobs.Launched before driving start/output/exit fixes that harness ordering; no production Harbor race is inferred. Final net10 Services/Runtimes validation passes 2,222/2,222, with a warning-free Release solution build. Focused net8 validation of the corrected harness passes 40/40 (in addition to the 229/229 runtime/coordinator/stall checks). Coordinator/stall regressions are added to push/PR CI. Deployment verification completed at 08:25 UTC: both services healthy, server digest matches this build, all existing bind mounts preserved, and the sole existing PM redispatch remains 1. Harbor runtime hashes match the updated build and admission was re-enabled. CI 39567599970 passed. A fresh no-retry cohort remains pending; this existing diagnostic voyage cannot establish that gate.

Protocol evidence was obtained via the Harbor probe API, emitting only selected event metadata from the owned Claude session files, not raw credentials/transcripts. Original PM and both observed research children reported claude-opus-5-5; API child visibility remains incomplete (HF-11). Session folders normalize underscores to hyphens, so the first metadata lookup missed them; an expanded lookup found the real records. Inherited/user-config lifecycle flags were absent; the runtime already closes stdin, so an open stdin/inherited flag was not the cause. Probe evidence: `/tmp/armada-claude-lifetime-probe-20261021/{baseline,guarded}/result.json`; mission evidence: current frontier directory `pm-lifecycle-tool-metadata.json`, `pm-session-metadata-expanded.json`, `pm-recovery-events.json`.

Anthropic's [environment-variable reference](https://code.claude.com/docs/en/env-vars), accessed 2026-10-21, documents session-cron disabling, fork-mode control and automatic exit after the query becomes idle. The local CLI behavior was checked independently rather than treating documentation as runtime proof.


#### HF-25 — Known production blocker survives passing test stages

First redactor Judge `msn_mvhn58zl_67Vn75chHIm` independently restored/built `cf56f62`, ran 49 passing shared cases and both adapters, then reproduced D-1: with no registered keys, `Redact("Authorization: Bearer Bearer secret-token")` returns `Authorization: Bearer [REDACTED] secret-token`. D13 requires every token following Bearer to be masked. Scanning resumes at tokenEnd and skips the nested marker. Rework 1 is queued with the normal five-role chain; no runtime failure or SDK attribution.

Test Engineer and Linter had already recorded this blocking defect. Linter correctly left behavioral changes for a Worker under its role contract; its BriefContradiction papercut is not proof that this separation is a bug. Test Engineer's completion despite an unresolved blocker conflicts with the prompt's instruction not to claim completion while blocked. More prose alone does not enforce that instruction. Consider a structured stage outcome (coverage completed versus product blocked), with reproducible evidence and explicit scheduling of independent review/rework, so known blockers cannot look like accepted checks or consume redundant downstream work. Do not broaden every verifier into an unrestricted production editor. Cases for nested/repeated delimiter tokens should join overlap/collision matrices. The helper fix passed independent rework review; structured blocking outcomes/earlier routing remain pending. Evidence: mission outputs/logs/diffs in the current private retest directory.

#### HF-26 — Docker control works; native Compose action has a deployment path gap

DocMon backend `http://view.homedns.org:8740` is healthy, bearer auth works, and live OpenAPI exposes 37 paths. The Armada stack is `homedocker-armada`; server and dashboard are Running/healthy on v1.1.0. Read-only server exec confirmed PolyPrompt/3.5.0. The credential is stored only in a private local auth file and is not in this document or source control.

Native POST `/api/v1/stacks/homedocker-armada/pull` returns HTTP 200 with exitCode=1 and `open /app/C:\HomeDocker\compose-armada.yaml: no such file or directory`. The Linux DocMon container has Docker's socket and only its own data directory mounted. Compose labels refer to host Windows paths, and the original Compose directory is unavailable there. This is a reproduced deployment/configuration limitation, not an Armada mission failure. Always inspect operation exitCode rather than HTTP status alone.

A temporary helper launched through DocMon exec, using the cached DocMon image, read-only `C:\HomeDocker -> /config` and the Docker socket, successfully validated the Compose file and pulled both Armada images (exitCode=0). Its unmodified resolved config, however, changes the server's five bind sources from `C:\HomeDocker\armada\...` to `/config/armada/...`. Environment values matched, but a direct helper up could therefore use different data mounts. The unmodified helper configuration was not applied. Explicit overrides were validated: all five source/destination pairs and RW flags match live Docker inspection, and environment values match. A recreate dry-run exited 0 without changing either service. The temporary operational helper is `/tmp/docmon_armada_compose.py`; it uses cached DocMon image, read-only original Compose files, Docker socket, explicit existing host binds and `up --wait`. Actual recreation subsequently succeeded during the bounded-read rollout; see the completed appendix. Preserve image, ports, network/environment/mount configuration before deployment; a plain restart does not apply a newly pulled image. Root temporary helpers are removed after inspection. Evidence: current run's private `docmon/` subdirectory. No DocMon source fix is claimed.


#### HF-11 — API-visible CLI transcript is incomplete

Observed during Product Manager `msn_mvh7i6ia_rdjA0ADcCWh` on Claude Code. Both mission and captain log APIs return readable `> Running ...` lines whose longer commands end in ellipses; tool-result payloads are absent. Fetching all lines does not restore those details. This limits the requested review of every interaction: the monitor archives every API-visible log update, but cannot reconstruct omitted tool arguments/results. It is an observability gap, not evidence that the commands failed or caused a product defect. Recommended remediation: preserve a diagnostic structured transcript with existing access controls/redaction, independent of readable summaries, and expose paginated reads. No fix or new image is claimed yet.

#### HF-12 — Dependency probe documentation gap

Product Manager emitted a MissingDoc papercut: installed PolyPrompt 3.4.1 README connectivity table does not specify xAI/OllamaCloud probe routes. This is a dependency-documentation gap, not a reproduced package failure. The next stage is explicitly responsible for verifying actual client behavior and recording supported routes/authentication; an unsupported hosted capability must remain reported as unverified. No external package fix is claimed.

#### Current retest — scoped handoff improvement and remaining read amplification

The new Architect plan selects relevant decisions (14 total); first slice references only [1,9,14]. Worker description fell from 18,132 to 10,162 characters. Redaction is a separate prerequisite before metadata consumers, with explicit prefix/shifted overlaps and union-of-original-spans acceptance. Inference/discovery/tools/embeddings are still grouped into one broad slice; semantic scope review remains open.

Initial DeepSeek prompt was 35,385 tokens. After a single unrestricted `read_file` of `src/Test.Shared/WilsonSuites.cs`, the next inference increased by 60,036 tokens to 100,885. Later it reached 158,399 tokens. This identifies a concrete separate amplification path: the built-in reader defaults to the whole file, up to 1 MB. A bounded-default fix is now implemented and locally verified: ordinary reads return at most 200 lines/24,000 characters including a continuation footer; instruction files remain complete, explicit ranges/full reads and small-file output retain their behavior. Oversized single lines require an explicit read instead of returning partial source. Focused runtime suites pass 25/25 on each target framework, including six new paging/compatibility cases. Release build and deployment completed through the verified DocMon path; see the completed appendix. Other context amplification remains open. Evidence: `package-context-growth.json` and API-visible package mission log. Smaller handoffs alone do not resolve it.

#### HF-13 — Declared contracts remain broad and over-copied

Architect output is 53,885 characters, with 19 global decisions totaling 10,302 characters. Slice 3 still combines chat, streaming, tool protocols, embeddings validation and discovery despite the default prompt requesting separation. The structural gate accepts declared coverage; it cannot evaluate this semantic boundary. `DescribeMission` also copies every global decision into every slice, including unrelated decisions. First Worker description: 18,132 characters; instruction file: 28,560. Initial inference prompt: 35,398 tokens before reading the file; by iteration 27 it reached 96,087 tokens. These are observed sizes, not a measurement that attributes all growth to the decisions.

Remediation: support explicit per-slice decision references, keep full-plan artifacts retrievable, and render only relevant resolved decisions in each handoff. Add a plan scope/contradiction review before fan-out rather than treating schema validity as design approval. Retain acceptance assertions and failure cases; remove unrelated copied context first. A local fix is now implemented and under validation: optional per-slice `decisionIndexes` render only selected decisions, with range/duplicate checks; omitted indexes retain older plan behavior. New default prompts request explicit scoped indexes. This is not deployed to the current run. Semantic slice-boundary review remains open.

#### HF-14 — A trailing echo can defeat pipefail

Worker `msn_mvh7i8d0_gflbLdMm6eM` ran `dotnet run ... | tail -60; echo EXIT:${PIPESTATUS[0]}`. Armada already enables Bash pipefail, but the final echo returns zero. A local negative reproduction, `/bin/bash -o pipefail -c 'false | tail -n 1; echo EXIT:${PIPESTATUS[0]}'`, prints EXIT:1 and exits 0. This is a command/evidence risk; no actual failed Wilson suite has yet been established as concealed. Prefer executable-plus-argument-vector validation through `run_process`, with tool-side output truncation and its original exit code. If a shell is necessary, save and explicitly return the validation status. Require independent verification rather than accepting the tool's success flag as the test verdict. A local prompt update now tells Worker and verification personas to prefer executable/argument-vector tools and explicitly return shell validation status; this is guidance, not enforcement over arbitrary CLI shell scripts, and is not deployed yet.

Recoverable Worker exploration errors so far: one mistyped absolute path (corrected next turn) and two ad-hoc reflection probes that lacked assembly-loading dependencies. They did not fail the mission or indicate a PolyPrompt transport/tool failure. Distinguish these from Judge rework and runtime redispatch.


#### HF-18 — Passing cases omitted combinations implied by universal rules

Metadata Judge `msn_mvh7xiak_1iLTFnRfXYS` independently rebuilt and ran 62/62 shared cases plus both adapters, then reproduced two registry-level violations of existing D-3: `ApiType="Custom", ToolCallingApiFormat="XAIResponses"` returned no error because unknown-provider compatibility returned early; `https://user:pass@ollama.com` passed the fixed cloud endpoint check. The Judge did not claim HTTP reproduction. These are implementation/test omissions under an existing rule, not missing model tool capability. Strengthen applicable test matrices for universal prohibitions (including unknown values) and URI components, rather than adding only examples for known providers. Automatic rework 1 ran all five verification roles without operator restart, and the next slice stayed blocked behind Recorder. The replacement Judge independently verified that both findings were fixed, then requested rework 2 for three additional reproduced defects:

- Non-admin runner status leaked another configured runner's credential through an invalid format value. PUT validated against all submitted keys, but ConfigurationError only masked its own runner's key.
- `Redact("abcdef", ["abcd", "abcdef"])` returned `[REDACTED]ef`: sequential substring replacement left a credential suffix. Aggregate redaction must operate on complete secret spans; earlier per-runner masking must not defeat it.
- Stored `ApiType=" Ollama"` acquired canonical Ollama defaults; baseline did not recognize that padded value. This violates the plan's legacy compatibility rule, regardless of whether canonical behavior might otherwise seem preferable.

The second review independently passed 65 shared cases and both adapters before these probes. Passing counts still did not establish the universal credential/compatibility rules. The same classes were already discussed in historical findings; default guidance requesting credential collisions and adjacent cases was insufficient. Require an applicable field × credential-source matrix (same/other/overlapping key) and representative baseline normalization inputs, with observable assertions. No fixed prose or one run proves exhaustive coverage. Record sequential Judge discovery as a verification limitation rather than blaming all rework solely on DeepSeek.

#### HF-20 — Prefix-overlap fix is not complete overlap masking

Independent execution of the exact `CredentialRedactor` source retrieved through the rework-2 mission diff reproduced `Redact("abcdefgh", ["abcde", "defgh"]) == "[REDACTED]fgh"` in either key order. The helper skips an entire matched prefix and therefore misses a second credential beginning inside that span. The prior prefix example now passes. Evidence: `independent-redactor-probe.log` and API diff snapshot in the private run directory. No Wilson product file was manually edited.

Operator intervention: appended AC-SEC-OVERLAP to all six pending redaction-stage contracts and all six final-integration contracts by authenticated API, then read back the result. It clarifies existing D-16/R-SEC-1: compute the union of matching spans on the original text, preserve surrounding non-secret text and short-key/header behavior, and prove the regression at helper and caller-visible boundaries. This is an external acceptance finding; do not describe eventual success as wholly unassisted. Keep it open until agents implement the fix and independent checks pass.


#### HF-22 — Harbor GUI restart can fail before connecting

After this voyage was terminal and API status confirmed all five captains idle/no active voyages, two local Harbor processes were stopped and consolidated into one current Debug/net10 binary. The first relaunch exited 134 with `Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661`, before Harbor connected. Waking the display (`caffeinate -u -t 5`) then relaunching succeeded; API confirmed Connected at 23:01:27 UTC. This matches the upstream [Avalonia no-display startup issue](https://github.com/AvaloniaUI/Avalonia/issues/18895), but no broader device/display matrix was tested. No active mission was interrupted, and this did not cause the completed acceptance failure. A supported unattended/headless fallback or actionable startup recovery remains open; no product fix is claimed.

#### HF-23 — Resetting source does not invalidate Recorder facts

The next dispatch inherited ProjectContext saying PolyPrompt was now 3.4.1 with 53 cases and a new contract-test file, although the acceptance repository was reset to 3.1.0 baseline. EnableModelContext=false did not prevent this separate ProjectContext injection. Archived then cleared only the Recorder-generated ProjectContext through the API while Product Manager was running, before Architect dispatch. Product Manager had already received it, so source inspection must take precedence; no restart or claim of uncontaminated context is appropriate. The objective already prohibits reusing old implementation/memory. Durable context should carry source revision/provenance and be invalidated or clearly labeled historical after repository reset. The Product Manager explicitly noticed that project context did not match the actual baseline during source inspection, which mitigates this instance. This is an observed handoff-risk gap, not evidence that it caused a failed mission.

#### Current retest — first review revision and dependency stream finding

First Judge `msn_mvhed60p_r0BXr4fnALf` independently restored/built commit fc1d4c2 (zero warnings/errors), passed 43 shared cases and both adapters, then found six missing `model` assertions in streaming/tool payload captures. This was an explicit UPG-2 guarantee omitted by Worker and intervening verification, not a transport failure. Rework Worker eb17e7d added all six and mutated each expectation independently: each mutant failed 42/43 and restored code passed 43/43. Full verification chain repeats. API audit at 01:18 UTC: nine launches, no duplicate launches, no routing mismatch, no runtime redispatches.


A rework Worker papercut also claimed a branch-header/anchor mismatch. Independent checks found the persisted instructions, repository anchors, mission BranchName and original launch-log header all naming the same inherited branch. The claimed mismatch is **not reproduced** and is not queued as a confirmed Armada bug. Agent reports require evidence checks.

Record further issues with mission/stage/captain, violated requirement, reproduction/evidence, remediation, and verification status. Keep unresolved findings here; move verified fixes and finished work into the appendix.

### Appendix — completed work and historical evidence

#### HF-06 subtask — bounded default file reads deployed

Commit 3101c2cb bounds ordinary implicit read_file output to 200 lines/24,000 characters including continuation. Preserve whole CLAUDE.md/AGENTS.md, explicit ranges/full reads, small/empty content and file-size/error rules. Long lines are not split; an explicit opt-in is required. Six new regression cases verify lossless continuation, Unicode/character caps, oversized lines, integer-boundary ranges and instruction/error compatibility. Focused suites passed 25/25 on net8 and net10; full runtimes passed 159/159 on net10; Release solution build has zero warnings/errors and CI passed. Built/published v1.1.0 and deployed through DocMon; Harbor updated while idle, then the same voyage resumed. This fixes one read-amplification path, not all context growth. The base 175-tool footprint remains open.

#### DocMon operational deployment path verified

Authenticated backend health, live OpenAPI, container/stack inventory and read-only exec are verified. Native Compose path resolution remains HF-26; the verified helper mounts the original Windows Compose directory read-only, supplies exact live Windows bind sources/RW flags, and talks to the same Docker daemon. Config comparison and dry-run passed before mutation. Actual pull/up --wait then returned exitCode=0; both new service containers are healthy and the exact five persistent mounts and pulled server image were independently verified. No other stack was changed; temporary helpers are removed. Scripts/auth/evidence live under private /tmp files, with no token committed. Future recreations must recheck config/mount/image compatibility rather than blindly reuse rendered paths. Original Compose labels were Windows paths; helper-created labels point to its /config and temporary override paths, so native DocMon actions still require a product/deployment path-mapping solution. Source Compose files were not modified.

#### 3.5.0 server rollout continuity verified on an active mission

Server restarted at 05:13:26 UTC while Test Engineer `msn_mvhn58y3_nJAanQfo9fW` was active (started 05:12:33). It later produced commit `cf56f62` with RedispatchAttempts=0. The voyage's eight captain.launched events each occur once, including that mission. Subsequent stages continue with their strict captains. This verifies continuity for this observed rollout; it does not prove every crash/reconnect combination. Read-only DocMon exec independently verified the running container dependency. Evidence: `rollout-continuity.json` and `docmon/live-server-dependency.json`.

#### HF-24 — Native Ollama/Ollama Cloud streaming integrity fixed upstream and verified

PolyPrompt 3.5.0 (NuGet repository commit `4b2f7e25356e15442b430e3238b687fef3124e9b`) resolves the reproduced incomplete/malformed/native-error stream handling in both clients across ChatStreamingAsync, ToolChatStreamingAsync and GenerateStreamingAsync. Real loopback Postman simulation plus raw short-Content-Length transport fixtures exercised 22 streaming scenarios × two clients × three operations and twelve nonstreaming controls on each of net8.0/net10.0. All 288 invocations had one HTTP request each; final lifecycle results matched across frameworks. ActivityListener verified successful valid turns, error outcomes for invalid streams, cancelled and abandoned outcomes separately.

Missing-terminal/empty/whitespace EOF, malformed or non-object records, native error objects, missing/nonboolean done, and broken transport now expose State=Failed and a StreamError. A later terminal does not erase an earlier error. Post-terminal content is no longer appended; terminal-only/no-trailing-newline controls complete correctly. Success deliberately retains its documented stream-open meaning. Partial tool calls and ToAssistantMessage remain available for compatibility; execution eligibility requires State=Completed. That compatibility behavior is documented, not an uncorrected silent-success claim. Armada currently invokes nonstreaming ChatAsync/ToolChatAsync, so this upstream correction must not be presented as proof that it caused or fixed prior Worker/Judge reworks.

Detailed original report: `../Polyprompt/OLLAMACLOUD_CLIENT_BUG.md`. Supplemental local artifacts: `/tmp/polyprompt-ollama-stream-report-20261021/nuget350-net10-state.json`, `nuget350-net8-state.json`, simulation/raw-server logs and runnable harness. These tests verify synthetic protocol behavior, not real Cloud availability/authentication. Deployment and Wilson full-pipeline acceptance remain separate gates.

#### Previous 3.4.1 retest — terminal result and review causes

Voyage `vyg_mvhed41p_WAfNMv3i4AY` failed at 2026-10-21 02:25 UTC. Final counts: 33 WorkProduced, 31 Cancelled, one Failed (65 total); no end-to-end success. The package slice passed after one automatic rework for missing six model-field assertions. The separate redaction helper passed its first independent review. Metadata consumed two reworks: pre-validation health/model-discovery HTTP and cross-runner credential exposure were addressed, but the last Judge independently found that explicit legacy Ollama `/v1/chat/completions` configuration was incorrectly rejected. The per-slice 2/2 budget was exhausted; strict selection correctly prevented captain escalation/substitution and displayed numeric budget plus remediation. Later slices were cancelled without running.

Passing 67 shared cases and both adapters before that final compatibility probe did not prove the universal preservation rule. The failure was application implementation/test coverage, distinct from the SDK stream defect. Preserve legacy-provider explicit path behavior while imposing fixed native-path rules only where required by new providers. The fresh run must verify this boundary rather than copying rejected code. No operator Wilson source fixes were made. Full prior evidence remains in `/tmp/armada-full-pipeline-retest-20261021` and the new run's prior-run archive.


#### Previous full-pipeline run — failed at metadata review

Started 2026-10-20 at 21:39:53 UTC on `view.homedns.org`: voyage `vyg_mvh7i6hn_Z5VjIzrlpnA`. The deployed server started at 21:38:13 UTC; all eight persona briefs matched the deployed `00f45ea4` defaults exactly at dispatch. Harbor was connected. Hosted CI for revision `7d820c4b` passed, including the new pipeline regression suites.

The previous Wilson acceptance voyage and its 33 completed missions were archived and deleted through the API. Confirmed zero remaining acceptance missions before dispatch. Acceptance branch reset from `49ac2ccc4e0c7f3af3364d7bd4c5848161616c2e` to pre-update baseline `8c3e8b5975cbe96e5700529fdfcfe0512d627ec2` (PolyPrompt 3.1.0), guarded by a remote force-with-lease. Prior source retained as `archive/pre-full-pipeline-retest-20261020`. Production Wilson main remains `c9ff616fd1b5262fe7edfd9ac295b9f264ed9650`.

FullPipeline: Product Manager → Architect → Worker → Usability Engineer → Test Engineer → Linter → Judge → Recorder. Claude Code handles planning and verification contributors, DeepSeek handles Workers, and Codex is the independent Judge. All eight assignments persist `StrictCaptain=true`; no implicit model substitution or escalation is permitted. The objective and acceptance baseline were unchanged from the preceding test; the later HF-20 addition is explicitly documented below.

Authenticated API polling every 25 seconds archives full API-visible logs, mission records, instruction snapshots, status transitions, and requested/actual captains. It captures terminal records even when a stage finishes between polls. Server log rendering omits protocol noise; this is not raw provider-token capture. Full log snapshots are retained when rendered content changes, avoiding an assumption that streamed lines are append-only. Evidence: `/tmp/armada-full-pipeline-20261020` (private local artifacts, not portable repository evidence).

Outcome: **the run failed**, ending at about 22:56 UTC after 76 minutes. First package slice passed on its first review. Metadata failed its third Judge review after two automatic reworks: broader overlapping credential spans still exposed a suffix in the helper and actual settings validation. The prior five findings were resolved. Strict DeepSeek selection correctly prevented escalation to a different model; downstream work was cancelled. API audit: 60 mission records (22 WorkProduced, 1 Failed, 37 Cancelled), 23 launches, zero duplicate launches, zero runtime redispatches and zero captain mismatches. Six implementation slices never started. No end-to-end or hosted Wilson provider success is claimed.

Terminal Judge: `msn_mvh9poa0_XHLiQnHeMz1`. It independently restored/built the branch (zero warnings/errors), ran 68 shared cases and both adapters, then reproduced `Redact("abcdefgh", ["abcdef", "cdefgh"]) == "[REDACTED]gh"` against the built assembly and the actual settings validator. This agrees with the separately observed HF-20 defect. The operator-added redaction/integration criteria were still pending when the pipeline stopped. No manual mission restart, model substitution or Wilson source edit was used.



Local remediation prepared during this run: per-slice `decisionIndexes`, range/duplicate validation, compatibility fallback for older plans, latest-brief migration preserving operator edits, and validation-status guidance. The final 155 targeted service tests pass on .NET 8 and 10, including migration and rendered-prompt checks. Every new slice also retains a full-plan mission/API reference. This code has not been published as images or applied to this voyage. HF-13 semantic scope review and HF-14 shell-status enforcement remain open.

Current-run first slice milestones: Worker `87aad329` upgraded the package and added five capture cases; Usability independently validated a route mutation; Test Engineer `cc99532` added six cases and five fixture mutations; Linter `5c3f9f2` fixed explicit-type conventions/dead code without weakening assertions; independent Codex Judge passed 53/53 plus both adapters. Confirmed package xAI connectivity GET `/v1/models`, Cloud connectivity GET `/api/tags`, model-list errors returning empty lists with failure information in CallDetails, and raw Bearer retention in CallDetails unless recording is disabled. These facts were explicitly carried from Test Engineer through Linter and retrieved by Judge. Actual provider connectivity is still unverified. Recorder completed and stored the verified dependency facts; prior memories were marked as historical rather than treated as current evidence.

Current-run milestones: HF-01 deployment fan-out verified: all eight slices have six downstream stages, and every acceptance ID plus verification text is retained in all 48 descriptions (`fanout-contract-check.json`). Rejection behavior remains locally tested, not deliberately triggered on production. Cleanup/reset verified; strict assignments persisted; Product Manager `msn_mvh7i6ia_rdjA0ADcCWh` produced stable R-/AC-/U- requirements with no source changes or unperformed-test claims. Planning completed; the package slice is accepted, while later product implementation remains under review.

Completed items: prior acceptance cleanup/reset and autonomous 33-mission run; prompt migration and bounded-slice guidance; mobile parity fixes and Android native flows; versioned plan validation, full-chain rework, contributor exclusion, strict dispatch selection, local regression checks, CI, image publication and verified deployment. Those mechanisms are implemented; their effect on this new run is still under the open acceptance gates above. Historical recommendations below are evidence, not a claim that every recommendation has been implemented.


#### HF-02 — Full verification chain preserved during live rework

First metadata rework: DeepSeek Worker `msn_mvh99zuo_cyX0HaRfjyj` → Claude Usability `msn_mvh99zvl_lcazzeHrQri` → Claude Test Engineer `msn_mvh99zwh_sU7SffWihuO` → Claude Linter `msn_mvh99zx7_AGaMHPBf48b` → independent Codex Judge `msn_mvh99zxw_OIZs95OlVNF`. Each completed and used its requested voyage captain. Recorder was reparented to the replacement Judge; dependent inference work remained blocked. The review correctly requested another revision instead of accepting green counts. Second rework also traversed all five stages on the correct captains. Its final Judge rejected the remaining defect, then the bounded policy cancelled descendants. Overall product acceptance failed.

#### Timing evidence for the failed full-pipeline run

Launch-to-process-exit event totals by role: Product Manager 2.3 minutes; Architect 9.5; Workers 13.9; Usability 8.2; Test Engineer 25.3; Linter 7.5; Judges 5.2; Recorder 1.3. These are observed role wall times, not isolated model-compute attribution. The run spent more time in independent verification than Worker implementation. Local Services tests ran concurrently for part of the run, so host contention is a possible timing confounder. No transport failure was observed. Two review loops, repeated full-suite/mutation checks, and broad copied context explain work performed; phase telemetry is still insufficient to allocate every wait.

#### HF-17 — A stale stage could rewind the shared branch

Code inspection found unconditional `git update-ref` during stage hardening. An isolated real-Git test failed against the existing implementation: an older completed stage moved a branch back behind newer committed work. This is a reproduced product bug, **not an observed cause in the current serialized voyage**. The local fix keeps already-contained commits, permits only compare-and-swap fast-forwards, and preserves divergent stage commits under `refs/armada/stage-commits/<SHA>`. An explicitly refused handoff becomes a failed mission with the commit, branch and remediation visible in mission details; downstream stages are cancelled, including during recovery redrive. Transport exceptions retain the existing best-effort behavior and remain a limitation. The guarded Git implementation must reach Harbor as well as the server; server-side ancestry checking additionally protects common stale handoffs with older Harbors. Regression tests cover normal advancement, stale heads, divergence, downstream cancellation and recovery. The fix is deployed to the restarted server and updated Harbor for the current retest; no unsafe production ref update was deliberately injected.


#### HF-21 — Review failure detail omitted the actual budget

The deployed terminal reason said strict selection prevented escalation and suggested captain/persona configuration fixes, but did not state that this slice had used both permitted reworks. Local remediation distinguishes an exhausted budget (`2 of 2 rounds`) from unchanged rejected commits, explicitly states that no substitution occurred, and directs the user to correct the Judge's identified code or configuration defects using the preserved branch and an independent Judge. The bounded limit and strict routing policy are unchanged. A regression reproduces the exhausted strict chain, asserts the numeric reason and remediation, verifies descendant cancellation, and proves no extra Worker is dispatched. Separate stagnation coverage prevents mislabeling unchanged work as budget exhaustion. The fix is deployed for the current retest; a live bounded-stop message will be checked only if one naturally occurs.


#### HF-03 — Strict routing verified throughout this run

All eight roles and both five-stage rework chains used the selected captains: DeepSeek Workers, Claude planning/contributing verification, Codex independent Judge. All 23 launches match the voyage overrides; no fallback, duplicate launch or runtime redispatch. At the per-slice limit, strict mode stopped rather than silently substituting another Worker model. This establishes routing behavior for this failed run, not full product correctness.

#### HF-19 — Broader prompt consistency checks

The initial broader local Services run finished 2040/2042 green; after corrections all 2042 existing cases passed. The final .NET 8 regression set passes 212/212, including the new budget case; the final broader .NET 10 Services run passes 2043/2043. The final solution build has zero warnings/errors. One failure expected the embedded Architect brief to require legacy mission markers; the new default requires a version-2 plan. The test was updated to assert the typed contract while retaining fallback/launch output-signal restrictions. The brief now explicitly forbids result/verdict lines as the launch instructions already did. The other failure showed that shortened memory guidance omitted the discoverable `search_memory` name; restore that name with availability/mission restrictions intact. Both suites were added to the standard CI pipeline checks. The Architect brief also names field × same/other-key and prefix/shifted-overlap negative cases, plus verification of shared safety helpers before consumers, remaining under 2400 characters. This is guidance, not proof of exhaustive verification, and is not applied to the running voyage.

#### HF-16 — Metadata defaults were not covered by the Worker fixtures

Usability `msn_mvh7xi7g_3hWohuQigDK` reviewed Worker commit `29c3ce4` and produced fix `254b04b0a6fc28edc24c07c8749068ff6b80f72b`:

1. An OllamaCloud request omitting endpoint inherited `http://localhost:11434` from the settings class and was incorrectly rejected instead of resolving to the cloud default (D-5).
2. Existing Ollama endpoints with slash/case/whitespace/empty variations were rewritten, also changing health URLs and violating compatibility.
3. A non-xAI runner using XAIResponses received duplicate errors for the same field.

The new `provider-default-endpoint-edges` case failed before the fix; after changes Usability reports 59/59 shared cases, both adapters passing and a warning-free build. Test Engineer expanded the suite to 62 cases and corrected a fixture that mislabeled an administrator token as non-admin; real non-admin access is now exercised. Codex independently passed those cases and requested two separate boundary fixes (HF-18). These are **pre-Judge corrections**, not automatic Judge rework loops. Track them separately so zero reworks does not imply a defect-free Worker delivery.

Cause: fixtures primarily exercised explicit, fully populated values rather than minimal deserialized requests and legacy value variations. The design already specified D-5 and compatibility; this was an implementation/verification omission, not lack of a written requirement. Local prompt guidance now explicitly requests omitted/null/empty/whitespace/inherited-default vectors, and verified component symbol owners (the Usability test initially called a static helper on the wrong class). This remains guidance; it does not prove fixture completeness and is not deployed to the current voyage.


#### Test setup — 2026-10-20

This is an observed acceptance test, not a claim of deterministic agent execution.

- Armada revision: `2bfd2297b7a9f80349b9139ff2bebde704ca9493`; live server restarted at `2026-10-20T18:23:56Z`, version 1.1.0.
- Live Architect, Worker, and Judge templates were updated by API and read back exactly before the rollout.
- Previous acceptance run: 21 missions and two voyages archived locally, then deleted at the user's request.
- Wilson acceptance branch reset from `45ce2db544a6c40664790908eb438065b82f3a9f` to pre-update baseline `8c3e8b5975cbe96e5700529fdfcfe0512d627ec2` (PolyPrompt 3.1.0). Previous state retained on a local archive branch. Production Wilson main and image tags were not reset.
- Fresh voyage: `vyg_mvh0jgzv_8StF48Imx3k`.
- Pipeline: WilsonArchitectReviewed; Claude Code Architect → DeepSeek API-endpoint Worker → Codex Judge. Captain assignments are preferences with configured fallback; actual captain use must be checked from mission records.
- Monitoring uses authenticated REST because Armada MCP tools are unavailable in this session. No dashboard/browser polling.
- Evidence directory: `/tmp/armada-bounded-slices-20261020` (machine-local, not a portable artifact).

#### Baseline and hypothesis

The preceding run eventually passed, but needed six automatic reworks, a corrective voyage, and one manual Judge restart. Findings included credential exposure, health snapshot association, stale probes, and editor-state preservation. Zero runtime redispatches did not mean zero review retries or operator intervention.

The new briefs assign design and acceptance-definition responsibility to the Architect. Each slice must define one behavioral outcome, components, approach, exclusions, invariants, stable acceptance IDs, negative cases, validation commands, dependencies, and uncertainties. Workers execute that bounded contract; Judges evaluate the same contract and report all evidenced findings together. A final integration slice must wait for implementation pipelines to settle.

These are prompt requirements, not enforced structured-schema fields or a guarantee that an Architect follows them. The plan parser remains backward compatible. Judge checks are independent verification, not proof of exhaustive coverage.

#### Observations

- The test request itself previously said “prefer a small coherent plan.” That competed with the new instruction to split independent behaviors. The new request removes this wording and explicitly invokes the bounded slice contract.
- New built-in briefs remain short (Architect 1,775 characters; Worker 1,573; Judge 1,634). Startup migrates exact prior defaults and preserves operator-customized prompts. Targeted tests passed 59/59 on .NET 10 and 59/59 on .NET 8.
- Architect mission `msn_mvh0jh0e_Lc5gzvenZVf` ran on Claude Code and produced eight slices: package-only upgrade; metadata/validation; inference/discovery/embeddings; tool protocols; health lifecycle; dashboard editing; API/SDK/docs; final integration. The final slice has a voyage-wide settlement barrier. This improves behavioral separation over the prior three-slice plan, but the inference slice still combines chat, streaming, discovery, embeddings validation, and error redaction. Eight slices is not itself an enforceable scope bound. Require an explicit execution boundary and split independent request paths rather than allowing broad umbrella outcomes. The output is 43,122 characters; slice descriptions range from 2,102 to 7,521 characters, so smaller implementation scope has not yet made planning/handoff text smaller. Planning took about eight minutes versus about 90 seconds in the previous run; the completed run did not demonstrate a speed improvement.
- The first Worker received a 15,312-character instruction file for a 2,102-character slice. Its first reported inference already contained 35,385 prompt tokens, growing beyond 49,000 by handoff. This is an observed context-efficiency concern, not a measured attribution: the model's prompt count also includes system/tool definitions and conversation history. Inspect and measure those separately before shortening only persona prose. The instruction file includes the overall objective and the full plan title inventory alongside the assigned slice.
- The first Worker stayed within the package-only outcome and reported baseline/post-upgrade builds plus all 42 shared cases. The first Judge independently restored and built both revisions. The first Judge accepted this slice on its first review; no rework or runtime redispatch was needed. Its handoff description is 5,164 characters versus roughly 28,000 for the previous broad backend review.
- Worker commands visibly used `dotnet build ... | tail` and `dotnet test ... | tail`. Subsequent source inspection confirmed Armada API `run_process` already selects Bash with `-o pipefail` on this Unix host, so absence of a visible inline setting is not evidence of masked failures here. Explicit nested shells/argument-vector execution remain responsible for their own pipeline semantics. Preserve original exit status and prefer tool-layer truncation; no hidden build failure was established.
- The live instruction snapshot contains the new exclusions, stable-ID acceptance, and final-integration guidance. Judge handoffs retain bounded prior evidence and do not reintroduce the legacy inline cumulative diff.

##### First metadata review: prompt guidance still leaves specification gaps

Codex rejected metadata commit `9ff3da8` despite all 47 shared tests passing. Two concrete findings were returned together:

1. A malformed endpoint equal to the API key appeared verbatim in an HTTP validation error. The Architect specified INV-SECRET but SECRET-1 did not enumerate credential collisions in every string field. Recommendation: the Architect should define a negative-case table/property test covering key-as-ID, name, type, URL, format, and upstream error body, rather than leave each worker/reviewer to discover those permutations serially.
2. Normalization corrected an existing OpenAI `/v1/v1/models` URL to `/v1/models`. The Architect's INV-COMPAT explicitly required identical normalized values, so the Judge correctly enforced the written contract, even though it preserves a malformed URL. Recommendation: distinguish preservation of working behavior from byte-for-byte preservation of incidental or broken defaults; name intended bug fixes and exclusions explicitly. This was a specification issue as well as a worker scope issue.

Independent real HTTP testing reproduced a contract mismatch: submitting `healthCheckUseAuth=false` for OllamaCloud persists `true`. The Architect explicitly required forced cloud authentication while the overall request required override preservation. If authentication is mandatory, explain it in the editor and reject incompatible values explicitly; otherwise preserve the override. Silently rewriting an accepted setting is confusing.

Automatic metadata rework was scheduled without operator intervention. The metadata rework was accepted on its next review (commit `1de738b`). No manual mission restart has been used in this run.

##### Observability gap

The inference Worker `msn_mvh0tprt_8f8tmvzjK3c` started at 18:44:17 UTC, logged one successful inference and a successful instruction-file read, then had a multi-minute interval with no new result. The API still reported InProgress and zero redispatches, without a failure reason. This does not establish whether provider queueing, transport, SDK retries, or execution scheduling caused the delay. At 18:48:55 UTC the Worker had resumed and reached inference iteration 13, with no recorded failed response or redispatch. The provider-reported compute durations did not explain the multi-minute wall-clock gap. Per-result provider timing does not explain an in-flight wait. Recommend exposing request/tool/validation phase, phase start time, wall-clock duration, timeout deadline, and attempt count in mission details and telemetry, rather than inferring a stall from silence.

##### Inference slice: unresolved dependency contracts reached the Worker

DeepSeek completed the inference slice as `e1b5287`, reporting 55/55 shared cases plus both adapter tests. Its recorded loop reached 98 successful responses and approximately 193,000 prompt tokens at peak. Codex subsequently reproduced a discovery failure: HTTP 401 from both xAI capability catalogs becomes an empty sequence, and Wilson reports Connected/Online=true without invoking the required `/v1/models` fallback. The unchanged branch still passed all 55 cases; the success-path fixture did not cover this failure. Two automatic reworks were needed: first to handle failed catalogs/fallback, then to enforce endpoint validation before sending requests. The final inference review passed. This is a materially larger worker effort than the package slice; the title still groups several independent behaviors.

The Worker emitted two concrete papercuts: the plan's proposed `MaxCallDetails=0` did not suppress raw provider-body logging, so a logging seam was needed to satisfy secrecy; and model-list clients return empty sequences on HTTP failures rather than naturally surfacing a provider error in status. These dependency semantics should have been verified in the Architect's contract research or a dedicated investigation slice. They are not evidence that the model cannot call tools. The Worker verified actual HTTP routes with local capturing handlers; real hosted-provider connectivity remains unverified.

Timing comparisons are observational: mobile native builds/tests ran concurrently on the Harbor host, and provider/transport wall time is not decomposed by current telemetry. Do not attribute total elapsed time solely to prompts or model compute.

#### Why the eight reworks happened, and how to reduce them

The dominant pattern was incomplete behavioral verification, not inability to execute tools. Workers often delivered passing tests that omitted the failure path the Judge subsequently reproduced. Two of eight slices passed first review; six needed rework, and inference and dashboard each needed a second revision. These are review-driven revisions, not eight infrastructure retries. Neither the records nor this single mixed-captain run establish an intrinsic DeepSeek capability limit.

Three causes overlap; they should not be assigned mutually exclusive percentages:

- **Implementation omissions:** credential redaction missed response/validation surfaces; incomplete SSE was treated as complete; asynchronous probe cancellation/identity missed configuration changes; editor normalization lost explicit choices and case-sensitive custom paths.
- **Specification and dependency gaps:** the Architect required exact preservation of a malformed legacy URL, conflicted on mandatory cloud auth versus explicit overrides, and proposed dependency behavior that had not been established. PolyPrompt model-list failures becoming empty results and logging configuration not suppressing raw bodies reached implementation as discoveries rather than resolved design facts.
- **Verification gaps:** tests proved selected happy paths instead of the stated invariant. The clearest example was final integration: 83 backend cases passed, but PUT exposed the key, request capture was attached after invalid-input checks, and every inventory check disabled live status. A zero count from an unattached handler proves nothing about outbound requests.

The final integration review was especially avoidable: its contract already explicitly limited the credential exception to administrator GET, required capture of invalid-input traffic, and required live-status listing. This was a Worker implementation/evidence failure, not missing instructions. Likewise, “incomplete streamed function call is not executed” was already stated; the missing EOF case needed an explicit fixture and self-check. More prose alone would not fix either failure.

##### Concrete prevention gates

| Gate | Required evidence before advancing | Reworks it targets |
| --- | --- | --- |
| Architect plan acceptance | Trace every overall acceptance criterion to an owned slice/test; resolve defaults versus explicit values, intended compatibility bugs, and cross-slice response/serialization rules | Contradictory metadata/auth requirements and omitted boundaries |
| Dependency investigation | Capture actual success, HTTP failure and empty-result behavior; verify logging and URL composition before prescribing their implementation | Discovery/fallback and redaction assumptions |
| Worker completion | For every acceptance ID, name the executable case, assertion, observed result and any skipped path; inherited test counts are not new evidence | Passing suites with missing guarantees |
| Test validity | Attach capture before the action; prove it observes a known request; make targeted regressions fail when the relevant guard is removed or the fixture is deliberately violated | False zero-request checks and vacuous success |
| Rework completion | Reproduce every prior finding, retain its regression, then inspect adjacent cases in that behavior before resubmitting | Serial second revisions in inference/dashboard |
| Integration | Exercise real local HTTP through configure → persist → reload → live inventory → inference/tools → health, with response/log leak scanning and SDK deserialization | Cross-surface gaps deferred to final Judge |

Use a reusable failure checklist rather than lengthening every persona: 401/403/404/5xx, empty success versus failed request, invalid settings before any HTTP, stream EOF without terminal completion, configuration removal/change during a queued or in-flight probe, and explicit false/empty/custom/case-sensitive values across save/reload. The Architect selects applicable rows and expected behavior; the Worker supplies executable evidence; the Judge independently challenges those claims. Avoid inventing expected behavior merely to fill the matrix.

Split the broad inference slice into discovery/status and inference execution, with tool streaming already separate. Preserve a final integration gate and serialize edits to shared files. More slices add overhead, so judge the change by first-pass acceptance and total elapsed time, not slice count. Do not split already successful package-only work further.

Prioritize contract checks and trustworthy failure-path tests ahead of additional prompt tuning. Captain pinning and phase telemetry are also needed for reproducible evaluation and diagnosis, but neither is proven to have caused these defects. Keep the Judge independent: reducing revisions by weakening its checks would hide the exact failures it caught here.

#### Mobile/tablet review

The React Native app shares the dashboard API client, types, and pure logic. Review found real gaps despite that sharing:

- A new dashboard-router-only duplication hook was treated as shared native code. Both lint and the shared-module test caught the missing `react-router-dom` dependency. It is now explicitly dashboard-only; the exception test recognizes router-bound code.
- Endpoint duplicate/edit/save was missing. Native flows now call the server duplication endpoint, retaining stored credentials without reading or displaying them, and allow editing before persistence.
- Native dispatch and fleet actions lacked per-run approval settings. They now expose Approve All, a 600-second/Deny default, configurable timeout and answer, administrator restrictions, and integer timeout validation. Fleet-action settings are sent under `Overrides.ApprovalOptions`, preserving other overrides.
- Native CLI permission settings omitted the timeout default. They now save Deny/Approve. Permission and mission-review inbox cards show countdowns and the known timeout decision; mission details show the review deadline countdown without inventing an unknown default.
- Endpoint validation now opens a modal immediately with progress and a result/error. Creating or duplicating an inference endpoint offers creation of an API Endpoint captain. Captain creation failure reports that the endpoint was saved instead of silently losing it.
- Voyage list deletion used “Purge” and lacked bulk deletion. It now uses Delete and Delete Multiple, retaining failed selections after partial success.
- The README still described most screens as placeholders; corrected to describe implemented native workflows and validation limits.

Mobile changes are committed in `88964f7c14d3dff26735af132e318bb88e02b76b`; the follow-up footer/flow changes are committed in `9ffcce82`. Strict TypeScript, lint, route coverage (86 dashboard plus eight mobile-only routes), and parity (573 entries, none planned) passed. Both platform Jest presets passed 78 suites / 1,247 tests. Tests cover credentials-preserving duplication, captain creation failures, immediate validation feedback, approval restrictions/defaults, partial deletion, platform-specific keyboard behavior, and validation errors outside the scrolling fields. Existing worklet warnings and asynchronous handles remain; Jest used forced exit. Production Hermes exports passed for both platforms. The Android Release APK built successfully; third-party Gradle deprecation warnings remain.

Native Android tablet coverage passed all four flows: sign-in, list/detail, sheets/Ask, and the added smoke test covering sidebar/rail, endpoint validation, and dispatch approval controls. A real long-form defect was found and fixed: validation errors previously appeared below scrolling fields while Save remained fixed. Errors now appear alongside the fixed actions. Screenshots confirmed the error is visible. Existing tablet flows were retained. The completed Android phone suite passed all ten native flows in 11m35s, including Build, Configuration, and sign-out. The tablet suite passed all four flows in 3m9s.

Native tooling limitation: iOS `simctl` hangs on this host. Maestro 2.11 enumerates iOS devices even with Android platform/device flags, confirmed by JVM thread dumps. A local-only runner makes Apple tool discovery fail fast for Maestro subprocesses only; it does not change global Xcode settings. Explicit `--platform` alone does not resolve this environment problem. Android tests use isolated temporary Admirals and synthetic records; they do not dispatch production jobs. No iOS simulator/device or App Store/Play publication is claimed. Phone operations also exposed an impossible page-edge centering assertion; the target was visibly present, and the flow now scrolls without requiring centering.

#### Outcome

Voyage `vyg_mvh0jgzv_8StF48Imx3k` completed from 18:24:56 to 20:16:01 UTC on 2026-10-20: approximately 111 minutes. All 33 missions are Complete. The pipeline needed eight automatic reworks, with zero runtime redispatches and no manual mission restart or corrective voyage this round. Complete status on a superseded review does not mean its original verdict passed.

| Slice | Automatic reworks | Findings that drove rework |
| --- | ---: | --- |
| Package | 0 | Accepted on first review |
| Metadata/validation | 1 | Credential collision leak; compatibility contract preserved a malformed legacy URL |
| Inference/discovery | 2 | Catalog HTTP failures falsely reported connected; missing pre-request validation |
| Tool protocols | 1 | SSE tool-call EOF without terminal marker reported success |
| Health lifecycle | 1 | Removed queued probe still sent; enabled flag missing from result identity |
| Dashboard | 2 | Lost tool format/custom URL; leaked validation values; case-sensitive path preservation |
| API/SDK/docs | 0 | Accepted on first review |
| Final integration | 1 | PUT credential leak; capture attached after negative checks; no live-status discovery coverage |

Actual execution: one Claude Code Architect, 15 DeepSeek Workers, one GLM Worker, and 16 Codex Judges; no Mux. The GLM worker was `msn_mvh1yaup_Zgag8ewoNRH`, despite its requested captain being DeepSeek. Mission records establish the substitution, not its precise scheduling cause.

Final accepted source was `76bcc4eb78170ea20c2a4fb0017fb93b449039c2`; acceptance branch HEAD is `49ac2ccc4e0c7f3af3364d7bd4c5848161616c2e`. Final Judge `msn_mvh4ab3c_G3lnFcekR3D` reported 83 backend and 57 dashboard cases passing, adapter checks, SDK compilation/syntax checks, and Postman checks. Independent testing then built the clean final branch and exercised real local HTTP: live/non-live status redaction, redacted PUT response with stored credentials retained, invalid settings rejected without persistence, and explicit tool/health enablement false retained. Python, JavaScript, and C# SDKs successfully deserialized authenticated runner/health responses against that server. No real hosted xAI/OllamaCloud Wilson inference was tested. Armada's actual DeepSeek execution is separate evidence.

Remaining coverage gaps: dashboard reload-after-save was reviewed/built but lacks a component regression; static Ollama `/api/ps` was outside capture; the C# nested health DTO omits the server's `HealthCheckEnabled` field although its runner DTO exposes the flag. The independently reproduced cloud health-auth rewrite remains a contract/UI issue. Do not equate the final passing review with exhaustive SDK parity or live provider certification.

The previous run took roughly 71 minutes but required operator intervention. This run completed autonomously, with more automatic rework and greater total elapsed time. Better containment is promising; faster execution or deterministic model selection is not established. Concurrent mobile builds on the Harbor host also confound timing. Monitoring used API snapshots and retained logs; a gap between conversational turns was reconstructed from server timestamps rather than continuously observed.

#### Implementation round after the completed acceptance run

The next rollout adds these prevention mechanisms; their effect on Wilson rework counts is not yet measured:

- Versioned Architect contracts (`contractVersion: 2`) require stable objective IDs, resolved decisions, named acceptance scenarios/expected outcomes/verification, failure cases or a non-applicability reason, validation commands, valid dependencies, and a final integration barrier. The same criteria and decisions are rendered into Worker and verification-stage descriptions. Invalid fenced contracts stop fan-out and expose remediation in mission details; they cannot silently use older blocks/prose. Legacy unversioned/customized templates remain supported. This validates declared structure/coverage, not semantic completeness or truth of test claims.
- Built-in Architect, Product Manager, Worker, Test Engineer and Judge briefs now emphasize resolved compatibility/default/override decisions, dependency failure semantics, capture before actions, observable acceptance evidence and adjacent rework cases. Exact previously shipped briefs migrate; operator edits remain intact. Briefs remain below 2,400 characters.
- FullPipeline Judge rework repeats the original intervening verification stages rather than replacing the chain with Worker → Judge. Routing/review settings and original acceptance criteria are retained, and Recorder waits for the replacement Judge.
- Judge selection excludes the reviewed Worker and intervening contributors, including when verification stages separate the Worker from its reviewer.
- Dashboard/mobile dispatch offer **Wait for selected captain**, persisted as `captainAssignments[].strictCaptain`. Strict assignments wait instead of silently substituting a captain. Stagnant/exhausted rework cannot automatically escalate to another model; it reports remediation. Default preferred-captain behavior remains compatible.

After deployment, repeat the same Wilson objective and validation checks on the pre-update acceptance baseline using built-in FullPipeline: Product Manager → Architect → Worker → Usability Engineer → Test Engineer → Linter → Judge → Recorder. Use Claude Code for planning/verification contributors, DeepSeek for Workers, and a separate Codex Judge, with strict captain assignments so actual model use is controlled. Delete only the Wilson acceptance test voyages/missions after archiving evidence; preserve production main/images and the previous accepted source on an archive branch. Compare first-pass acceptance, automatic revisions, wall time, actual captain use and unverified checks. Adding stages may increase total time; no speed improvement is assumed.

Still open: semantic plan contradiction detection, role-limited tool/context exposure, provider request phase telemetry and a new end-to-end result on the rollout. The Wilson cloud-auth rewrite and nested SDK health-field gap must be addressed by the fresh pipeline rather than copied from the accepted implementation. Retain the same overall behavior requirements; the new prompts should discover and handle these boundaries. Do not claim these recommendations are all resolved by structural checks.

Local validation for this implementation round: 153 targeted service cases passed on .NET 10 and the same 153 on .NET 8; the complete solution built with zero warnings/errors. Dashboard production build and 15 shared dispatch tests passed. Mobile type checking/lint passed, and seven dispatch interaction tests passed on each iOS/Android preset, including strict selection in the submitted request. The earlier full native mobile results are independent of this new switch; it has not been rerun on a device. A concurrent dashboard/solution build initially raced while generated assets were being replaced; the ordered rerun passed. A subsequent no-restore build required a full restore for Harbor's missing net8 assets; restored solution build passed. Neither was an observed product runtime failure.

The targeted pipeline contract/routing suites are also added to push/PR CI, so these regressions no longer depend solely on manually running the broader Services suites.

Rollout artifact: product changes committed as `00f45ea4`; `build-all.sh v1.1.0` completed successfully for server, dashboard and proxy, both architectures, pushed `v1.1.0`/`latest` and pulled them locally. Local tag identity was checked. Published manifest digests:

| Image | Digest |
| --- | --- |
| `jchristn77/armada-server` | `sha256:506c8eeb9d3a42254d07f9cc48dc74f5417099fd98fa7c444367841ec5a041cd` |
| `jchristn77/armada-dashboard` | `sha256:bfbcb1beeb952d0350a56ca79744a4501e05d394a7d10ddd38e333621b510c35` |
| `jchristn77/armada-proxy` | `sha256:17f872c50d443147b6c38b188a340f3bba7430845e09329fcb44266bae0d41b2` |

That deployment checkpoint is complete: rollout prompts were verified, the 33-mission voyage was archived/deleted, the repository was reset, and the full-pipeline run was launched. Hosted CI for `7d820c4b` passed. Current open work and progress are maintained above, rather than in this historical appendix.


#### Completed publication checkpoint — 2026-10-20 23:07 UTC

Product revision `8c2fd584215947c0852e4d9e3fd15db229847f26`: warning-free solution build, 2043/2043 .NET 10 Services tests, 212/212 targeted .NET 8 tests, and successful [GitHub CI](https://github.com/jchristn/Armada/actions/runs/39512438792). `./build-all.sh v1.1.0` exited 0, pushing and pulling all three images. Registry inspection confirms Linux AMD64/ARM64 and equal `v1.1.0`/`latest` manifests:

| Image (`jchristn77/`) | Manifest digest |
| --- | --- |
| armada-server | `sha256:4679ead5404ac1f3ddd3013357e1846ab21420edc4b6200d15765cc244d87228` |
| armada-dashboard | `sha256:dd694d9e8d9c4c5f30f52c4f0bbc393e140ec81b0b91353c2b321341c97b597d` |
| armada-proxy | `sha256:13fbe46539155b24b5d1b6a2f6c266fc635007af43118d03cfbd3ad3415695e4` |

This publication completes image preparation; it does not establish that the server has been recreated or that Wilson passed end to end. The server update was requested. No acceptance voyage is active.


#### Completed HF-27 — Named model validation on the owner Harbor (2026-10-21)

Live POST /api/v1/captains with Runtime=ClaudeCode and Model=claude-opus-5-5 (the exact model reported by a successful Harbor mission) returned HTTP 400 / ModelRejected: Claude CLI not installed on this host. Server uses Docker, connected owner Harbor advertises ClaudeCode, and RequireHarborForLaunch=true. AgentLifecycleHandler.ValidateCaptainModelDetailedAsync delegated to a local-only runtime factory, unlike actual captain execution. Default-model captains skipped the check, hiding this configuration gap. No probe captain was persisted.

Implemented in `5da71f11`, pushed to main and deployed: captain-specific validation uses CaptainLaunchRouter with the captain's tenant/user, a Harbor-owned scratch directory, the requested model/options, and fail-closed required-Harbor routing. Direct runtime-only validation retains its explicit local behavior. Bound validation to 30 seconds (the former five seconds is too tight for frontier startup), preserve caller cancellation, and stop remaining jobs using a noncancelled cleanup token. Startup/availability failures are distinct from model rejection. Eight added regression cases cover Claude/Codex routing, foreign user/tenant refusal, provider rejection, timeout/cancellation cleanup and missing Harbor CLI. Focused lifecycle/interactive suites pass 51/51 on each target framework; all 2,051 service regressions, the Release solution build, and GitHub CI 39552795209 pass; deployment and live positive-model validation now pass. Evidence: named-model-reproduction.json and /tmp/armada-model-validation-*.json/log.

Live POST captain creation succeeded for explicit Claude `claude-opus-5-5` and Codex `gpt-6.1-sol`; both returned idle captain records and were immediately deleted. This establishes actual validation execution through the connected owner Harbor, rather than merely a cached model catalog. It does not establish full-pipeline model quality or every account entitlement. Codex candidate models were obtained using read-only app-server `model/list`; catalog membership alone was not treated as inference proof.

`build-all.sh v1.1.0` completed; DocMon pulled/recreated the services. Server started at 06:04:10 UTC. Both services are healthy, running image IDs match the remotely pulled tags, and all original bind source/destination/RW tuples are unchanged. Server manifest: `sha256:79c13c080ef08dc0a06d89a1baa9f7ca2d354601d08ccebb8713c412e573373b`. Current acceptance voyage continued with zero RedispatchAttempts. Evidence: `named-model-live-validation.json`, `docmon/model-routing-rollout-verification.json`, and `/tmp/armada-model-validation-build-all.log`.

#### Completed live MCP session recovery across rollout (2026-10-21)

A real `McpToolClient` initialized against port 8711 before deployment, listed 175 tools and read status. The same instance called status after the server replacement: old session returned 404; the client initialized exactly once more and the replayed status succeeded. The active voyage remained visible. There were two successful status calls total and one session-expired 404. This verifies safe protocol-session recovery, not replay of ambiguous timed-out writes. No new client code was needed. Private trace: `/tmp/armada-mcp-rollout-probe-20261021/result.json`.


#### Completed live ownership/secret boundary checks (2026-10-21)

Twenty-three live assertions passed using two temporary tenants, same-tenant regular users, a foreign-tenant regular user, and temporary tenant admins. Regular-user endpoint creation forces caller ownership and UserSpecific scope despite supplied foreign ownership/TenantWide fields. Other users' private endpoints are absent from enumeration, return 404 by ID, and reject update/duplicate/delete. Create and authorized duplicate responses do not expose the credential; duplication retains HasApiKey without reading the secret. Tenant admins with no owned Harbor receive typed RuntimeUnavailable on named Claude/Codex captain validation instead of borrowing the existing administrator's Harbor (same tenant and foreign tenant). Regular users are denied captain creation at authorization, before model validation.

The first test script incorrectly expected the routing-level 400 for a regular user; actual 403 was the correct earlier authorization refusal. Repeated with authorized tenant-admin fixtures to exercise routing itself. All temporary test users/tenants and their cascaded endpoints/credentials were deleted in finally blocks after each attempt; production resources were untouched. Evidence: `live-isolation-checks.json`. This is focused live coverage, not certification of every route, role, provider or bulk operation.


#### Completed HF-28 — Separate bounded Mux inference deadline (2026-10-21)

Creating an ephemeral local Mux Qwen captain through the live API failed with EndpointProbeFailed: mux timed out after ten seconds. No captain was persisted. Source inspection confirms Mux `probe` performs real SendAsync inference, while Armada applies the same ten-second process budget as endpoint list/show. A later direct probe succeeded in 1.91 seconds with qwen3.8:27b and 14 built-in tools; this is a warm/repeated observation, not proof of why the first call was slow (cold loading/network/provider delay remain hypotheses). Tool capability count is configuration evidence; this probe sends no actual tool call.

Give inference probes a bounded 120-second deadline while keeping metadata inspection at ten seconds. Caller cancellation still flows through the existing executor; actual failures remain failures and there is no automatic retry. Regression verifies separate deadlines and existing timeout handling. Focused suites passed 60/60 on net10 and 48/48 on net8; Release solution builds with zero warnings/errors. A controlled native Ollama HTTP fixture delaying a valid completion by 12 seconds independently reproduces live captain creation failure at 10.17 seconds on the old server. A repeated actual Qwen probe through the Harbor API passed in 2.79 seconds, confirming routing and availability at that later instant. Fix `36d533a4` is committed/pushed; build-all v1.1.0 and DocMon deployment completed. The exact same delayed native HTTP fixture now passes captain creation in 12.40 seconds; the ephemeral captain was immediately deleted. Its independent Harbor-probe control passed in 12.29 seconds. This isolates the old ten-second process limit as a sufficient cause of the synthetic failure, without claiming the first real Qwen timeout had the same underlying delay. The owned HTTP fixture was stopped after validation. Full local-model pipeline coverage remains pending. Evidence: `mux-local-live-preflight.json`, `mux-direct-probe.json`, `mux-harbor-probe.json`.


Deployment verification: both services healthy, exact original mounts preserved, remote image IDs match this build, server manifest `sha256:ef277f63549c0de3e39f42c9c1b71a797cca0c7a5a79b7b117b7b0bad52b33d4`, server start 06:25:04 UTC. Existing voyage continued with zero redispatches. Evidence: `docmon/mux-probe-rollout-verification.json`, `/tmp/armada-mux-delayed-fixture-20261021/{before,fixture-control,after}.json`.

#### Local Qwen real tool and permission preflight (2026-10-21)

Through the Harbor command API, Mux/qwen3.8:27b proposed an actual read_file against a scratch challenge with an unseen random value. With Deny it returned tool_call_denied / approval and nonzero exit 2, explained that it could not read the file, and did not invent the value. With AutoApprove it performed exactly one successful read_file, returned the exact challenge line, exited 0/completed in 6.65 seconds, and left the scratch directory unchanged. Native model inference is through the existing Wilson gateway. The probe used 16 effective tools and no configured MCP server, so it does not establish behavior with the full pipeline's 175-tool context.

An initial isolated-config attempt returned 401 because the public endpoint inventory intentionally omits its stored API credential; this was test setup, not an endpoint runtime bug. The subsequent tests used the existing authenticated Mux config without copying or printing credentials and a strict empty MCP config. Deny and approved traces are preserved separately at `/tmp/armada-local-model-tool-preflight-20261021/result-{authenticated,approved}.json`; the first setup trace is result.json. No Wilson source changes were made by these preflights.


#### Additional independent API and native-local checks (2026-10-21)

Existing authorization coverage, REST/MCP/WebSocket scope, cross-tenant API, account/login security, WebSocket security, API compatibility and real McpToolClient end-to-end suites passed 121/121 on each net8/net10 (242 invocations), no skips/failures. These use isolated loopback servers and stub agent processes; no live production resources/models are touched, and this is separate from the 23 deployed-service assertions. Evidence: `/tmp/armada-cross-user-api-net{8,10}.{json,log}`.

A native `ApiAgentRuntime` using the published PolyPrompt dependency and real local qwen3.8:27b through the existing authenticated Wilson gateway completed one allowed read_file, returned the unseen challenge and completion marker, exited 0 in 20.15 seconds and left the scratch directory unchanged. MCP was disabled and tools restricted to read-only for this first check. This is real native API inference/tool execution, independent of the Mux harness; it does not establish full MCP/pipeline behavior or direct raw Ollama server coverage. Evidence: `/tmp/armada-native-local-preflight-20261021/result.json`.




#### Closed hosted-Worker acceptance round — historical checkpoint

#### Hosted-Worker run setup and deployment history

2026-10-21: upgrading Armada Core and Server from PolyPrompt 3.4.1 to 3.5.0, then repeating the Wilson full-pipeline acceptance test from pre-update baseline `8c3e8b5975cbe96e5700529fdfcfe0512d627ec2`. Production Wilson main is preserved. Prior failed voyage `vyg_mvhed41p_WAfNMv3i4AY` and all 65 associated missions were archived and deleted through the API; unrelated voyages were preserved. The checkout and remote acceptance branch were verified at baseline, and rejected work `77381fd` retained on `archive/pre-polyprompt350-retest-20261021`. Revision-stale Recorder ProjectContext and ModelContext were cleared and read back before dispatch (HF-23 mitigation).

Dependency verification is complete: 144 real-loopback HTTP cases on each of net8/net10 (288 invocations), using the published 3.5.0 package and ActivityListener telemetry. Both regular Ollama and Ollama Cloud, all three streaming operations, now distinguish failed, completed, cancelled and abandoned turns. HF-24's reproduced SDK defect is resolved locally and moved to the appendix. No hosted inference or full Wilson acceptance success is implied. Private evidence: `/tmp/polyprompt-ollama-stream-report-20261021/nuget350-net{8,10}-state.json`.

Armada solution builds both target frameworks with zero warnings/errors. Services/Runtimes regression suites passed 2,196/2,196 on net10; focused endpoint/runtime suites passed 60/60 on net8. No failures or skips. A further 66/66 focused cases passed with the repository Postman provider simulation enabled (23 recorded HTTP requests), covering actual Armada endpoint validation and native xAI/runtime tool paths. These are local contract checks, not hosted-provider certification. Harbor restarted and connected at 04:57:20 UTC with the rebuilt 3.5.0 dependency before dispatch; deployed Admiral still uses the prior dependency until server image deployment. Keep strict Claude planning/verification contributors, DeepSeek Worker, independent Codex Judge, and the eight-stage FullPipeline. Fresh voyage `vyg_mvhn5715_zVgaii9eBkg` started at 04:57 UTC. Product Manager `msn_mvhn571l_c0skwxGkXOd` completed without source edits; Architect `msn_mvhn58v1_KfTr0w1h6Ih` is active with the expected Claude captain. API collector runs every 25 seconds; no routing/duplicate-launch alerts so far. The objective changes only the package target to 3.5.0 and adds explicit regression boundaries learned from prior independent reviews (legacy explicit Ollama paths, validation before HTTP, and final streaming states). This is an informed regression retest, not a blind replay of the original wording. Current evidence directory: `/tmp/armada-polyprompt350-retest-20261021`.

Image publication: `build-all.sh v1.1.0` completed successfully at about 05:01 UTC; all server/dashboard/proxy multiarch images published and pulled locally. Dependency/update/findings commit `15999ec0` is pushed to main. Published server manifest is `sha256:e90f87b2bf6f683dc917c31d0c1da8597acc90630dcb666eb10ee77ebe47dff3`; inspecting its actual deps.json confirms PolyPrompt/3.5.0. GitHub CI run 39546981229 passed. Operator deployed images; API health confirms server restart at 05:13:26 UTC. DocMon exec of the actual running server deps.json independently confirms PolyPrompt/3.5.0. Harbor already runs the new dependency, so the acceptance run continues without an operator restart.

Latest checkpoint (06:55 UTC): dashboard rework-2 was independently rejected for a sparse-stash add/delete/restore defect and exhausted its two-round budget. The mission shows the reason and remediation; strict routing prevented hidden escalation. The redactor passed rework-1. The provider/defaults slice fixed its five original findings, was then rejected for explicit default-route health authentication, and is in rework-2 verification. This acceptance run cannot finish successfully; Harbor admission is drained while the active verifier finishes, before archiving/stopping/resetting for the next cohort. Zero infrastructure redispatches were observed. Deferred MCP schemas are implemented and pass local/native-live harness checks, but are not yet deployed.

Controlled rollout: Harbor was drained after current jobs finished, the bounded-read change `3101c2cb` built/tested/pushed, and build-all v1.1.0 published. DocMon pulled/recreated the two Armada services with explicit existing Windows host binds; both are healthy, all five mount source/destination/RW tuples exactly match before deployment, and the running image matches the pulled manifest `sha256:e7a347d4330a2898d2dea1fdc74ce90dd1e22635a78270b7644d1b250469a4f6`. Server started 05:36:11 UTC. The Release Harbor was updated at idle and re-enabled at about 05:37 UTC; the same voyage resumed with no mission cancellation/relaunch. The pause is operator maintenance and must be excluded from claims about unassisted pipeline wall time. GitHub CI 39550090787 passed.

Model inventory: successful Claude telemetry reports `claude-opus-5-5`; Codex telemetry currently reports the generic `Codex` label rather than a precise model. Existing CLI captains leave Model unspecified. Named Claude model creation was reproduced failing on the Admiral host (HF-27). Owner-scoped Harbor validation is now deployed and live create/cleanup succeeded for `claude-opus-5-5` and `gpt-6.1-sol`; full model-pinned pipeline cohorts remain pending.

#### Completed HF-06 subtask — Deferred MCP schemas deployed

Controlled native-local comparison used qwen3.8:27b through the existing Wilson gateway and the actual Armada runtime/MCP client. Eager scoped tools: 723 initial input tokens, 10.91 seconds; eager full 175-tool catalog: 36,271 tokens, 55.32 seconds. Both read the unseen challenge correctly. One paired observation does not establish a benchmark average or exclude caching/load effects, but the reported token difference directly measures the catalog overhead.

Implemented large-default-catalog schema discovery in ApiAgentRuntime: retain all policy-approved MCP routing, initially advertise built-ins plus a local discovery function, then load up to four exact/keyword-matching schemas per query. Discovery performs no remote action. Small catalogs (32 or fewer), explicit allowlists and discovery-name collisions remain eager. Exact policy restrictions, caller-scoped MCP auth and built-in collision precedence remain unchanged; direct permitted names remain callable. No endpoint schema or SDK setting is added. This reduces schema exposure, not permissions, history or process-output size.

Seven added runtime cases cover discovery then real MCP dispatch, isError, bounded pagination/deduplication, invalid/empty/past-end queries, explicit policy/forged calls, threshold/collision compatibility and permitted direct calls. All 166 Runtimes cases pass on each net8/net10; the Release solution builds warning-free. Ten focused MCP/Ask HTTP end-to-end cases pass. The repository Postman provider simulation passes 73/73 with 23 real native Ollama/xAI HTTP requests. These tests are local contracts, not hosted-provider certification. A rebuilt native harness (not yet the deployed Harbor) repeated the same full-catalog file task: 2,782 initial tokens and 20.13 seconds, exact challenge returned. An explicit live discovery/status/read task passed in 29.85 seconds: initial 2,827 tokens, then 3,153 / 6,323 / 6,483; exactly discovery, scoped status and read_file completed successfully, with actual ActiveVoyages=1. These are single observations and a small tool task, not end-to-end acceptance or measured worker quality improvement.

Evidence: `/tmp/armada-native-local-preflight-20261021/{eager-full,eager-scoped,full,discovery}/result.json` and `/tmp/armada-deferred-all-runtimes-net{8,10}.{json,log}`. Deployment and updated Harbor are verified in the current checkpoint. Full API-cohort quality improvement, history bounds and process-output reduction remain unverified.

#### Current run terminal dashboard rejection (06:45 UTC)

Dashboard rework-2 Judge `msn_mvhq3om4_CaXN5R0eUSD` requested another revision and failed terminally at the configured two-round budget. Strict selection correctly prevented hidden escalation/substitution, and the mission details contain reason, budget and remediation. Its Recorder was cancelled as blocked. A previously reported sparse-array stash issue remains: load A, add B/C, customize C, switch provider, delete A, switch C back; filtering sparse stashes shifts C into B's slot and loses/restores incorrect state. Fifty-one passing dashboard cases did not cover that sequence. The reload/stale-error findings were fixed, so this is not unchanged-source stagnation.

The voyage could not meet full acceptance with this rejected slice. It was archived/stopped at idle, deleted with all missions, and the source baseline reset before the next cohort. Do not manually patch Wilson or present the failed run as a success. Stable per-entity UI state and add/delete/reorder/reload interaction coverage remain important planning/test gaps; absent DOM test tooling and narrow allowed-file scopes limit the current helper-only checks.

#### Deployed bounded-Claude lifecycle guard (completed implementation)

Commit `b9fefbaf` was built with `build-all v1.1.0`, pushed and deployed through DocMon. Health, exact data mounts, image identity, unchanged recovery counts and Harbor runtime identity were verified before resuming admission at 08:25 UTC. Evidence: `/tmp/armada-polyprompt350-retest-20261021/docmon/claude-lifetime-rollout-verification.json`. The private rollout verifier initially finished its assertions and saved evidence but raised a TypeError in its final message because a digest variable was reused for a retry list; the message was corrected and the full verification rerun successfully. This was a test-helper reporting defect, not a deployment failure. Fresh-run acceptance remains open under HF-29.

#### Frontier package slice rework accepted

After deployment resumed Harbor admission, Judge `msn_mvhttb22_ec0bAmKgtCQ` accepted the first implementation slice after one rework. The report records independent Release build, NUnit/xUnit adapters and 87 automated cases, and confirms the transport injection omission was resolved. This is a stage result, not overall acceptance; credential redaction Worker `msn_mvhsjddk_6w94LEiiPuQ` began next, with no additional infrastructure redispatch. The current cohort remains diagnostic because of its earlier PM timer recovery.

#### Live guarded Worker and residual validation-status risk

Harbor API process inspection matched captain ProcessId 11890 for credential-redactor Worker `msn_mvhsjddk_6w94LEiiPuQ` to a Claude invocation with ScheduleWakeup/CronCreate disallowed; selected proof is `/tmp/armada-frontier-retest-20261021/live-worker-lifecycle-guard.json`. No new infrastructure redispatch was observed at that checkpoint. Worker logs nevertheless show a dotnet command followed by `echo "exit=$?"` and grep despite the prompt's status-preservation guidance. Its 103/103 claim is not treated as independent verification; later verifier/Judge results remain required. This reinforces open HF-14 rather than establishing a failed test or a provider bug.

#### Prior hosted-Worker context and review checkpoint (06:34 UTC)

The three first-pass Judges so far all requested revision. Redactor rework-1 passed; dashboard and provider/default rework-1 each fixed every prior blocking finding, then independent review found another missing boundary. Dashboard: provider stash indexed by array position survives settings reload and transfers a replaced runner's endpoint/key into another draft. Provider defaults: explicitly supplied default xAI/OllamaCloud health URLs plus auth=false bypass D7's required default-route authentication. Both have bounded rework-2 queued. These are additional boundary omissions, not evidence that prior corrections were ignored. Full acceptance has not passed.

Six observed DeepSeek Workers start at roughly 35.4k reported prompt tokens before reading the mission file. Initial mission files range from 19,003 to 32,798 characters. First package and dashboard Workers reach 127,818 and 131,069 input tokens respectively; their first reworks reach 108,503 and 126,121. The new bounded file reader removes one source of large implicit reads but does not bound process output or accumulated history. There is no context compaction in ApiAgentRuntime; provider context truncation/retention and its effect on these defects have not been demonstrated. Do not diagnose the model from token pressure alone. Next controlled checks compare a real local tool turn with small versus full MCP exposure before changing defaults; preserve exact permissions and record any policy/configuration changes. Evidence: `quality-context-checkpoint.json`.


Timing note: Harbor admission was deliberately drained during the lifecycle update (roughly 08:14-08:25 UTC). These maintenance delays and concurrent local regression builds confound wall-clock model comparisons.

NuGet version checkpoint: the official flat-container index (`https://api.nuget.org/v3-flatcontainer/polyprompt/index.json`) still reports 3.5.0 as latest during this cohort. The local Python TLS trust lookup failed before a response; system-trusted curl succeeded without bypassing certificate checks. This lookup issue was local tooling, not a PolyPrompt provider failure.

#### Decision-to-check contracts — implementation and local validation completed

Version 3 criteria carry required decisionIndexes (explicit [] for general checks); references must be unique, one-based and within the slice scope. Every resolved plan decision needs a named acceptance check. Invalid plans fail before Worker dispatch with decision-specific remediation. Slice descriptions number decisions and preserve criterion links across downstream roles. Legacy/unversioned and version 2 plans remain compatible. Built-in brief migrations preserve operator edits; the Architect asks for atomic rules and resolution of contradictory invariants.

Final checks: Services/Runtimes net10 2,226/2,226 in 211.096 seconds; targeted net8 157/157; Release solution zero warnings/errors. Earlier broad runs each exposed one test issue: an obsolete contractVersion:2 expectation, an immediate stop-delivery assertion racing the remote transport, and a prompt-text assertion after condensed wording removed analyze. The intended version expectation was updated, remote cleanup now waits boundedly for actual controlled-job stop using synchronized snapshots, and analyze wording was restored. These corrections preserve the asserted behavior; no production stop-loss or provider bug is inferred from the fixture failures. Evidence: `/tmp/armada-decision-links-all-final-net10.{json,log}`, `/tmp/armada-decision-links-final-verified-net8.{json,log}`, `/tmp/armada-decision-links-solution-final.log`. Fresh deployed-cohort quality remains open.

#### Decision-contract image deployment and redactor acceptance (completed)

Commit `a6b8b29e` passed CI 39572267011, completed build-all v1.1.0 (all three multiarchitecture images), and was pulled/recreated through DocMon. Verification at 08:54 UTC confirms healthy services, matching published/running image, identical host bind mounts and unchanged redispatch counters. All four changed live persona briefs exactly match their new embedded defaults; each matched the previous shipped default before deployment, so no operator customization was overwritten. Harbor stayed connected/enabled. Evidence: `decision-links-live-verification.json`, `pre-decision-links-prompts.json`, and old evidence root `docmon/decision-links-rollout-verification.json`.

Judge `msn_mvhv4t1i_tSb36mbrtIj` independently passed the redactor rework with fresh restore/build, both adapters and 113/113 automated cases. It confirmed query-boundary and relative-path regressions fixed and accepted the explicit mandatory-marker exception. Its start time remained 08:52:19 and RedispatchAttempts stayed 0 through the server recreation; the job finished and Recorder followed. No no-retry claim is made for the entire diagnostic voyage.

HF-30 isolated reproduction: four cases (ToolsEnabled=false, AllowedTools=[], status-only and wrong-case READ_FILE) each pass plain-chat captain validation, but reach the instrumented runtime factory during mission launch. Expected creations=0, observed=1 in all four. The factory deliberately throws before any provider request; no live mission was dispatched for this fixture. Results: 40 existing lifecycle cases pass and four new guard expectations fail, `/tmp/armada-api-mission-readiness-before.{json,log}`. The in-progress guard/tests live in an isolated worktree and are not in main or deployed images.

#### Closed frontier cohort setup and interim checkpoints (historical)

Fresh frontier cohort `vyg_mvhroi21_DBccRAmgb0h` started at about 07:04 UTC on 2026-10-21. FullPipeline remains Product Manager -> Architect -> Worker -> Usability -> Test -> Linter -> Judge -> Recorder. Worker is explicitly pinned to Claude Code `claude-opus-5-5` (`cpt_mvhrmu1n_xvbXFWpc4TW`); independent Judge is explicitly pinned to Codex `gpt-6.1-sol` (`cpt_mvhrmxe8_skVOSB9nDUf`). Both passed live owner-Harbor model validation before dispatch. Original Claude Code captain handles planning/verification contributors. All eight assignments are strict. The SDK-contract investigation passed its first Judge review. Package/legacy parity/stream completion passed after one review rework. Credential redaction is in its first full-chain rework after URL boundary/path defects and a fixed-marker invariant conflict; its Test Engineer is active at the 08:48 UTC checkpoint. Product Manager required one infrastructure recovery caused by a native pending timer; this is a diagnostic run, not a no-retry acceptance pass. At dispatch, the objective, acceptance boundaries and persona prompts matched the preceding hosted-Worker cohort. Subsequent lifecycle/runtime updates make this a diagnostic mixed-runtime run. Runtime and harness behavior must be included in comparisons; one recovery already prevents a clean no-retry result.

The prior DeepSeek voyage `vyg_mvhn5715_zVgaii9eBkg` was archived and stopped at idle after terminal dashboard rejection, then deleted with all 87 associated missions. Every mission ID and voyage read back 404. Two source-bearing branches were preserved remotely under `armada/acceptance/archive-rejected-polyprompt350-20261021-{1,2}` at `73fdb700` and `a9b9cd80`. The PM/Architect branch had no remote ref or source changes and points to baseline. The checkout's production-ref push guard was retained; its rejection of an initial archive/* ref was handled using the permitted armada/* namespace. Eight unrelated voyages were preserved.

The dedicated Wilson checkout and remote acceptance branch are clean at pre-update `8c3e8b5975cbe96e5700529fdfcfe0512d627ec2` (PolyPrompt 3.1.0); production main remains `c9ff616fd1b5262fe7edfd9ac295b9f264ed9650`. ProjectContext/ModelContext were cleared and read back. Earlier branches/memory must not be copied. Evidence: `/tmp/armada-frontier-retest-20261021/{reset-evidence,captain-preflights,prepared-request,started-voyage,initial-voyage}.json`. Its API collector polls every 25 seconds. CLI logs expose abbreviated interactions (HF-11); monitoring archives everything available, not an invented complete raw transcript.

Runtime change `48f9dd98` built warning-free, passed regressions and GitHub CI 39558460747, then build-all v1.1.0 completed. DocMon pulled/recreated the services: both healthy, exact original bind mounts retained, running image matches published server manifest `sha256:b3c5052820ffd2a10d7a544b313c5d8c6b2259f1e91a6df8e09af26ab3bab094`; server started 07:00:09 UTC. Harbor restarted at idle with matching rebuilt Runtimes DLL hash and reconnected at 06:59:27 UTC, then was re-enabled before dispatch. Deferred MCP schemas apply to API captain runtimes, not this cohort's CLI Worker; fresh full API-cohort impact remains unverified. No user image update is needed for this deployment.


#### Frontier provider/default failure sequence

First Judge `msn_mvhsjdod_dl3lM73jDUu` passed 132 named cases but reproduced a null runner entry breaking otherwise valid runner listing (HTTP 400 rather than 200). Rework-1 Judge `msn_mvhwksom_VIoIMZ9nePO` passed 133 cases but reproduced settings JSON type mismatch returning 500 instead of required 400. Rework-2 Judge passed 134 cases but reproduced two leaks: Endpoint/HealthCheckUrl in status retained a stored query secret, and a malformed settings JSON error echoed a submitted secret as part of JsonException.Path. Prior verifier outputs and excluded fields need coverage review; do not infer that the Worker had received those exact defects in the preceding Judge report. That preceding report focused on JSON status handling. The budget correctly halted work and cancelled 43 dependent missions; no rejected work was approved for landing. These frontier misses reinforce HF-04/05/18/25 and do not isolate model capability as the cause.

#### API mission configuration guard — implementation locally verified

Main `8900f9d4` adds mission-only validation before runtime creation when tool calls are disabled or an allowlist is explicitly empty. Ordinary Ask chat remains valid, and nonempty/read-only/custom tool policies are preserved. Prototype expectations that required read_file specifically were narrowed: approved process or other tools can legitimately provide instruction access, so a mandatory tool name would be an unsupported restriction. A typed configuration error now persists in mission details as existing Infra/failureReason, releases captain/dock, emits an owner-scoped signal, cancels dependent stages and terminates the voyage instead of backing off forever. No new wire field/enum value is required.

Tests: Services/Runtimes net10 2,233/2,233; targeted net8 lifecycle/pipeline/failure classification 98/98; Release solution zero warnings/errors. New negative cases prove zero runtime creation, positive cases preserve alternate/read-only policies, and assignment-level coverage proves terminal state, ownership, cleanup and no retry. The new owner-specific fixture initially lacked its user/tenant records and matching captain ownership; those setup issues were corrected before the passing run. Its first compile also used the wrong signal-enumeration overload; the final test queries the actual tenant/user scope. Evidence: `/tmp/armada-api-mission-readiness-{before,terminal,broad-net10,net8}.json` and `/tmp/armada-api-mission-readiness-solution.log`. Image deployment and a live failure readback are pending.

#### HF-30 — Disabled-tool mission readiness completed and live verified

Main `8900f9d4`, CI coverage/documentation follow-up `95da8d55`, completed build-all v1.1.0 and DocMon deployment. CI 39621537266 passed. At 15:03 UTC both services were healthy, server image identity matched the new build and all original bind mounts were preserved; no acceptance voyage was active during this rollout, so no active-job continuity claim is made for it. All deployed persona defaults remained available.

Live API probes covered ToolsEnabled=false and an explicit empty allowlist. Both missions failed with Infra plus corrective Configuration > Endpoints instructions in the actual mission-details response, had no process, zero redispatch attempts and released captains. A real Harbor-owned HTTP listener recorded zero requests of any kind. All six temporary endpoint/captain/mission resources were deleted and read back 404; the owned listener was stopped. No visual dashboard interaction is claimed; the failureReason wire field is the existing dashboard/mobile details source. Evidence: `/tmp/armada-api-mission-live-20261021/{result,disabled-mission,empty-mission}.json`, rollout proof `/tmp/armada-polyprompt350-retest-20261021/docmon/api-mission-readiness-rollout.json`. The checkout and vessel context were reset/reverified again after these isolated negative probes, immediately before the fresh full-pipeline run.

This completes the disabled/empty-policy guard, not a guarantee that every nonempty custom allowlist or provider/model supports every workload. Those remain part of the cross-model matrix; permissions and intentional plain-chat behavior are preserved.


Native Harbor rollout assessment: changed production methods are Admiral-side connection management/metrics, mission runtime observers, captain recovery/recall and prompt generation. No Harbor link client/job-runner/native CLI implementation changed. Those callers send the existing lifecycle protocol unchanged, so this revision requires server deployment but no native Harbor restart. Native startup/display behavior (HF-22) remains unverified/open. Docker context is the reviewed worktree, where RESTART_TESTING.md is absent; its `.dockerignore` also excludes Markdown except README. No credentials are staged.


Publication check correction: commit 2adfcfbb passed all behavioral tests but the first clean Docker compile and GitHub CI 39643680830 found CS1571 (duplicate XML jobId parameter documentation added during the async stop-intent edit). The earlier incremental solution build emitted zero warnings because its outputs were already current; that did not establish a clean warning-free compilation. The first images were published but not deployed. The duplicate line is removed. A full Release solution Rebuild with warnings as errors now passes with zero warnings/errors (`/tmp/armada-resumed-clean-rebuild.log`, 6.45s). Republish before rollout. No behavioral production code changed for this correction; the 2,247/521 behavioral results remain applicable.


#### Resumed lifecycle publication and deployment — 2026-10-21 17:32 UTC

Corrected image-source revision `f027b8dc636ac1553f2b6f142b0274599762c797` completed `./build-all.sh v1.1.0` with no compiler warnings/errors. First images from `2adfcfbb` were never deployed; they were superseded after the duplicate XML-comment correction. Final manifests:

| Image | Digest |
| --- | --- |
| server | `sha256:98166deb55d84c171720f340612198e93169f95974ede4450c11b24338e6536a` |
| dashboard | `sha256:2869888a35afcf2379b8fcc3a35f0e2d7a422a3a1d3088db5b5955a9717b9ef1` |
| proxy | `sha256:51a9a272a48bd49cf08817d7314ea54c021c9e419915f2b9fa8b7d1182b1758f` |

DocMon pull and up each returned exitCode=0. Verification confirms both actual running service digests match the final build and exact mount source/destination/RW triples are unchanged. Read-only Compose inspection separately verifies environment, published ports, networks and restart policies. Server StartUtc advanced to 17:31:47 UTC. Worker/Judge live brief content matches new embedded defaults; all other 25 template contents match before, including customized content. Harbor is connected/enabled; no native Harbor code-path update was required. No voyage was active during rollout. Evidence: `/tmp/armada-resumed-20261021/{build-proof,compose-config-preservation,prompt-and-harbor-rollout}.json`, `docmon/rollout-verification.json`, build logs `/tmp/armada-resumed-build-all-final.log`; evidence is private. The new Wilson run still requires full independent completion/verification.


Deployed-source CI `f027b8dc` passed: https://github.com/jchristn/Armada/actions/runs/39644167613 . The fresh planning failure is separate from this passing product regression run.


HF-34 remediation is implemented locally: generated Architect instructions use the concrete `requirements:["REQ-1"]` shape, forbid ID-plus-explanation entries and require exact case-sensitive reference matches. The parser still rejects the original malformed shape; it does not infer IDs, rewrite plans or loosen coverage. Diagnostics name each unknown ID once with the first criterion and corrective shape guidance. A failing-before/passing-after regression reproduces repeated bare references to a descriptive requirement string, verifies one actionable mismatch and no input mutation. Targeted parser/prompt/fan-out/routing/migration suites pass 176/176 on each net10/net8; fresh Release Rebuild with warnings as errors has zero warnings/errors (6.92s). Evidence: `/tmp/armada-plan-id-{before,net10,net8}.{json,log}` and `/tmp/armada-plan-id-rebuild.log`.

The archived rejected output had 11 slices, 33 decisions and 47 criteria. All 15 intended identifier prefixes had references; the mismatch was the declared string shape. That observation does not establish semantic plan completeness. Runtime remained on `f027b8dc` during this diagnostic cohort. Product source/configuration must be redeployed with the clarified instructions before a new clean cohort. The failed cohort's only recorded commit is baseline `8c3e8b5`, archived as `armada/acceptance/archive-frontier-v4-plan-id-20261021`; all eight missions and voyage read back 404 after deletion. No Wilson product code was edited by root.


#### Exact requirement-ID instructions deployed — 2026-10-21 17:50 UTC

Source `d4752eeb42710c34c6b08853309fa913a9204260`, 176/176 affected tests on each net8/net10 and clean warning-as-error Release Rebuild, completed build-all v1.1.0. Server manifest `sha256:a76d91083b920b7341a4dfcd3572d002c6a3d30817a8f73b27e907e6c580e7c8`; dashboard `sha256:a58bad9de3b123da297569ddcf626497afa01a6f31f8989a0142e810c1f7456d`; proxy `sha256:9c88635acbcdbe042f8a8c87c80204fcbc67e04f8b682273e2e8cd2f8775977c`. DocMon pull/up returned exitCode=0. Both running service digests match, exact bind triples and environment/ports/networks/restart policies match, all 27 template contents are preserved, and Harbor is connected/enabled. Server StartUtc 17:49:35 UTC. No voyage was active during deployment. Evidence: `/tmp/armada-plan-id-rollout-20261021`, `/tmp/armada-plan-id-build-all.log`. The live generated Architect instructions and next emitted plan remain the explicit next gate.


#### HF-35 — Pending RPC ownership audited separately

The lifecycle-job ownership guard does not cover ordinary host RPC results. `HarborConnectionManager` already records `_PendingOwners` to fail requests when their Harbor disconnects, but did not check it before removing/completing pending Git, dock, file or deferred-launch requests. Eight isolated fixtures (four message types × same-tenant other user / other tenant) failed: another connected Harbor that knows RequestId could supply the result. All eleven pre-existing reconciliation cases passed. This is in-process transport/database evidence, not observed live misuse or a claim of exploiting production jobs.

A local guard now validates the authenticated sending Harbor against `_PendingOwners` before sequence handling or pending callbacks. A rejected reply leaves the rightful request waiting; the requested Harbor can still complete it. Tests verify both rejection and the exact legitimate result. No wire fields changed. Evidence: `/tmp/armada-rpc-owner-before.{json,log}`, `/tmp/armada-rpc-owner-after-net10.{json,log}`. Final affected checks pass 78/78 on each net10/net8, and the sequential broad Services/Runtimes run passes 2,256/2,256 (234.989s). A fresh warning-as-error Release Rebuild has zero warnings/errors (6.44s). Commit/deployment follow; deployment remains deferred until the current cohort ends. The active v5 cohort remains on recorded `d4752eeb`; no mid-cohort rollout was performed. A local build/isolated fixtures ran during planning; stage wall times are observational and not isolated model benchmarks. Ordinary request-ID generation/reuse and other protocol message classes remain separate boundaries; do not claim total protocol isolation.


HF-35 validation correction: the first broad run overlapped a Rebuild of the same test output directory and reported persistent SQLite type-initializer failures. That run is invalid evidence; no product defect is inferred from it. The sequential rerun, after outputs stopped changing, passes all 2,256 cases without skips. Evidence: `/tmp/armada-rpc-owner-broad-verified-net10.{json,log}`, `/tmp/armada-rpc-owner-after-net8.{json,log}` and `/tmp/armada-rpc-owner-rebuild.log`.

V5 planning checkpoint: the live Architect instruction file contains the concrete identifier shape and exact-match guidance, and the emitted version 3 plan passed that gate without operator edits. It has 10 slices, 38 decisions and 41 named criteria, but collapses objective requirements to the example-like single identifier `REQ-1`. This proves matching shape, not detailed PM-to-plan traceability. HF-05 remains open. The first pinned Claude Worker completed the package slice and Usability is active; no Judge acceptance yet and no runtime redispatches observed. An independent semantic audit is in progress; do not claim comprehensive objective coverage from the structural pass.


#### HF-36 — REST stop bypasses the owner-aware lifecycle

Source audit found POST `/api/v1/captains/{id}/stop` constructing a fresh local runtime and stopping `captain.ProcessId` before calling owner-aware recall. This can reinterpret a remote Admiral handle/native Harbor PID in the server's local process namespace, bypassing the lifecycle safeguards. Two real loopback HTTP fixtures fail safely before the fix by instrumenting the local factory to throw/count creations; no real PID is touched. Remove that preliminary stop and let recall invoke the coordinated lifecycle exactly once. A successful exit releases the captain; an unconfirmed exit retains captain/mission identity and reports Conflict. The constructor's now-unused runtime-factory dependency is removed.

The expanded four-case HTTP fixture also reproduces stop-all returning success despite an unconfirmed recall (3/4 passed before correction). RecallAll now attempts every captain and surfaces incomplete recalls; the REST stop-all route continues planning/refinement stop attempts, collects failures, and cannot return all_stopped when any requested stop failed. The single-stop route explicitly maps an owner-recall InvalidOperationException to the existing Conflict error shape; bulk failures also return Conflict. An intermediate test exposed that throwing alone returns HTTP 500 here, so explicit route mapping is required. No REST fields changed; descriptions/Conflict metadata now state the intended behavior. Test seam evidence is real HTTP against isolated Admirals with an instrumented lifecycle, distinct from real Harbor process-kill evidence. Logs: `/tmp/armada-stop-route-before.*`, `/tmp/armada-stop-all-before.*`, and final targeted validation files. Add this HTTP suite to ordinary CI. No deployed captain-stop call was used while the bypass was present; the owned campaign was cancelled by the pending-stage voyage operation and its running verifier finished naturally.


#### HF-37 — Tenant-admin REST stop-all scope

The REST stop-all permission is TenantAdmin, but its implementation invoked global RecallAll and enumerated every planning/refinement session without caller filtering. A real loopback request authenticated with a newly created tenant-admin bearer credential stopped a different tenant's instrumented captain (expected Working, observed Idle); four stop-result cases passed and this fifth scope case failed. No real process was killed and no deployed stop-all endpoint was invoked. MCP already filters non-global callers; WebSocket command control is global-admin-only.

The local REST correction uses tenant-scoped working-captain enumeration for tenant admins and filters planning/refinement sessions to the caller tenant; global admin behavior remains global. The positive test verifies the own captain stops, the foreign captain remains Working, and foreign planning/refinement rows remain Active. Instrumented local runtime factories are installed before synthetic PID setup, including fixture failure/shutdown paths. Evidence: `/tmp/armada-stop-scope-before.{json,log}` and final verification files. Update stop API docs and the existing Postman request description; no new wire fields or roles. This is a focused real HTTP scope regression, not universal authorization certification.


#### Stop and scope batch — final local validation

Final behavioral source passes Services/Runtimes plus Captain/Workflow/CrossTenant real-loopback API suites: **2,415/2,415 net10** (188.312s), and **348/348 affected net8** (28.397s), no skips/failures. Final stop-route five-case real HTTP fixture passes with valid foreign user/vessel/objective/session records; an intermediate expanded fixture lacked its required vessel/objective foreign-key rows, which was corrected before the final runs. Synthetic PID factories are guarded before any such rows are created. Final Release Rebuild with warnings as errors passes with zero warnings/errors (6.41s). Commands/reports: `/tmp/armada-stop-batch-final-net{8,10}.{json,log}` and `/tmp/armada-stop-batch-release-rebuild.log`.

The sole source change after the behavioral runs was documenting stop-all's existing successful 200 response alongside its new 409 metadata; the API surface was regenerated and the final clean Rebuild passed afterward. Generated surface also catches up the pre-existing mission-diff 503 response; no new diff behavior was implemented. REST documentation and existing Postman stop request descriptions now describe owner coordination, partial failure and tenant scope. RPC fix 57f8e012 passed CI 39649895390. Current deployed image remains d4752eeb until the combined batch rollout. Follow deployment with a positively identified owned native stop probe (separate from the acceptance benchmark), then the full v6 request with marker/unmatched-fragment cases. No universal allocator identity, all-platform stop safety, semantic validator or structured blocker protocol claim is made.


#### Combined owner/stop/scope rollout — 2026-10-21 18:55 UTC

Source `e0753d8afb8da4705429d14efe4f4d28433369b2` includes RPC reply ownership from 57f8e012, owner-aware REST stop, incomplete stop-all Conflict reporting, tenant-scoped REST bulk control, real HTTP regressions, API/Postman docs and invariant/upstream-ID guidance. CI 39655560554 passed. Build-all v1.1.0 exited 0 without compiler warnings; final manifests: server `sha256:85d9c9c092a9df7169187d454cc11864e9272ecbc9ac6313b11aff7ade0c1dfd`, dashboard `sha256:6350f3447497c2704c6e43bc581b01d7d5feed1c36ddc3b054705746ab827a62`, proxy `sha256:47cc97f8123ea910ec992e72fe25efcedac3e869337f011dd4d2d1fd0bfaf25b`. DocMon pull/up returned exitCode=0; both services healthy, matching running digests, exact bind triples and environment/ports/networks/restart policy preserved. All 27 template contents preserved, Harbor connected/enabled, StartUtc advanced to 18:53:35. No acceptance job was active during rollout. No native Harbor restart was required for these Admiral-side changes. Evidence: `/tmp/armada-stop-owner-rollout-20261021`, `/tmp/armada-stop-owner-build-all.log`.

#### Real deployed native stop probe passed — separate diagnostic fixture

A dedicated temporary Claude captain with a unique persona fence and strict WorkerOnly assignment launched one explicitly non-editing control fixture. This was not a Wilson feature-acceptance run and does not count toward autonomous end-to-end success. Owning-Harbor read-only process probes confirmed the real CLI's exact model argument and assigned dock working directory. The fixture parent voyage was cancelled first (it leaves InProgress processes alive) to fence any recovery/replacement on uncertainty. The corrected deployed single-stop endpoint returned stopped after **10.710s**; an independent owner-host OS probe then found no remaining non-zombie process among the observed owned PIDs. A repeated stop returned success. Exactly one launch, zero RedispatchAttempts; mission ended Failed/OperatorAction, captain Idle with cleared process/mission assignment.

Source baseline was archived remotely as `armada/acceptance/archive-native-stop-probe-20261021-b20e5637ad`. All three temporary mission/voyage/captain resources were deleted and verified 404; production main stayed c9ff616f. Evidence: `/tmp/armada-stop-probe-20261021/{result,native-before,native-after,source-archive,events}.json`. This establishes one real REST/native owning-Harbor positive/repeated-stop case. Lost-stop/disconnect negatives remain isolated transport evidence; no live cross-Harbor collision, arbitrary DB-fault, universal descendant-process, platform or provider certification is implied.
