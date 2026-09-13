# Mission auto-land detail

`GET /api/v1/missions/{id}/auto-land` reads the mission in the caller's scope and
returns `MissionAutoLandReport`. It is built only from recorded state. It does
not evaluate the predicate, read a diff, or infer a decision from mission or
merge entry status. The landing handler, `AutoLandEvaluator`, calibration and
the merge queue keep their existing behavior.

## Contents

| Field | Source |
| --- | --- |
| `PredicateConfigured`, `CurrentPredicate` | The vessel's current `AutoLandPredicate`, read in the caller's scope. An unparsable or unreadable predicate is not shown and names the reason. |
| `DecisionState`, `LatestDecision` | The newest `merge_queue.auto_land_triggered` or `merge_queue.auto_land_skipped` event for the mission in scope: outcome, merge entry, redacted and bounded skip reason, and the predicate recorded at decision time. |
| `LatestMergeEntry` | The newest merge entry for the mission in scope with status, audit lane, convention result, critical triggers, deep-review pick, verdict and completion time. |

The outcome reports which event was recorded, not a predicate result.
`Triggered` means processing was started; the merge entry can still sit in the
deferred deep-review lane. `Skipped` means the entry was not processed
automatically, and the reason says why: a failed predicate, an unavailable diff,
or a landing-drain safety-net hold during calibration or after a critical
trigger, which the safety net records as skipped even when the predicate passed.

`DecisionState` is `Recorded`, `NotRecorded` (no decision in scope) or
`Unavailable`. A missing or malformed payload, or two latest decisions that share
a timestamp, reads `Unavailable`; an older decision is never reported instead.
The DoD report uses the same shared `RecordedHistoryStateEnum`.

## Scope and history limits

- Admin: unscoped. Tenant admin: the caller's tenant. Other users: tenant and user.
- Auto-land events and merge entries carry the mission owner's scope only from
  the landing record scope change onward. Earlier records have no tenant or user
  and are visible only to an unscoped administrator; they are not backfilled.
- The current predicate is today's configuration, not the configuration at
  decision time; use `PredicateAtDecision` for the recorded one.
- The report does not certify that the entry landed. Merge entry status and
  landing evidence remain the authority.
- A merge entry carries a tenant only when the mission and vessel share it, while
  its events carry the mission owner's scope. A scoped reader can therefore see
  a decision whose merge entry is not visible; `MergeEntryUnavailableReason`
  then says so. Two newest merge entries with the same creation time are
  reported as unavailable rather than picked arbitrarily.

## Validation

Unit cases cover the newest decision winning over an older one, redaction of the
reason, the recorded and current predicate, merge entry audit fields, no
decision, a malformed newest decision hiding an older one, tied decisions, an
unparsable predicate, an unscoped legacy event invisible to a scoped reader but
visible to an admin, and another tenant seeing neither the predicate nor the
decision. A unit case reads a decision written by the real landing-drain safety
net. A unit case with two missions in one tenant proves each report shows its own
merge entry for an admin, a tenant admin and a member. An API case covers owner
access, cross-tenant 404 and unauthenticated 401.

The two-mission case exposed a provider defect: the SQLite and SQL Server scoped
merge entry reads (tenant, and tenant plus user) ignored the mission, vessel and
status filters, so a scoped reader got another mission's newer entry. They now
apply the same filters as the unscoped read and the PostgreSQL and MySQL
providers. The database runner case `MergeEntry_Scoped_Enumerate_Filters_By_Mission`
covers all four providers.
