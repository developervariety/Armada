# Historical PostgreSQL operational schema repair

Status: source repair landed at `7c71cc6b` and isolated image validation passed.
The upstream campaign remains incomplete. Production rollout is separate.

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
under test. The isolated image validation below passed. No source repair has been deployed by this session.

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

## Corrected-restore image proof

The owner authorized the choice of correction after both original backups failed
strict restore. The 13:48 UTC backup was restored into a new isolated PostgreSQL
database. Exactly four missing-pipeline references were recorded and set to NULL
before the original foreign key was applied. No original backup or live row was
changed. Strict restore then completed successfully.

Release output from source `7c71cc6b` was placed in a separate image using the
same runtime base as the current deployment. Image digest
`sha256:d724d5691b085b96490c4b5b4209719cd0ea7752cfab3fe0de8b4509b4e28db4`
passed `--validate-database` twice and exited zero at schema 87. No production
container was replaced. The test image did not start Admiral services.

Normalized row counts and SHA-256 fingerprints matched before and after for
all common columns in workflow_profiles (13 rows), check_runs (3,291),
environments (24), releases (0), deployments (2), objectives (1,421), pipelines
(14), captains (18) and docks (5,133). The comparison normalizes the documented
four objective links and the intended timestamp/boolean types. Thirteen mission
identity/state fields matched for 6,930 rows; heavy mission payloads were not
included. All 68 historical migration rows through version 83 matched exactly.
This is scoped persistence evidence, not a certification of every runtime path.

Final ordinary provider runs on the combined native-memory tree passed
65 SQLite, 65 PostgreSQL, 66 MySQL and 65 SQL Server cases. The 17-scenario matrix,
combined suites and process tests above passed. Rollback-image build automation
and the durable deployment lesson remain separate incident follow-ups.
