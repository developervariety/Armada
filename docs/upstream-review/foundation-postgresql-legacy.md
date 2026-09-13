# Historical PostgreSQL operational schema repair

Status: implementation and validation in progress. Foundation remains incomplete.

The deployment incident exposed source shapes missing from the generated upgrade
fixtures: 21 text timestamp columns on workflow_profiles, check_runs,
environments, releases and deployments; integer check_runs.duration_ms;
integer deployments.approval_required; and the captain foreign key on
objective_refinement_sessions with ON DELETE SET NULL.

The repair keeps the original prerequisite declarations, their checksum and all
historical migration rows unchanged. It runs under the same transaction as the
existing prerequisite validation and records a separate checksum only after that
validation succeeds. Existing tables are locked in a fixed order. Known text
timestamps require an explicit ISO offset and microsecond precision (extra
fractional zeroes are accepted); invalid values fail the transaction.
Only integer duration is widened. Approval conversion accepts only 0 and 1 with
the historical zero default. The captain constraint must have the expected
validated, single-column target and update rule before its delete rule changes.
Other incompatible shapes still fail the existing checks.

The first populated historical fixture failed before the repair at
workflow_profiles.created_utc. After repair it passed with 63 ordinary provider
persistence cases. The final historical scenario passed all 63 ordinary persistence cases after
checks for all 21 columns, invalid offsets/dates/precision, invalid approval
values/defaults, unexpected FK rules, rollback and repeat startup. Both pre-repair backups failed strict restore at
objectives_suggested_pipeline_id_fkey. Each contained four objective references
to absent pipelines. The owner authorized the choice of correction. In a new
isolated restore of the later 13:48 UTC backup, the four links were recorded and
cleared before the original foreign key was applied. This follows that key's
ON DELETE SET NULL behavior. The original backups and failed restores remain
unchanged. The corrected restore completed with ON_ERROR_STOP and all foreign
keys enabled. This is a documented test-data correction, not the schema repair
under test. Image validation is next. No source repair has been deployed by this session.

The native memory port is owned by another session. This repair does not alter
memory or learned-facts behavior, automatic dispatch, routing or landing gates.

## Combined validation

The combined run passed 4,071 unit, 960 API and 183 runtime tests with zero
failures or skips in 336 seconds. The final precision/constraint fixture was
validated separately against the final PostgreSQL binary. The solution build
passed with 122 warnings and zero errors (incremental, not a clean warning
census). The 274 protected migration declarations and 10 guard controls passed.

The first four-provider matrix passed 14 of 16 scenarios. SQL Server partial-52
and upgrade-51 exceeded the unchanged 90-second deadline during backup-restore
activity. Failed databases and logs were retained. Successful task-owned SQL
Server databases were removed before retry. Both retries passed under the same
90-second deadline: partial-52 in 13.38 seconds and upgrade-51 in 12.43 seconds.
Thus each of the 16 original provider/scenario combinations has a passing run;
the two timed-out attempts remain recorded.

The other session then landed the native memory port through `7abd94fe`. Its
implementation was preserved. The two shared scenario-registration conflicts
were resolved by retaining both scenarios. The combined tree passed all 17 provider/scenario combinations (four baseline
scenarios per provider plus the PostgreSQL historical guard scenario). It passed
4,106 unit, 967 API and 183 runtime tests with zero failures or skips in 353
seconds. The integrated solution build passed with 105 warnings and zero errors
(incremental). Two added process tests for database-validation mode passed
separately after that combined run.

## Image validation without dispatch

`dotnet Armada.Server.dll --validate-database` loads normal settings and runs the
same database driver initializer as server startup, then exits. It does apply
schema migrations and repairs. It starts no listeners, Admiral services,
scheduler or captains. Use an isolated restored database for deployment preflight.
Extra arguments fail before database initialization. The process tests verify
successful exit, no mission/captain creation and invalid-argument refusal.
