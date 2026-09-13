# Definition-of-done history

Mission completion now records every definition-of-done result as a scoped
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

The record also holds the captain, dock, branch, the mission commit hash when
known, the recovery attempt count, and start and end times. The failure output
passes through the shared secret redactor and keeps only its last characters,
4,000 at most including a truncation marker. On a long failure the gate's
leading actionable-diagnostics section can fall outside that tail; the complete
gate output remains in the mission `FailureReason`. A cancelled completion
records nothing.

A failed event write is logged with the outcome and exception type. It never
changes the gate outcome or the mission decision.

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
evaluation; the report does not read a diff. It does not list declared
consumers or per-edge trigger overrides.

`HistoryState` reads the latest two evaluation events for the mission in the
caller's scope (admin, tenant admin, or tenant and user):

| State | Meaning |
| --- | --- |
| `Recorded` | The latest record was read. `LatestEvaluation` holds it. |
| `NotRecorded` | No record in scope. Missions completed before this change have none. This is not a pass or a failure. |
| `Unavailable` | The latest record has no payload, is malformed, lacks its schema version, outcome or times, has another schema version, uses a name that is not an exact declared outcome or failure class (numbers, combined names and case variants are rejected), the two latest share a timestamp, or the history could not be read. An older record is never reported instead. |

The report never infers a result from mission status, `Complete`, or Check
runs.

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
