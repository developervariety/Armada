---
topic: "Recovery And Incident Workflow"
summary: "How failed missions are classified, rescued or blocked, how incidents open and close, and how a stranded branch is preserved."
read_when: "A mission or voyage failed, or an incident needs driving to closure."
applies_to: orchestrator
tier: leaf
---
# Recovery And Incident Workflow

Objective closeout evaluates each failed chain in an original voyage. Every
independent `Failed` or `LandingFailed` chain root needs a linked automatic
rescue whose missions all reached `Complete`. A completed rescue for one branch
does not cover a separate failed branch. Failed rescue attempts stay as history
and do not become new closeout obligations; a later completed attempt can
resolve the original failure. A missing linked voyage, a malformed mission
graph, unlanded work, or a cancelled-only original voyage keeps the objective
open.

When autonomous recovery is enabled, Armada classifies failed missions,
creates or updates an incident, records a recovery runbook execution, and can
dispatch a bounded rescue mission. It does not use generic rescue missions for
landing failures. Authentication, quota, review, protected-path, dependency,
and exhausted-recovery failures remain for operator action. While the dispatch
hold is engaged, a rescue is deferred and named on the incident, not
dispatched (section 8.18).

A definition-of-done gate failure records its class in the failure reason
(`DoD gate failed: classification=<Class>; ...`). Recovery reads that class.
`Infra` (no workflow commands, restore errors, a missing SDK or .NET runtime, a
dead container runtime, a crashed or exited test host) and `Timeout` do not
dispatch a rescue: a rescue re-runs the same commands on the same host. The
incident records a blocked policy that names the class. `Compile` and
`TestFail` dispatch a rescue as before. A failure reason with no recorded class
follows the other rules in this section.

When a `TestFail` failure is recorded, the gate parses the failing test
identifiers from the still-whole runner output into an ordered, de-duplicated
set and stores it on the evaluation record, with an overflow flag set when the
runner named more distinct tests than the set could keep. Recovery uses this set
deterministically: when a rescue (a mission with a parent) fails on `TestFail`
and both the rescue's and the parent's stored sets are complete (not
overflowed), non-empty and identical, the failure did not change, so no further
rescue is dispatched. The blocked decision reads `repeated_identical_test_failure`
and the incident names the repeated tests. An empty, overflowed, unknown or
differing set keeps the earlier behaviour, and the hard blocks above (recovery
budget, policy refusal, read-only mode) still win. This is the deterministic
fallback the typed foreign-test decision sits on top of.

Recovery never selects a mission whose voyage is `Cancelled`. When a captain
process exits with a genuine, non-recoverable failure and no committed work,
the admiral halts (cancels) the voyage, so the process-exit path itself opens
one High incident for the mission (`mission.failed_incident_opened`). It carries
the failure reason as its root cause and says that no rescue will run. An
active incident already linked to the mission is kept, not duplicated.

A mission that no captain of its tenant can ever serve (no captain allows its
persona, or none that does serves its tier) is not waiting for capacity. The
first assignment pass that finds this records one
`mission.unassignable_by_construction` event with the reason. If the mission is
still unassignable after 10 consecutive assignment passes, one High incident
opens. Busy, benched or quarantined captains that could serve the mission do
not count as unassignable. The mission itself is not changed: give a captain the
persona and tier, or cancel and re-dispatch at a tier a captain serves.

The recovery incident is linked to each objective that already owns the failed
mission or its voyage. This annotation preserves the objective's existing
mission and voyage lineage. It does not add unrelated incident context to the
objective or change its lifecycle state.

Audit and Research failures remain read-only. Recovery records the mission
mode and audit-only scope in the incident, runbook execution, and event, then
stops without an Implementation rescue. A failed Judge also creates one
durable Judge follow-up for its recommendation. Other review stages, including
PortingReferenceAnalyst and TestEngineer, do not create Judge follow-ups. The
system updates an older recovery runbook in place to add the required
`missionMode` parameter and preserves its existing content.

Use this order for manual diagnosis:

1. Read the complete mission and captain logs.
2. Use `armada_captain_diagnostics`.
3. Compare with a known-good mission on the same runtime.
4. Separate provider, host, repository, test, and model failures.
5. Create or update the incident before repeated intervention.
6. Cancel only the exact mission or voyage that must stop.
7. Restart or retry only after the cause is understood.

A mitigation acknowledges failure evidence at or before its recorded time.
Lifecycle reconciliation does not reopen a mitigated or monitoring incident
from that same old failure when both timestamps are available. A newer failure
can reopen it.

Incident closure is evidence-driven. Produce a newer passing check, successful
rescue, shipped release, verified deployment, or completed rollback. Do not
close an incident only because a captain reported success.

The lifecycle sweep reads only incidents that are not Closed or RolledBack. It
takes at most `incidentLifecycle.maxIncidentsPerSweep` of them per sweep,
oldest update first. Each sweep resumes after the last incident the previous
sweep evaluated and wraps to the oldest at the end. Terminal incidents never
take a sweep slot, so every open incident is evaluated within one full pass.
The cursor is held in memory and restarts from the oldest after an admiral
restart.

### A rescue of a stage inside a voyage re-enters review

The rescue root first uses the failed mission's produced commit. If no commit
was produced, Armada keeps the mission's original `StartFromRef`. If both are
absent, Armada can use the immediate dependency commit only when both missions
use the same vessel. Armada resolves this ref before provisioning and proves
that the new checkout contains it before the captain starts. A missing
ref, a false ancestry result, or an ancestry result that Armada cannot verify
fails closed as a provisioning fault. If a reviewer rescue has no captured
commit, original start ref, or same-vessel dependency commit, Armada leaves the
incident open and does not start a Worker from the vessel default branch. Later
rescue stages inherit the verified Worker branch.

A Worker that fails its gate inside a voyage has already cost that voyage its
TestEngineer and Judge: the pipeline cancels them as blocked dependents when the
Worker fails. Its rescue is therefore dispatched as a rescue VOYAGE — the Worker
revision, then a TestEngineer where the vessel pipeline defines one, then a
Judge — exactly as a Judge rejection is, and with Build and UnitTest Checks
armed. Before this rule a Worker gate failure produced a STANDALONE rescue
mission that passed its own gate and landed through `LocalMerge` with no
reviewer ever reading the final code. A standalone mission that has no voyage
never had review stages and keeps a standalone rescue.

### A captain's first terminal marker ends its stage

A captain that prints its terminal marker and keeps its process running has
finished. Before this rule it looked exactly like a captain still working,
because Armada read the verdict only at process exit. The stall nudge then
asked the finished reviewer to continue, and each nudge started a full new
review.

The first `[ARMADA:VERDICT] PASS|FAIL|NEEDS_REVISION` or
`[ARMADA:RESULT] COMPLETE` line in the streamed output is now recorded. If the
process has not exited `autonomousRecovery.terminalMarkerGraceSeconds` after
that line (default 60, clamped to 5-3600), the lifecycle handler stops it and
records `captain.terminal_marker_stop`. The handler owns that stop, so the
exit completes the stage from the recorded output as a clean exit, whatever
exit code the stop produced.

While the marker is recorded, the recovery sweep sends no stall Mail nudge to
that mission. It logs and counts every withheld nudge and records one
`autonomous_recovery.mail_nudge_suppressed` event that names the marker.
The recorded Judge verdict is the first canonical `[ARMADA:VERDICT]` line. A
later verdict from a re-review cannot replace it. Output with no canonical
line falls back to the runtime's `[verdict]` echo or a labelled verdict.

### A completion is de-duplicated per launch

The process-exit callback and the health check can both report the same
agent exit. The completion handler therefore skips a repeat completion for
the same launch of a mission for 30 seconds after it handled the first one.

A launch is the mission's start time plus its agent process. A completion for
a later launch is processed even inside the 30 seconds. That holds for every
path that returns a mission for another attempt, because each one ends in a
new launch:

| Path | Where it requeues | Guard |
| --- | --- | --- |
| Judge PASS held while Checks resolve | completion handler, in-place re-run | released by the next launch |
| Judge exited with no verdict | completion handler, in-place re-run | released by the next launch |
| Refusal in the captain output | completion handler, continuation on another runtime | released by the next launch |
| Provider safeguard block on exit | process-exit failure, continuation | released by the next launch |
| Probable resource kill or captain unavailable | process-exit failure, transient requeue | released by the next launch |
| Runtime-reported interruption (exit code -1) | process-exit handler, interrupted re-dispatch | released by the next launch |
| Provider quota, credit or spend limit | process-exit failure, re-route | released by the next launch |
| Operator restart | restart of a Failed, Cancelled or LandingFailed mission | released by the next launch |
| Review denied with retry | review decision | released by the next launch |
| Merge failure routed to redispatch | merge recovery | released by the next launch |
| Captain process gone | stale-captain cleanup | released by the next launch |
| Stall recovery | relaunch in place (same start time, new process) | released by the new process |

Assignment rollbacks (Assigned back to Pending when a launch fails) and the
orphan reset of a mission that never started a process return a mission that
was never launched, so no completion for it exists to de-duplicate.

A completion for a requeued mission that has not been launched again is a
late duplicate and is still skipped. Look for the handler's log line naming
the new launch when a completion inside the window is processed.

### An interrupted run is re-dispatched within a budget

A runtime reports a cancelled run as exit code -1. A stop, a shutdown or an
Admiral restart causes it. Every other code is a failure, including other
negative values: a native crash status reads as a large negative number on
Windows and on Harbor runners. The process-exit handler treats -1 as an
interruption:

1. An explicit `[ARMADA:RESULT] COMPLETE` still wins and completes the stage.
2. A Cancelled mission is not re-dispatched.
3. When the mission has fewer `mission.interrupted_redispatched` events than
   `maxInterruptedExitRedispatchAttempts` (default 2, 0-10), it returns to
   Pending with its captain and dock released, the captain goes to Idle, the
   voyage keeps running, and one more event is written. Its `FailureReason`
   names the exit code and the attempt.
4. Otherwise the exit fails the mission through the normal terminal path.

The mission is never Failed while it is re-dispatched, so autonomous recovery
opens no incident or rescue for that exit. The budget is separate from the
rescue budget. Deleting a mission's `mission.interrupted_redispatched` events
resets its count.

The health check is not an interruption source. When it cannot find a
captain's process it reports `-1`, but that process may have exited cleanly
after its exit record was pruned, so the health check keeps the failure path.

### A rescue brief keeps the reviewer's instructions, not only its diagnosis

The rescue brief embeds the failed mission's reviewer feedback under a size cap.
The cap exists because an uncapped gate log once tripped a provider's content
filter and opened a self-perpetuating loop; it stays. What changed is the shape
of the cut. A Judge report is written diagnosis first and instructions last
(Completeness, Correctness, Tests, Failure Modes, Suggested Follow-ups,
Verdict, then the `[ARMADA:VERDICT]` line), so a head-first cut kept the table
of what failed and dropped every line that said what to do. Four rescues in one
shift each needed an operator to re-send the tail by Mail.

An over-cap Judge report now keeps `## Suggested Follow-ups`, `## Verdict` and
the verdict line whole, fills the remaining budget from the head, and places
the truncation marker where the omitted middle was, naming the sections it
dropped. An under-cap report is embedded whole. Text without those headers, such
as a gate log, keeps the head-first cut, because its signal is at the top. When
a rescue brief still reads `truncated`, the marker names what is missing; the
full report remains on the failed mission's `ReviewComment`.

### A rescue is judged by what it changed

A rescue that starts, logs, and exits satisfies every liveness measure Armada
keeps: the process lived, the captain reported, the pipeline advanced. None of
that says the defect was touched. One rescue ran for twenty-four hours, drew
escalating stall nudges, died on a runtime crash, and left behind a single
changed documentation file.

An Implementation-mode rescue whose change set is empty, or consists only of
documentation, now fails with `ineffective_rescue` and the change set named in
the `FailureReason`. Read that field before retrying: the correct response is a
replacement brief that quotes the defect, not another attempt at the same one.

Three limits keep this from firing on correct work:

- **Only rescues are assessed.** A first-attempt mission may legitimately have
  been dispatched to write documentation.
- **Only Implementation mode.** An Audit or Research mission delivers a report
  and is never expected to change code. Judging those by a diff is the same
  mistake in the other direction, and it once marked correct work Failed.
- **The objective kind declares the deliverable.** A `Research` objective is
  report-only, so its rescue is not assessed. A `Chore` objective delivers a
  committed document, such as a report under `docs/audits/`, a
  `discoveries.d` record or a census. Its rescue is effective when it commits
  any change, documentation included, and ineffective only when it commits
  nothing. `Feature`, `Bug`, `Refactor`, `Initiative` and rescues with no
  linked objective owe a change that can carry behavior. A committed-document
  deliverable must therefore be filed as `Chore`, not as a code kind.
- **The original mission's paths are NOT compared against.** A rescue is
  expected to rewrite the prior branch from scratch over the same files, so
  treating an overlapping path set as a no-op would flag the normal case.

Documentation means a `.md`, `.txt`, `.rst`, or `.adoc` file, anything under a
`docs/` directory at any depth, or a conventional bare name such as `README` or
`LICENSE`. A directory that merely contains the letters "doc" - `docker/`, a
`DocumentStore/` source tree - is not documentation.
