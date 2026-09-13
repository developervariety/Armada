# Recorded mission admission

Mission details and lightweight mission summaries expose `LastAdmissionObservation`.
It is the last recorded global workload and resource-pressure evaluation. It is
not a claim that the mission can launch now. A GET does not run the policy or
probe. Missing, malformed, oversized, unknown-version or contradictory evidence
is unavailable. Existing mission scope rules apply.

The existing dependency and sibling-lane gates run first. The global workload
limit still runs before resource pressure. A global refusal records no pressure
decision because the probe did not run. Other observations preserve the actual
policy decision, including nullable evidence from custom implementations. Limits,
OOM cooldown, reserves and captain selection retain their existing behavior.

Observations are server-written. Their mission, tenant, user and vessel must
match the loaded row. Reasons use shared secret redaction on write and read;
each reason is bounded, and the stored JSON is limited to 64 KiB to accommodate full Unicode IDs and escaped reasons. No provider
account collection is added.

A database revision protects against stale snapshots, including a mission that
changes and returns to its earlier state. Generic mission updates, heartbeats
and observation writes advance this revision. The conditional write also checks
status, assignment state, timestamp, prior JSON and exact ownership values.
A refusal records WaitingForResourcePressure in that same write. It cannot
assign a captain or change process identity. Generic updates cannot restore an
old observation from a client object; ownership changes clear stored evidence.

New migrations are SQLite 87, PostgreSQL 88, MySQL 79 and SQL Server 82.
They append nullable evidence and a revision counter after native memory storage.
Previously accepted migration declarations remain unchanged. Native memory
implementation is outside this change.

## Validation

The initial storage regression failed on all four providers before implementation.
The service refusal regression failed before its decision was persisted.

Twenty migration runs passed: fresh installation, the dedicated admission
upgrade/restart case, interrupted first migration, interrupted migration 52,
and upgrade from migration 51 on each provider. Prior migration rows are retained.

The long-Unicode case failed on each provider with the initial 16 KiB bound.
The 64 KiB implementation then passed on the same four databases after reopen.
The final ordinary totals are 67 SQLite, 67 PostgreSQL, 68 MySQL and 67
SQL Server cases, with no failures. They include storage, summary, stale-write, concurrent-write,
heartbeat, ownership, redaction and invalid-evidence checks.

Final combined validation, including the later consumer DoD change, passed
4,112 unit, 967 API and 183 runtime tests with no failures or skips. The solution
build completed with 210 warnings and no errors. All 274 protected migration
declarations and the ten migration-guard controls passed.

An earlier API run had one voyage-purge failure without a diagnostic response.
The test now asserts the purge response before checking deletion. It passed in
two later complete runs. The original cause remains unconfirmed; this change
does not claim to repair voyage purge. Failed-run evidence is retained.

No production deployment is included. Effective landing, DoD and recovery detail
projections remain separate backend work.
