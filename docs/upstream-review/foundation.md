# Fork preservation and provider foundation

Evidence date: 2026-09-13 UTC. This is an implementation checkpoint, not
four-provider certification. The foundation remains open. No production
migration or runtime contract changed in this checkpoint.

## Source boundary and migration ownership

Both remotes were fetched. Upstream remains at the pinned `19242085`.
The implementation baseline is `24a23b966`, which includes Routing V2,
standalone lead retirement and the incident mitigation guard. The original
path census remains pinned to fork `21786ec0`, upstream `19242085` and base
`e9e3021f`. Its path and commit counts were checked again, including separate
vendor, built-output and archive exclusions. This is a path/capability census,
not a review of every line or proof of runtime behavior.

[The migration manifest](fork-migrations.json) records every declared version,
its existing description (semantic owner), source path and token digest. Its
fixed baseline is the full commit ID in `baselineCommit`. All 274 declarations
also match the original fork review commit.

| Provider | Declarations | Maximum | Minimum new version at this checkpoint |
| --- | ---: | ---: | ---: |
| SQLite | 82 | 82 | 83 |
| PostgreSQL | 68 | 83 | 84 |
| MySQL | 62 | 74 | 75 |
| SQL Server | 62 | 77 | 78 |

These are lower bounds, not reserved numbers. Re-read the current tree before
each addition. Each driver selects work above `MAX(version)`; a gap below the
maximum is not available. Upstream reaches 70 with conflicting owners. For
example, upstream 70 adds memory, while fork SQLite 70 adds coordination leases.
Never replay upstream history or renumber a fork migration.

Run the source gate before each port:

```sh
python3 scripts/common/verify-fork-migrations.py
python3 scripts/common/test-verify-fork-migrations.py
```

The gate rejects changed, removed or reused historical declarations. It also
protects the initial SQL and statement assembly referenced by MySQL and SQL
Server. Comments and C# spacing outside string literals do not affect hashes;
SQL string contents do. Nine controls cover the current tree, changed SQL,
removed history, a reused gap, accepted append, and both providers' referenced
SQL/assembly. The pinned upstream tree is rejected. This is a source check,
not a SQL parser, migration-runner check or security boundary. It does not
protect unrelated service behavior. Keep the reviewed manifest fixed; do not
regenerate it to accept a port. `--write-manifest --ref <fixed-commit>` is for
an explicitly reviewed baseline change only.

Accepted schema work must use this procedure:

1. Map the model field through schema, create, update, row mapping and every
   relevant query. Establish a failing non-default test before repair.
2. Add new feature SQL above the current provider maximum. Leave historical
   bodies and their referenced inputs unchanged.
3. Use provider catalog checks for equivalent existing columns. Check type,
   nullability, default and required index/constraint semantics, not only the
   name. Accept an equivalent definition; reject an incompatible definition
   with a clear error. Do not hide errors with a broad catch.
4. Prove fresh install, representative fork upgrades, repeat startup and
   non-default create/update/reopen/query behavior on each provider. Include
   interrupted migration recovery and existing equivalent/incompatible columns.
5. A failure before the new migration needs a separately reviewed startup or
   repair path. A later version cannot repair an earlier startup failure by
   itself. Do not alter historical SQL to conceal this problem.

## Preserved behavior gates

Keep the existing runners and registrations. The following tests identify
current protection; they are not an exhaustive case map. Test-discovery work
must produce that map before replacing a runner.

| Contract to preserve | Existing evidence and next required gate |
| --- | --- |
| Scheduling and capacity | `AutonomousObjectiveSchedulerTests`, `SchedulerCapacityTests`, `SchedulerHydrationTests`; preserve bounded sweeps, fairness, limits and pause ownership. |
| Objective preparation | `ObjectivePreparation` source paths and database `preparation_json`; preserve source/target claims, invalidation and dispatch requirements. Complete four-provider field mapping. |
| Pipeline graph | `PipelineDispatchTests`, `AliasPipelineDispatchTests`, `PersonaPipelineDbTests`; retain dependencies, stage order, immutable source refs and sibling inputs. |
| Immutable Check and landing | `ArmedCheckEligibilityTests`, `CheckRunIsolatedCheckoutTests`, `LandingPipelineTests`; preserve armed ref, required checks and landing evidence. |
| Recovery and incidents | `AutonomousRecoveryOrchestratorTests`, recovery suites, `IncidentLifecycleOrchestratorTests`; retain full recovery pipeline and the mitigation-time guard. |
| Sibling lanes | `SiblingLaneAdmissionTests`, `SiblingLeaseRegistryTests`; retain leases and admission evidence. |
| Coordination and wakes | `CoordinationClaimTests`, `CoordinationServiceTests`, coordination database tests; retain ownership and directed generic wakes. Do not restore retired lead or Grok paths. |
| Routing and provider limits | `UsageRoutingTests` and provider-routing suites; retain approved accounts, explicit persona/model constraints, window mappings, stale/unknown handling, reserves, concurrency and disabled default. Live account collection needs separate proof. |
| Process ownership | Runtime suite, `ProcessExitFailureReasonTests` and quarantine service; manual quarantine must protect a live process and its assignment. |
| Output evidence | `MissionOutputArtifactTests`, `RuntimeOutputFormattingTests`; retain structured output, redaction and artifact provenance. |
| Official MCP transport | `ArmadaMcpHttpServerTests`, `McpStdioServerTests`, production-tool suites; retain SDK and provider injection. Auth/scope additions need negative credential tests. |
| Deployment | `DeploymentServiceTests`, `DeploymentEnvironmentServiceTests` and current delivery guide; retain backup, health, rollback and build provenance. Source tests do not prove a running image. |

## Mission field matrix

This first detailed field slice covers Mission additions at risk from smaller
upstream copies. It does not complete the field census for all entities.
Sources are `src/Armada.Core/Database/<provider>/Queries/TableQueries.cs` and
`Implementations/MissionMethods.cs`. PostgreSQL and SQL Server also map rows in
their database driver. MySQL declares migrations in `MysqlDatabaseDriver.cs`.

`S/C/U/R/Q` means schema, create, update, read and full scoped query. `Yes`
means the path is present in source, not that the provider runtime test passed.

| Field | Column | SQLite S/C/U/R/Q | PostgreSQL S/C/U/R/Q | MySQL S/C/U/R/Q | SQL Server S/C/U/R/Q |
| --- | --- | --- | --- | --- | --- |
| TenantId | tenant_id | Yes | Yes | Yes | Yes |
| UserId | user_id | Yes | Yes | Yes | Yes |
| CaptainId | captain_id | Yes | Yes | Yes | Yes |
| AssignmentState | mission_assignment_state | Yes | Yes | Yes | Yes |
| ProcessId | process_id | Yes | Yes | Yes | Yes |
| Persona | persona | Yes | Yes | Yes | Yes |
| RequestedCaptainId | requested_captain_id | Schema only | Schema only | Schema only | Schema only |
| Tier | tier | Absent | Absent | Absent | Absent |
| DependsOnMissionId | depends_on_mission_id | Yes | Yes | Yes | Yes |
| StageOrder | stage_order | Yes | Yes | Yes | Yes |
| PreferredModel | preferred_model | Yes | Yes | Yes | Yes |
| CapabilityHint | capabilityhint | Yes | Yes | Yes | Yes |
| Mode | mission_mode | Yes | Yes | Yes | Yes |
| RequiresReview | requires_review | Yes | Yes | Yes | Yes |
| ReviewDenyAction | review_deny_action | Yes | Yes | Yes | Yes |
| ReviewComment | review_comment | Yes | Yes | Yes | Yes |
| ReviewedByUserId | reviewed_by_user_id | Yes | Yes | Yes | Yes |
| ReviewRequestedUtc | review_requested_utc | Yes | Yes | Yes | Yes |
| ReviewedUtc | reviewed_utc | Yes | Yes | Yes | Yes |
| PrestagedFiles | prestaged_files | Yes | Yes | Yes | Yes |
| RecoveryAttempts | recovery_attempts | Yes | Yes | Yes | Yes |
| LandingRetryCount | landing_retry_count | Yes | Yes | Yes | Yes |
| StartFromRef | start_from_ref | Yes | Yes | Yes | Yes |
| LastRecoveryActionUtc | last_recovery_action_utc | Yes | Yes | Yes | Yes |
| RetrySkipCaptainIds | retry_skip_captain_ids | Yes | Yes | Yes | Create binding absent; other paths present |

The new registered Mission case sets non-default values, reopens the driver,
checks scoped read and full query, changes values, and repeats both checks.
It includes review denial, review/recovery dates, prestaged content, usage-wait
state and retry-skip data. It also checks a cross-tenant read. Identity and
graph bindings remain fixed during update. This does not certify rebinding,
every query projection or every field's update path.

`RequestedCaptainId` and `Tier` are deliberately excluded from the passing
case. Their intended persistence and dispatch contracts need explicit failing
tests and backend review. Do not infer a new routing policy from their model
properties. SQL Server's create SQL references `@retry_skip_captain_ids` but
does not bind it; this is a source finding pending startup repair and a runtime
failure test. No repair is claimed here.

## Executed evidence and open gates

The existing database runner was extended from 44 to 46 registered cases.
The added cases check repeat startup/version and Mission field preservation.
Schema checks now require the fork version floor and selected fork columns and
tables, including preparation, dispatch, quarantine, coordination and Judge
follow-ups. These checks do not enumerate every protected column.

| Check | Result |
| --- | --- |
| Full solution build before test changes | 212 warnings, 0 errors |
| Database project build after changes | Passed |
| Unit / automated API / runtime suites | 3,982 / 907 / 183 passed; zero failures; combined wall time 302 seconds |
| Migration gate and negative controls | Current and pinned fork pass; pinned upstream rejected; 9 controls pass |
| SQLite fresh install | 46 passed |
| SQLite saved baseline database reopened by new runner | 46 passed; schema 82 retained |
| PostgreSQL 16.14 fresh and repeat | Exit 2 before cases: migration 52 references missing `objectives`; applied maximum 51 |
| MySQL 8.4.11 fresh | Exit 2 before cases: `idx_missions_vessel_status` exceeds the 3,072-byte index-key limit |
| MySQL retry after failed first install | Exit 2 before cases: duplicate `idx_vessels_fleet`; partial initial DDL remains |
| SQL Server 2022 16.0.4275.2 fresh and repeat | Exit 2 before cases: migration 52 references missing `objectives`; applied maximum 51 |

Server tests used isolated, loopback-only disposable containers and databases,
not the operational database. The three test containers and their temporary
credential files were removed after the runs. The MySQL failing statement was confirmed in
that test server's query log. It indexes two `VARCHAR(450)` columns under the
server's default character set. Do not treat a different character set as a
proved repair. Startup failure means zero contract cases ran on these three
providers. Their persistence and fork-upgrade gates remain unproved.

The saved SQLite database was created with unchanged fork production code
before the test extension. No production migration changed, so this checks
reopen compatibility, not a version transition. Representative historical
upgrade fixtures and broader entity maps remain required. Dashboard tests were
not repeated for this test/docs-only checkpoint; the prior routing result is
recorded in the review index.

No image was built or deployed for this checkpoint. A running-image inspection
did not provide a source commit label, so it does not prove removal of old
endpoints. Live provider collection, deployment proof, later ports and the
final archive decision remain separate work. Keep current review plans and
inventories accessible until accepted implementation and dispositions finish.
