# Definition-of-done history

Mission completion records every definition-of-done result as a scoped
event. A read-only mission report shows the current gate configuration and
the latest recorded result. Neither changes when the gate runs, what it
decides, or landing readiness.

## Recorded evaluation event

Event type: `mission.definition_of_done_evaluated`. The event carries the
mission tenant and user, mission, captain, vessel and voyage identifiers. The
payload is a `DefinitionOfDoneEvaluationRecord`, schema version 1:

| Outcome | When |
| --- | --- |
| `Passed` | The gate ran its required commands and they passed. |
| `Skipped` | The gate does not apply: disabled, persona not applied, or doc-only marker. A skip is not evidence that a build or test ran. |
| `NotVerifiable` | The mission changed nothing, so the commands would measure the base commit. The gate did not run. |
| `Failed` | A required command failed. Label, exit code, failure class and output tail are recorded. |
| `EvaluationError` | The gate threw. Only the exception type is recorded, not its message. |
| `Cancelled` | The mission was cancelled while the gate ran or waited for the host-wide command slot. The gate's command process was stopped and the slot released. No build or test result exists. |

The record also holds the captain, dock, branch, the mission commit hash when
known, the recovery attempt count, and start and end times. The failure output
passes through the shared secret redactor and keeps only its last characters,
4,000 at most including a truncation marker. On a long failure the gate's
leading actionable-diagnostics section can fall outside that tail; the complete
gate output remains in the mission `FailureReason`. A completion stopped by
server shutdown records nothing.

Cancelling a mission (the operator cancel, or a status transition to
`Cancelled`) stops a gate still running for it: the gate's command process
group is killed, the host-wide command slot and dock lease are released, and
completion records `Cancelled`. The mission keeps the `Cancelled` status the
cancel wrote; it is not failed, handed off or landed.

A failed event write is logged with the outcome and exception type. It never
changes the gate outcome or the mission decision.

## Handoff requires a recorded result

Mission status is not evidence that the gate passed: completion writes
`WorkProduced` before the gate runs. A completion interrupted after that write,
for example by an admiral restart while the gate waits for the host-wide slot,
leaves a `WorkProduced` mission with no gate result. The lazy handoff that
dispatch runs for a dependent whose upstream was never handed off, and the
dangling-handoff recovery, therefore read the upstream's latest evaluation
event first. They hand off only when that event is newer than the upstream's
current launch (`StartedUtc`) and its outcome is `Passed`, `Skipped` or
`NotVerifiable`. Otherwise the upstream is held for operator review with a
`definition_of_done_not_run:` reason and its dependents keep waiting. The
operator clears the hold with `armada_review_hold` to accept the work as it
stands, or fails it to send it to recovery. This applies only when a gate is
wired.

The server builds the gate only at startup, and only when
`DefinitionOfDone.Enabled` is true. With no gate, completion records no
evaluation event. A settings reload replaces the `DefinitionOfDone` settings
object but not the gate: the running gate keeps the settings it was built
with, and enabling or disabling DoD takes effect only after a restart.

## Mission report

`GET /api/v1/missions/{id}/definition-of-done` reads the mission in the
caller's scope and returns `MissionDefinitionOfDoneReport`.

`Configuration` describes the gate mission completion actually uses, not the
current settings file. With no gate wired, `GateActive` is false, `Enabled` is
false and `ExpectedSkipReason` says completion records no evaluation. With a
gate, `DefinitionOfDoneGate.DescribeAsync` runs against that gate's own
settings. It uses the same skip rule and workflow-profile resolution as
evaluation: active profiles
for the mission tenant, vessel scope, then fleet, then global, with the default
profile first in each scope. It reports whether commands exist, never the
command text. It reports consumer settings and the global trigger prefixes.
Whether consumer tests run for a change is decided from the producer diff at
evaluation; the report does not read a diff. When evaluation cannot read the
producer's changed paths, it treats the change as reaching every trigger: each
consumer with trigger prefixes runs its suite, and the gate logs the
`changed_paths_unavailable` reason. An unreadable change is never treated as an
empty change. It does not list declared
consumers or per-edge trigger overrides.

`HistoryState` reads the latest two evaluation events for the mission in the
caller's scope (admin, tenant admin, or tenant and user):

| State | Meaning |
| --- | --- |
| `Recorded` | The latest record was read. `LatestEvaluation` holds it. |
| `NotRecorded` | No record in scope. Missions completed before this change have none. This is not a pass or a failure. |
| `Unavailable` | The latest record has no payload, is larger than 262,144 characters (checked before it is parsed), is malformed, lacks its schema version, outcome or times, has another schema version, uses a name that is not an exact declared outcome or failure class (numbers, combined names and case variants are rejected), has a negative recovery attempt count, combines an outcome with fields the writer never produces, the two latest share a timestamp, or the history could not be read. An older record is never reported instead. |

The report never infers a result from mission status, `Complete`, or Check
runs.

### Stored-record validation

Only mission completion writes this event; no REST route or MCP tool creates
events. The rules below therefore reject rows the writer cannot produce, such
as a damaged row or a manual edit, and each rejection names its reason.

| Outcome | Required | Rejected |
| --- | --- | --- |
| `Passed` | — | a skipped reason, command label, exit code, failure class or output tail |
| `Skipped`, `NotVerifiable`, `Cancelled` | a skipped reason | a command label, exit code, failure class or output tail |
| `Failed`, `EvaluationError` | a command label | a skipped reason |

The skipped reason and command label pass through the shared secret redactor
and keep their first 1,000 characters, a truncation marker included. The same
rule applies when the record is written and when it is read, so an older row
is returned with the same bound. The output tail keeps its existing 4,000
character bound. Captain, dock, branch and commit identifiers are never
truncated; the payload size limit is far above the writer's largest record
with full-length Unicode identifiers.

A reversed start and completion time is still `Recorded`. A clock step during
evaluation can produce it on a real result, and hiding that result would be
worse than showing its times. This is an accepted limit.

## Validation

- A skipped completion records a `Skipped` event with the disabled reason. The
  mission still reaches WorkProduced.
- A real enabled gate with no workflow commands records `Failed` with
  `missing-commands` and `Infra`. The mission still fails.
- Missing, malformed, unsupported-version, unknown-outcome and same-timestamp
  cases read `NotRecorded` or `Unavailable` as above. A malformed newest
  record hides an older pass.
- Tenant B cannot see tenant A's record.
- Vessel profile selection wins over global, and command text and secrets
  from both profiles are absent from the serialized report.
- Persona and doc-only skips are reported without running a gate.
- Failure output is redacted and bounded.
- API: the owning tenant reads the report, another tenant receives 404, and
  an unauthenticated caller receives 401.

No migration: the history uses the existing events table. No deployment is
included.
