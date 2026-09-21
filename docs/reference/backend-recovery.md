# Mission recovery detail

`GET /api/v1/missions/{id}/recovery` reads the mission in the caller's scope and
returns `MissionRecoveryReport`. It is assembled only from recorded state. It
dispatches nothing, classifies nothing, and does not add a second recovery
coordinator. The autonomous recovery orchestrator, rescue budgets, incident
lifecycle and runbooks keep their existing behavior.

## Contents

| Field | Source |
| --- | --- |
| `Status`, `FailureReason` | The mission. The failure reason is redacted and bounded to 2,000 characters. |
| `RecoveryAttempts`, `LastRecoveryActionUtc`, `LandingRetryCount` | Counters on the mission, written by recovery and landing. |
| `MaxRecoveryAttempts`, `RecoveryBudgetExhausted` | The current `AutonomousRecovery.MaxMissionRecoveryAttempts`, which the orchestrator also reads at decision time. |
| `AutonomousRecoveryEnabled`, `DispatchRescueMissions`, `MaxLandingRetries` | Current settings, not a record of past settings. |
| `ParentMissionId`, `IsRescue` | Set only by rescue dispatch. |
| `Rescues` | Missions on the same vessel whose `ParentMissionId` is this mission, oldest first, at most 50. |
| `Incidents` | Incidents linked to the mission through the scoped incident service, most recently updated first, at most 50, each with at most 20 runbook executions from the scoped runbook service. Recovery notes are redacted and bounded. |
| `Events` | Recovery events among the mission's 100 most recent events: `autonomous_recovery.*`, `landing_drain.*`, orphan recovery, recoverable-work failure, retry requeue, restart and landing retry. Messages are redacted and bounded. |

## Scope

- Admin: unscoped.
- Tenant admin: the caller's tenant.
- Other users: the caller's tenant and user. Rescues are read per vessel in the
  tenant and then filtered to the caller's user.

Each section fails independently. A read error clears that section and sets its
`...UnavailableReason`; it never presents a partial section as complete.

## Limits

- A rescue's `Complete` status is not evidence that its work landed. Use landing
  evidence, not this report, for that question.
- `EventsWindowFull` means the 100-event window was full, so an older recovery
  event may exist that is not listed.
- The landing service writes `mission.landing_retry` in the mission owner's
  scope. Retry events written before that change carry no tenant or user and are
  visible only to an unscoped administrator; they are not backfilled.
- Events written through the generic server and admiral event helpers, the
  architect over-cap event and papercut events carry their owner's scope.
  Such events written before that change carry no tenant or user and are not
  backfilled. This report lists only recovery event types.
- A mission without a vessel lists no rescues and names the reason.
- Rescues are found by reading the vessel's mission summaries in scope and
  filtering by parent, the same lookup the recovery orchestrator uses. There is
  no query by parent mission, so the read grows with the vessel's mission count.
  Incidents are filtered in memory by the existing incident service.
- Titles and free text are redacted and bounded to 2,000 characters.

## Validation

Unit cases cover recorded counters and budget exhaustion without mutating the
mission, rescue listing and order for admin, tenant admin and an ordinary user,
another tenant seeing nothing, the no-vessel reason, incidents with a real
runbook execution and redacted notes across tenants, recovery event filtering
and redaction per scope, and the full event window flag.
