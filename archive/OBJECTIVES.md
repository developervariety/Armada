# Backlog Capability Plan

## Status Snapshot

As of `2026-05-14`, the backlog capability is implemented across server, dashboard, MCP, Helm, SDK, Postman, and docs. Automated verification is now green across the unit suite, the full REST/MCP harness, and the SQLite/PostgreSQL/MySQL/SQL Server database matrix. The remaining follow-up is limited to deeper manual UI review and smoke coverage, plus any external SDK publication work that exists outside this repo.

- [x] Armada has a first-class backlog surface that lets users capture future work without leaving the product.
- [x] Objective storage is normalized into first-class database tables while preserving event-backed history.
- [x] Users can prioritize, rank, categorize, and filter backlog items in the React dashboard.
- [x] Users can refine backlog items with a model inside Armada before launching repository-aware planning.
- [x] Backlog refinement with a specifically selected captain is a first-class product workflow, not just an indirect use of planning.
- [x] Armada preserves full lineage from backlog item -> refinement -> planning -> dispatch -> release -> deployment -> incident -> history.
- [x] REST, MCP, Helm, SDK, Postman, dashboard, and documentation surfaces are all updated.
- [x] The capability ships as a minor-version release and all version-bearing files, scripts, tests, and docs are updated consistently.
- [x] SQLite, PostgreSQL, MySQL, and SQL Server are all covered by schema changes, migrations, and tests.

## How To Maintain This File

- Use `[ ]` for not started, `[~]` for in progress, and `[x]` for complete.
- This plan currently assumes the next release for this additive capability is the minor bump `v0.7.0 -> v0.8.0`.
- When implementation reality changes, update:
  - the status snapshot
  - locked decisions
  - planned schema if the model changes
  - workstream checklists
  - file map
  - verification and definition of done
- If another release lands first, keep this capability on a minor-version increment from the then-current release and update the migration-script and release-surface references instead of leaving stale version numbers in place.

## Product Goal

Armada should support the full future-work lifecycle from inside the product:

1. capture ideas, bugs, refactors, and research items in Armada
2. prioritize and rank them across fleets and vessels
3. refine them with a model before code-aware execution starts
4. promote refined items into planning and dispatch without copy-paste
5. preserve end-to-end lineage and queryable memory after the work ships

The intent is to make Armada the single pane of glass for both future design work and current implementation work.

## Recommended Product Shape

The recommended implementation is:

1. Keep `Objective` as the core domain record.
2. Make `Backlog` the primary user-facing label and workflow in the dashboard, CLI, and docs.
3. Normalize objective persistence into dedicated tables instead of continuing to read the latest state only from `events`.
4. Keep `objective.snapshot` events for audit/history/timeline continuity.
5. Add a lightweight backlog-refinement conversation flow that is distinct from repository-aware planning sessions.
6. Preserve compatibility for existing `/api/v1/objectives/...` routes and current objective-linked planning/release/deployment behavior.

This gives Armada a backlog without introducing a second overlapping "future work" entity.

## Non-Goals For First Ship

- Full sprint-planning, story-point, burndown, or velocity tooling
- Full Jira/Linear replacement
- Proxy-shell parity with the full React backlog UX
- Interactive backlog-refinement chat in Helm CLI
- New external tracker integrations beyond the current GitHub objective import path
- Replacing `Objective` terminology everywhere in the codebase on day one

## Locked Decisions

- [x] User-facing product language should prefer `Backlog`, while the internal domain record remains `Objective`.
- [x] Backlog v1 should be objective-backed, not a separate `BacklogItem` entity.
- [x] Objective persistence should move to normalized tables; events remain for timeline/audit, not as the only source of current truth.
- [x] Backlog refinement should be a separate experience from planning sessions.
- [x] Backlog refinement must allow the user to explicitly choose which captain will perform the refinement.
- [x] Choosing a captain for backlog refinement is part of the normal backlog UX and should not require the user to leave the backlog/objective flow for a separate planning workaround.
- [x] Refinement should be lighter than planning: no dock/worktree by default, no repository mutation expectation, optional vessel context.
- [x] Planning sessions remain the heavier repository-aware step before dispatch.
- [x] Existing objective routes and tools remain supported for backward compatibility even if backlog aliases are added.
- [x] This capability should ship as the next additive minor release, assumed in this plan as `v0.7.0 -> v0.8.0` unless another minor release lands first.
- [x] Cross-database support is required on first ship for all schema-backed backlog capabilities.
- [x] React dashboard gets the full backlog UX in v1.
- [x] Any new dashboard views and any materially updated existing dashboard views for backlog/objectives must receive an explicit styling and usability pass focused on consistency, scanability, and ease of use before the work is considered done.
- [x] Backlog management must be exposed through MCP as a first-class surface, not left as dashboard/REST-only functionality.
- [x] Legacy embedded dashboard and proxy shell should at minimum avoid breakage and surface links/wording updates, but do not need full parity in v1.
- [x] Helm CLI should get backlog CRUD coverage in v1, but not interactive refinement chat.
- [x] Objective status should continue to represent lifecycle state; a separate backlog maturity field should handle pre-dispatch readiness.

## Planned Domain Model

### Objective Additions

Add the following fields to `Objective`, `ObjectiveUpsertRequest`, `ObjectiveQuery`, API schemas, SDKs, and dashboard types:

- `Kind`: enum, recommended values `Feature`, `Bug`, `Refactor`, `Research`, `Chore`, `Initiative`
- `Category`: optional short freeform label such as `Frontend`, `Backend`, `DevEx`, `Ops`
- `Priority`: enum, recommended values `P0`, `P1`, `P2`, `P3`
- `Rank`: integer used for deterministic ordering within a tenant/fleet/vessel backlog
- `BacklogState`: enum, recommended values `Inbox`, `Triaged`, `Refining`, `ReadyForPlanning`, `ReadyForDispatch`, `Dispatched`
- `Effort`: enum, recommended values `XS`, `S`, `M`, `L`, `XL`
- `TargetVersion`: optional string such as `v0.8.0`
- `DueUtc`: optional UTC due date
- `ParentObjectiveId`: optional parent objective for initiative/epic rollup
- `BlockedByObjectiveIds`: list of objective IDs
- `RefinementSummary`: optional model-generated summary of the refined implementation intent
- `SuggestedPipelineId`: optional pipeline recommendation
- `SuggestedPlaybooks`: optional ordered playbook suggestions to use when dispatching

Keep the existing fields for:

- title/description
- owner
- tags
- acceptance criteria
- non-goals
- rollout constraints
- evidence links
- linked fleets/vessels
- linked planning sessions/voyages/missions/check runs/releases/deployments/incidents
- external source metadata

### New Refinement Models

Add backlog-refinement models:

- `ObjectiveRefinementSession`
- `ObjectiveRefinementMessage`
- `ObjectiveRefinementSessionStatusEnum`

Recommended session fields:

- `Id`
- `ObjectiveId`
- `TenantId`
- `UserId`
- `CaptainId`
- `FleetId`
- `VesselId`
- `Title`
- `Status`
- `ProcessId`
- `FailureReason`
- `CreatedUtc`
- `StartedUtc`
- `CompletedUtc`
- `LastUpdateUtc`

Recommended message fields:

- `Id`
- `ObjectiveRefinementSessionId`
- `ObjectiveId`
- `TenantId`
- `UserId`
- `Role`
- `Sequence`
- `Content`
- `IsSelected`
- `CreatedUtc`
- `LastUpdateUtc`

### Captain State

If backlog refinement reserves captains, add:

- `CaptainStateEnum.Refining`

If implementation proves simpler without explicit captain reservation, revisit this decision before coding and update this file.

## Planned Persistence

### Objectives Table

Create a normalized `objectives` table in every supported database. Recommended columns:

- `id`
- `tenant_id`
- `user_id`
- `title`
- `description`
- `status`
- `kind`
- `category`
- `priority`
- `rank`
- `backlog_state`
- `effort`
- `owner`
- `target_version`
- `due_utc`
- `parent_objective_id`
- `blocked_by_objective_ids_json`
- `refinement_summary`
- `suggested_pipeline_id`
- `suggested_playbooks_json`
- `tags_json`
- `acceptance_criteria_json`
- `non_goals_json`
- `rollout_constraints_json`
- `evidence_links_json`
- `fleet_ids_json`
- `vessel_ids_json`
- `planning_session_ids_json`
- `voyage_ids_json`
- `mission_ids_json`
- `check_run_ids_json`
- `release_ids_json`
- `deployment_ids_json`
- `incident_ids_json`
- `source_provider`
- `source_type`
- `source_id`
- `source_url`
- `source_updated_utc`
- `created_utc`
- `last_update_utc`
- `completed_utc`

Recommended indexes:

- `tenant_id, status, last_update_utc`
- `tenant_id, backlog_state, priority, rank`
- `tenant_id, kind, priority`
- `tenant_id, owner`
- `tenant_id, due_utc`
- `tenant_id, target_version`
- `tenant_id, parent_objective_id`
- unique or de-duplication-supporting source index for imported records where safe, for example `tenant_id + source_provider + source_type + source_id`

### Objective Refinement Session Tables

Create:

- `objective_refinement_sessions`
- `objective_refinement_messages`

Recommended indexes:

- `objective_refinement_sessions(tenant_id, objective_id, created_utc desc)`
- `objective_refinement_sessions(tenant_id, captain_id, status)`
- `objective_refinement_messages(objective_refinement_session_id, sequence)`
- `objective_refinement_messages(objective_id, created_utc desc)`

### Backfill Strategy

- [x] After the normalized `objectives` table exists, backfill it from the latest `objective.snapshot` event per objective ID.
- [x] The backfill must be idempotent so restarts do not duplicate or corrupt data.
- [x] Existing `objective.snapshot` events must remain intact after backfill.
- [x] Once cut over, `ObjectiveService` should read current state from the table and continue writing snapshot events on create/update/link transitions.
- [x] GitHub objective import must upsert the normalized row and continue emitting snapshot events.

## Migration And Release Strategy

Current release/version surfaces observed in-repo:

- Shared .NET version in [src/Directory.Build.props](C:/Code/Armada/Armada/src/Directory.Build.props): `0.8.0`
- Runtime product version in [src/Armada.Core/Constants.cs](C:/Code/Armada/Armada/src/Armada.Core/Constants.cs): `0.8.0`
- Dashboard package version in [src/Armada.Dashboard/package.json](C:/Code/Armada/Armada/src/Armada.Dashboard/package.json): `0.8.0`

Release assumption for this plan:

- Ship backlog as minor release `v0.8.0`
- If another release merges first, preserve minor-version semantics and update this file to the then-current `vX.Y.Z -> vX.(Y+1).0` release handoff before implementation merges

Current max schema versions observed in-repo:

- SQLite: `43`
- PostgreSQL: `42`
- MySQL: `42`
- SQL Server: `42`

Implemented migration numbers for this backlog schema slice:

- SQLite: `43`
- PostgreSQL: `42`
- MySQL: `42`
- SQL Server: `42`

If the work is split across multiple PRs, keep versions monotonic and sequential per backend. Do not renumber migrations that already merged.

### Required Migration Tasks

- [x] Add SQLite migration(s) in [src/Armada.Core/Database/Sqlite/Queries/TableQueries.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/Sqlite/Queries/TableQueries.cs).
- [x] Add PostgreSQL migration(s) in [src/Armada.Core/Database/Postgresql/Queries/TableQueries.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/Postgresql/Queries/TableQueries.cs).
- [x] Add MySQL migration(s) through [src/Armada.Core/Database/Mysql/Queries/TableQueries.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/Mysql/Queries/TableQueries.cs) and [src/Armada.Core/Database/Mysql/MysqlDatabaseDriver.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/Mysql/MysqlDatabaseDriver.cs).
- [x] Add SQL Server migration(s) in [src/Armada.Core/Database/SqlServer/Queries/TableQueries.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/SqlServer/Queries/TableQueries.cs).
- [x] Add or update any new foreign-key constraints and indexes consistently across all four backends.
- [x] Add one-time normalized-objective backfill logic in application startup/service initialization, not in ad hoc operator docs only.
- [x] Update schema verification tests for the new max schema versions, tables, columns, and indexes.
- [x] Add release upgrade scripts `migrations/migrate_v0.7.0_to_v0.8.0.sh` and `migrations/migrate_v0.7.0_to_v0.8.0.bat`, or rename those paths if the release number changes.
- [x] Ensure migration scripts explain backup expectations, automatic schema upgrade behavior, and any manual prechecks.

## Workstreams

### Workstream 1: Objective Storage Normalization

- [x] Add `IObjectiveMethods`.
- [x] Add `Objectives` to `DatabaseDriver`.
- [x] Implement objective CRUD/enumeration/reorder support for SQLite.
- [x] Implement objective CRUD/enumeration/reorder support for PostgreSQL.
- [x] Implement objective CRUD/enumeration/reorder support for MySQL.
- [x] Implement objective CRUD/enumeration/reorder support for SQL Server.
- [x] Migrate `ObjectiveService` away from scanning event snapshots for every list/read operation.
- [x] Preserve `objective.snapshot` event emission on create/update/link transitions.
- [x] Preserve current objective IDs and route semantics so existing links remain valid.
- [x] Backfill current objective state from historical events into the normalized table.
- [x] Add tests proving backfilled rows match the latest event snapshot.

### Workstream 2: Objective Domain Expansion

- [x] Add `ObjectiveKindEnum`.
- [x] Add `ObjectivePriorityEnum`.
- [x] Add `ObjectiveBacklogStateEnum`.
- [x] Add `ObjectiveEffortEnum`.
- [x] Extend `ObjectiveStatusEnum` only if there is a compelling need beyond the separate backlog-state field.
- [x] Extend [src/Armada.Core/Models/Objective.cs](C:/Code/Armada/Armada/src/Armada.Core/Models/Objective.cs).
- [x] Extend [src/Armada.Core/Models/ObjectiveUpsertRequest.cs](C:/Code/Armada/Armada/src/Armada.Core/Models/ObjectiveUpsertRequest.cs).
- [x] Extend [src/Armada.Core/Models/ObjectiveQuery.cs](C:/Code/Armada/Armada/src/Armada.Core/Models/ObjectiveQuery.cs).
- [x] Update validation rules so rank, priority, parent/dependency references, and linked IDs are validated consistently.
- [x] Add a batch reorder/request model for ranked backlog updates.
- [x] Decide whether `SuggestedPlaybooks` reuses `SelectedPlaybook` directly or a backlog-specific suggestion model.

### Workstream 3: Backlog Refinement Sessions

- [x] Add `ObjectiveRefinementSession`.
- [x] Add `ObjectiveRefinementMessage`.
- [x] Add `IObjectiveRefinementSessionMethods`.
- [x] Add `IObjectiveRefinementMessageMethods`.
- [x] Add per-database CRUD implementations for refinement sessions and messages.
- [x] Add `ObjectiveRefinementCoordinator` or equivalent service.
- [x] Model backlog refinement around an explicitly selected captain so the session records which captain performed the work and the user controls that selection up front.
- [x] Reuse the runtime-launch pattern from planning where appropriate, but do not provision a dock/worktree by default.
- [x] Decide whether refinement requires captain reservation; if yes, add `CaptainStateEnum.Refining` and the associated lifecycle handling.
- [x] Add captain availability checks and clear failure behavior when the selected captain cannot accept a refinement session.
- [x] Persist the selected `CaptainId` on the refinement session and surface it everywhere the session is read.
- [x] Add summary generation from a selected or latest assistant refinement message.
- [x] Add an "apply summary to objective" path that updates acceptance criteria, non-goals, rollout constraints, refinement summary, and related backlog metadata.
- [x] Add objective-level links to associated refinement sessions.
- [x] Ensure refinement sessions are tenant-scoped and user-scoped like planning sessions.

### Workstream 4: REST API, OpenAPI, Auth, And Request History

- [x] Extend existing objective REST routes with the new backlog fields and query parameters.
- [x] Add `POST /api/v1/objectives/reorder` for ranked backlog updates.
- [x] Add backlog alias routes such as `/api/v1/backlog` and `/api/v1/backlog/{id}` if you want user-facing API terminology without breaking `/objectives`.
- [x] Add refinement-session REST routes, recommended minimum set:
- [x] `GET /api/v1/objectives/{id}/refinement-sessions`
- [x] `POST /api/v1/objectives/{id}/refinement-sessions`
- [x] `GET /api/v1/objective-refinement-sessions/{id}`
- [x] `POST /api/v1/objective-refinement-sessions/{id}/messages`
- [x] `POST /api/v1/objective-refinement-sessions/{id}/summarize`
- [x] `POST /api/v1/objective-refinement-sessions/{id}/apply`
- [x] `POST /api/v1/objective-refinement-sessions/{id}/stop`
- [x] `DELETE /api/v1/objective-refinement-sessions/{id}`
- [x] Ensure OpenAPI output reflects all new fields, routes, and schemas.
- [x] Ensure the dashboard API Explorer and request-history replay continue to work against the expanded OpenAPI surface.
- [x] Add request-history coverage for the new routes without exposing sensitive tokens or prompts beyond current redaction policy.
- [x] Update authorization policy configuration so backlog/refinement routes are protected consistently with objectives and planning.

### Workstream 5: WebSocket And History Integration

- [x] Continue broadcasting `objective.changed` with the expanded payload.
- [x] Add `objective-refinement-session.changed`.
- [x] Add `objective-refinement-session.message.created`.
- [x] Add `objective-refinement-session.message.updated`.
- [x] Add `objective-refinement-session.summary.created`.
- [x] Add `objective-refinement-session.applied`.
- [x] Update [src/Armada.Core/Services/HistoricalTimelineService.cs](C:/Code/Armada/Armada/src/Armada.Core/Services/HistoricalTimelineService.cs) to include refinement-session entries.
- [x] Add history filtering so users can trace a backlog item through refinement, planning, dispatch, checks, releases, deployments, and incidents.
- [x] Update WebSocket docs and event tests to cover the new payloads.

### Workstream 6: Planning, Dispatch, Release, And Delivery Integration

- [x] Keep the current objective-to-planning-session link path intact.
- [x] Allow a backlog item to launch a refinement session without requiring a vessel.
- [x] Allow a refined backlog item to promote into a normal planning session once a vessel/captain/pipeline is chosen.
- [x] Carry `ObjectiveId` from backlog through planning-session creation, voyage creation, and downstream release/deployment linkage.
- [x] Auto-promote objective `Status` and `BacklogState` when refinement, planning, and dispatch milestones occur.
- [x] Ensure release creation and deployment flows continue to link back to the same objective record.
- [x] Update any timeline/history summary cards that assume objectives are only "scoped delivery records" and not also backlog items.

### Workstream 7: React Dashboard

- [x] Treat styling/detail work as first-class scope for backlog/objective dashboard changes, not optional cleanup after functional implementation.
- [x] Add a first-class `Backlog` navigation entry in the React dashboard.
- [x] Decide whether to keep `/objectives` as the route and add `/backlog` as an alias, or move to `/backlog` and preserve `/objectives` as a redirect.
- [x] Rework the list view so users can sort and filter by `Kind`, `Priority`, `BacklogState`, `Effort`, `Owner`, `TargetVersion`, `DueUtc`, and vessel/fleet links.
- [x] Add rank editing or explicit reorder controls.
- [x] Add grouped backlog views such as "Inbox", "Ready For Planning", "Ready For Dispatch", and "Blocked".
- [x] Update the detail page to surface all new backlog metadata fields cleanly.
- [x] Add a first-class backlog-refinement entry flow from the backlog/objective detail view that includes explicit captain selection as part of session start.
- [x] Add a backlog-refinement transcript UI with create/send/stop/summarize/apply actions.
- [x] Show which captain is assigned to the refinement session in the list, detail, and transcript views.
- [x] Make it obvious in the UI when refinement uses a selected captain versus when planning uses a separate repository-aware session.
- [x] Add "Start Planning", "Open In Dispatch", and "Draft Release" actions from the backlog detail page.
- [x] Show clear messaging that refinement is lighter than planning and does not imply repository mutation.
- [x] Show clear messaging when a backlog item lacks a vessel and therefore cannot yet start repository-aware planning or dispatch.
- [x] Update empty states, field labels, confirmation copy, badges, and summaries from `Objectives` language to `Backlog` where user-facing.
- [x] Keep objective detail deep links working.
- [x] Keep the old embedded dashboard as a minimal fallback surface; the React dashboard remains the supported backlog experience whenever external dashboard assets are present.
- [~] Perform a deliberate consistency pass across all touched list/detail/refinement/planning entry views so spacing, typography, cards, tables, forms, action rows, badges, and modal patterns match adjacent Armada dashboard surfaces.
- [~] Review high-density screens for scanability: column order, visual hierarchy, truncation, metadata grouping, and action placement should reduce cognitive load rather than mirror raw API shape.
- [~] Review all new and updated views for empty, loading, success, warning, and error states so they feel intentional and consistent with the rest of the dashboard.
- [~] Review desktop and narrow-width layouts for backlog/objective screens to ensure the UI remains easy to use when lists, boards, filters, and detail forms become dense.
- [~] Include accessibility and interaction polish in the styling pass: focus order, button labeling, hover/focus states, disabled states, and keyboard usability should be checked explicitly.

### Workstream 8: Helm CLI

- [x] Add `armada backlog list`.
- [x] Add `armada backlog show <id>`.
- [x] Add `armada backlog create`.
- [x] Add `armada backlog update`.
- [x] Add `armada backlog delete`.
- [x] Add `armada backlog reorder` or an equivalent rank-update command.
- [x] Decide whether Helm should expose "start planning from backlog" as a convenience command in v1.
- [x] Do not block the ship on interactive backlog-refinement chat in Helm unless that becomes a deliberate product requirement.
- [x] Update CLI help text and README examples.

### Workstream 9: SDKs And Clients

- [x] Extend [src/Armada.Core/Client/ArmadaApiClient.cs](C:/Code/Armada/Armada/src/Armada.Core/Client/ArmadaApiClient.cs) with objective/backlog list, read, create, update, delete, reorder, and refinement-session methods.
- [x] Extend [src/Armada.Dashboard/src/api/client.ts](C:/Code/Armada/Armada/src/Armada.Dashboard/src/api/client.ts).
- [x] Extend [src/Armada.Dashboard/src/types/models.ts](C:/Code/Armada/Armada/src/Armada.Dashboard/src/types/models.ts).
- [x] Keep backward compatibility where possible for older objective payload consumers by adding fields rather than renaming/removing existing ones.
- [ ] If other published SDKs exist outside this repo, apply equivalent objective/backlog route and schema changes before release.

### Workstream 10: MCP

- [x] Treat backlog management over MCP as required parity for core non-UI operations, not a stretch goal.
- [x] Update `get_objective` so it returns the expanded backlog shape.
- [x] Update `create_objective` to accept the new backlog metadata fields.
- [x] Add `update_objective`.
- [x] Add `enumerate_objectives` or ensure `enumerate` can filter by the new backlog fields.
- [x] Add `reorder_objectives` or `reorder_backlog_items`.
- [x] Expose enough MCP operations to manage the backlog end to end: inspect, enumerate, create, update, reprioritize/re-rank, and delete or cancel as appropriate.
- [x] Ensure MCP callers can move a backlog item forward into refinement/planning/dispatch flows without losing the objective/backlog linkage.
- [x] If refinement is exposed through MCP, the caller must be able to specify the captain explicitly rather than relying on server-side implicit selection.
- [x] Decide whether backlog-specific MCP tool names should be added in parallel with objective-compatible names for clearer user-facing ergonomics.
- [x] Add refinement-session MCP tools if v1 includes model refinement over MCP.
- [x] Add `backlog` as an enumerate alias in `McpEnumerateTools` if user-facing terminology matters there.
- [x] Update [docs/MCP_API.md](C:/Code/Armada/Armada/docs/MCP_API.md) accordingly.

### Workstream 11: Postman And API Examples

- [x] Add or expand a `Backlog` or `Objectives` folder in [Armada.postman_collection.json](C:/Code/Armada/Armada/Armada.postman_collection.json).
- [x] Add examples for create/read/update/delete objective-backlog records.
- [x] Add examples for reorder/rank updates.
- [x] Add examples for refinement-session create/message/summarize/apply/stop/delete flows.
- [x] Add an example for objective -> planning -> voyage follow-through.
- [x] Update request bodies and example responses to include the new objective fields.

### Workstream 12: Versioning, Release Metadata, And Packaging

- [x] Bump the shared .NET product/package version from `0.7.0` to `0.8.0` in [src/Directory.Build.props](C:/Code/Armada/Armada/src/Directory.Build.props).
- [x] Update [src/Armada.Core/Constants.cs](C:/Code/Armada/Armada/src/Armada.Core/Constants.cs) so `Constants.ProductVersion` reports `0.8.0`.
- [x] Inspect all `.csproj` files under `src/` and `test/` for explicit version or packaging overrides and update or remove any per-project version surfaces so they align with the shared minor version.
- [x] Bump [src/Armada.Dashboard/package.json](C:/Code/Armada/Armada/src/Armada.Dashboard/package.json) and [src/Armada.Dashboard/package-lock.json](C:/Code/Armada/Armada/src/Armada.Dashboard/package-lock.json) to `0.8.0`.
- [x] Update release upgrade script filenames and contents to `migrations/migrate_v0.7.0_to_v0.8.0.sh` and `migrations/migrate_v0.7.0_to_v0.8.0.bat`, including real backlog schema steps and release notes in the script bodies.
- [x] Update release/version tests such as [test/Armada.Test.Unit/Suites/Services/ReleaseVersionTests.cs](C:/Code/Armada/Armada/test/Armada.Test.Unit/Suites/Services/ReleaseVersionTests.cs) and [test/Armada.Test.Unit/Suites/Database/SchemaMigrationTests.cs](C:/Code/Armada/Armada/test/Armada.Test.Unit/Suites/Database/SchemaMigrationTests.cs) to assert `0.8.0`.
- [x] Update tests or examples that hard-code the current release number in planning/checklist text, including [test/Armada.Test.Unit/Suites/Services/PipelineDispatchTests.cs](C:/Code/Armada/Armada/test/Armada.Test.Unit/Suites/Services/PipelineDispatchTests.cs) where applicable.
- [x] Run a repo-wide stale-version audit for `0.7.0` and `v0.7.0` across docs, scripts, code, tests, and examples; keep prior-version references only where they are intentionally historical.
- [x] Ensure release notes and changelog positioning describe backlog as the additive capability shipped in `v0.8.0`.

### Workstream 13: Documentation

- [x] Update [README.md](C:/Code/Armada/Armada/README.md) feature bullets, lifecycle narrative, glossary, and examples to include backlog.
- [x] Update [GETTING_STARTED.md](C:/Code/Armada/Armada/GETTING_STARTED.md) with a backlog-first workflow.
- [x] Update [END_TO_END_WORKFLOWS.md](C:/Code/Armada/Armada/END_TO_END_WORKFLOWS.md) with at least one backlog-refinement-planning-dispatch scenario.
- [x] Update version-pinned operator and packaging docs such as [docs/DOCKER.md](C:/Code/Armada/Armada/docs/DOCKER.md), [docs/DELIVERY_OPERATIONS.md](C:/Code/Armada/Armada/docs/DELIVERY_OPERATIONS.md), [docs/PIPELINES.md](C:/Code/Armada/Armada/docs/PIPELINES.md), [docs/REMOTE_MGMT.md](C:/Code/Armada/Armada/docs/REMOTE_MGMT.md), [docs/TUNNEL_PROTOCOL.md](C:/Code/Armada/Armada/docs/TUNNEL_PROTOCOL.md), [docs/TUNNEL_OPERATIONS.md](C:/Code/Armada/Armada/docs/TUNNEL_OPERATIONS.md), and [docs/TESTING_PIPELINES.md](C:/Code/Armada/Armada/docs/TESTING_PIPELINES.md) so they reference the correct shipped minor version where relevant.
- [x] Update [docs/REST_API.md](C:/Code/Armada/Armada/docs/REST_API.md) route tables, schemas, and examples.
- [x] Update [docs/MCP_API.md](C:/Code/Armada/Armada/docs/MCP_API.md).
- [x] Update [docs/PROXY_API.md](C:/Code/Armada/Armada/docs/PROXY_API.md) if backlog/versioned release examples or surrounding release narrative are affected.
- [x] Update [docs/WEBSOCKET_API.md](C:/Code/Armada/Armada/docs/WEBSOCKET_API.md).
- [x] Add `docs/BACKLOG.md` or an equivalent focused operator/user guide once the feature is implemented.
- [x] Update any architecture or persona docs if backlog refinement introduces a new built-in prompt template or persona concept.
- [x] Update [CHANGELOG.md](C:/Code/Armada/Armada/CHANGELOG.md) for the release that ships backlog.

### Workstream 14: Tests

- [x] Add or update objective model tests for all new fields and validation.
- [x] Add refinement-session model tests.
- [x] Add objective service tests for normalized storage, filtering, ranking, and lifecycle promotion.
- [x] Add refinement-session service/coordinator tests.
- [x] Add cross-database CRUD tests for normalized objectives.
- [x] Add cross-database CRUD tests for refinement sessions/messages.
- [x] Add migration/backfill tests from event-only objectives into normalized objective rows.
- [x] Update [test/Armada.Test.Database/SchemaVerificationTests.cs](C:/Code/Armada/Armada/test/Armada.Test.Database/SchemaVerificationTests.cs) with the new schema version expectations, tables, columns, and indexes.
- [x] Update foreign-key tests if `parent_objective_id` is added.
- [x] Update multi-tenant scoping tests for backlog/refinement routes and persistence.
- [x] Add automated REST route tests for new backlog and refinement endpoints.
- [x] Add WebSocket regression tests for backlog/refinement events.
- [x] Add MCP tool tests for the updated objective/backlog tools.
- [x] Add dashboard Vitest coverage for backlog list/detail/refinement components and utilities.
- [x] Evaluate Helm command coverage against the existing CLI test surface; no dedicated CLI command harness exists in this repo, so backlog Helm verification remains packaging/manual-smoke based rather than a new automated suite.
- [x] Add or extend release-surface tests so missed `0.7.0` literals fail fast outside intentional historical references.

## File Map

### Core Domain And Services

- [x] [src/Armada.Core/Models/Objective.cs](C:/Code/Armada/Armada/src/Armada.Core/Models/Objective.cs)
- [x] [src/Armada.Core/Models/ObjectiveUpsertRequest.cs](C:/Code/Armada/Armada/src/Armada.Core/Models/ObjectiveUpsertRequest.cs)
- [x] [src/Armada.Core/Models/ObjectiveQuery.cs](C:/Code/Armada/Armada/src/Armada.Core/Models/ObjectiveQuery.cs)
- [x] `src/Armada.Core/Models/ObjectiveRefinementSession.cs`
- [x] `src/Armada.Core/Models/ObjectiveRefinementMessage.cs`
- [x] `src/Armada.Core/Enums/ObjectiveKindEnum.cs`
- [x] `src/Armada.Core/Enums/ObjectivePriorityEnum.cs`
- [x] `src/Armada.Core/Enums/ObjectiveBacklogStateEnum.cs`
- [x] `src/Armada.Core/Enums/ObjectiveEffortEnum.cs`
- [x] `src/Armada.Core/Enums/ObjectiveRefinementSessionStatusEnum.cs`
- [x] [src/Armada.Core/Services/ObjectiveService.cs](C:/Code/Armada/Armada/src/Armada.Core/Services/ObjectiveService.cs)
- [x] `src/Armada.Core/Services/ObjectiveRefinementService.cs` or `ObjectiveRefinementCoordinator.cs`
- [x] [src/Armada.Core/Services/HistoricalTimelineService.cs](C:/Code/Armada/Armada/src/Armada.Core/Services/HistoricalTimelineService.cs)
- [x] [src/Armada.Core/Services/GitHubIntegrationService.cs](C:/Code/Armada/Armada/src/Armada.Core/Services/GitHubIntegrationService.cs)

### Database Interfaces And Drivers

- [x] `src/Armada.Core/Database/Interfaces/IObjectiveMethods.cs`
- [x] `src/Armada.Core/Database/Interfaces/IObjectiveRefinementSessionMethods.cs`
- [x] `src/Armada.Core/Database/Interfaces/IObjectiveRefinementMessageMethods.cs`
- [x] [src/Armada.Core/Database/DatabaseDriver.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/DatabaseDriver.cs)
- [x] [src/Armada.Core/Database/Sqlite/Queries/TableQueries.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/Sqlite/Queries/TableQueries.cs)
- [x] [src/Armada.Core/Database/Postgresql/Queries/TableQueries.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/Postgresql/Queries/TableQueries.cs)
- [x] [src/Armada.Core/Database/Mysql/Queries/TableQueries.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/Mysql/Queries/TableQueries.cs)
- [x] [src/Armada.Core/Database/Mysql/MysqlDatabaseDriver.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/Mysql/MysqlDatabaseDriver.cs)
- [x] [src/Armada.Core/Database/SqlServer/Queries/TableQueries.cs](C:/Code/Armada/Armada/src/Armada.Core/Database/SqlServer/Queries/TableQueries.cs)
- [x] Per-database objective implementations under each `Implementations` folder
- [x] Per-database refinement session/message implementations under each `Implementations` folder

### Server, MCP, And WebSocket

- [x] [src/Armada.Server/Routes/ObjectiveRoutes.cs](C:/Code/Armada/Armada/src/Armada.Server/Routes/ObjectiveRoutes.cs)
- [x] `src/Armada.Server/Routes/ObjectiveRefinementRoutes.cs`
- [x] [src/Armada.Server/Routes/PlanningSessionRoutes.cs](C:/Code/Armada/Armada/src/Armada.Server/Routes/PlanningSessionRoutes.cs)
- [x] [src/Armada.Server/ArmadaServer.cs](C:/Code/Armada/Armada/src/Armada.Server/ArmadaServer.cs)
- [x] [src/Armada.Server/Mcp/Tools/McpObjectiveTools.cs](C:/Code/Armada/Armada/src/Armada.Server/Mcp/Tools/McpObjectiveTools.cs)
- [x] [src/Armada.Server/Mcp/Tools/McpEnumerateTools.cs](C:/Code/Armada/Armada/src/Armada.Server/Mcp/Tools/McpEnumerateTools.cs)
- [x] [src/Armada.Server/WebSocket/ArmadaWebSocketHub.cs](C:/Code/Armada/Armada/src/Armada.Server/WebSocket/ArmadaWebSocketHub.cs)

### Dashboard

- [x] [src/Armada.Dashboard/src/api/client.ts](C:/Code/Armada/Armada/src/Armada.Dashboard/src/api/client.ts)
- [x] [src/Armada.Dashboard/src/types/models.ts](C:/Code/Armada/Armada/src/Armada.Dashboard/src/types/models.ts)
- [x] [src/Armada.Dashboard/src/pages/Objectives.tsx](C:/Code/Armada/Armada/src/Armada.Dashboard/src/pages/Objectives.tsx)
- [x] [src/Armada.Dashboard/src/pages/ObjectiveDetail.tsx](C:/Code/Armada/Armada/src/Armada.Dashboard/src/pages/ObjectiveDetail.tsx)
- [x] `src/Armada.Dashboard/src/pages/Backlog.tsx` or route alias wiring if reusing `Objectives.tsx`
- [x] `src/Armada.Dashboard/src/components/backlog/*`
- [x] [src/Armada.Dashboard/src/pages/Planning.tsx](C:/Code/Armada/Armada/src/Armada.Dashboard/src/pages/Planning.tsx)
- [x] [src/Armada.Dashboard/src/pages/Dispatch.tsx](C:/Code/Armada/Armada/src/Armada.Dashboard/src/pages/Dispatch.tsx)

### Helm, SDK, Postman

- [x] [src/Armada.Helm](C:/Code/Armada/Armada/src/Armada.Helm)
- [x] [src/Armada.Core/Client/ArmadaApiClient.cs](C:/Code/Armada/Armada/src/Armada.Core/Client/ArmadaApiClient.cs)
- [x] [Armada.postman_collection.json](C:/Code/Armada/Armada/Armada.postman_collection.json)
- [x] [nupkg](C:/Code/Armada/Armada/nupkg)

### Docs And Release Artifacts

- [x] [README.md](C:/Code/Armada/Armada/README.md)
- [x] [GETTING_STARTED.md](C:/Code/Armada/Armada/GETTING_STARTED.md)
- [x] [END_TO_END_WORKFLOWS.md](C:/Code/Armada/Armada/END_TO_END_WORKFLOWS.md)
- [x] [CHANGELOG.md](C:/Code/Armada/Armada/CHANGELOG.md)
- [x] [docs/REST_API.md](C:/Code/Armada/Armada/docs/REST_API.md)
- [x] [docs/MCP_API.md](C:/Code/Armada/Armada/docs/MCP_API.md)
- [x] [docs/WEBSOCKET_API.md](C:/Code/Armada/Armada/docs/WEBSOCKET_API.md)
- [x] `docs/BACKLOG.md`
- [x] [migrations](C:/Code/Armada/Armada/migrations)

### Tests

- [x] [test/Armada.Test.Unit](C:/Code/Armada/Armada/test/Armada.Test.Unit)
- [x] [test/Armada.Test.Automated](C:/Code/Armada/Armada/test/Armada.Test.Automated)
- [x] [test/Armada.Test.Database](C:/Code/Armada/Armada/test/Armada.Test.Database)
- [x] Dashboard Vitest files under [src/Armada.Dashboard/src](C:/Code/Armada/Armada/src/Armada.Dashboard/src)

## Verification

### Automated Verification

- [x] `dotnet build src\Armada.sln`
- [x] `dotnet run --project test\Armada.Test.Unit\Test.Unit.csproj --framework net8.0`
- [x] Run the database verification harness in `test/Armada.Test.Database` against SQLite, PostgreSQL, MySQL, and SQL Server; all four backends passed with normalized objective/refinement CRUD, FK, and scoping coverage.
- [x] Run the automated REST/MCP harness in `test\Armada.Test.Automated` end to end; the full suite now completes with `946/946` passing in [test/artifacts/automated-full-final.log](C:/Code/Armada/Armada/test/artifacts/automated-full-final.log).
- [x] `npm.cmd run build` in `src\Armada.Dashboard`
- [x] `npm.cmd run test:run` in `src\Armada.Dashboard`
- [x] `git diff --check`

### Manual Smoke Checklist

- [ ] Create a backlog item with no vessel and confirm it appears in the backlog list.
- [ ] Set kind, category, priority, rank, backlog state, effort, and target version and confirm list sorting/filtering works.
- [ ] Start a backlog-refinement conversation using an explicitly selected captain and confirm the selected captain is recorded, visible, and used by the session.
- [ ] Confirm assistant output streams and persists for that captain-backed refinement session.
- [ ] Apply a refinement summary back into the backlog item and confirm acceptance criteria/non-goals/summary update.
- [ ] Promote the backlog item into a planning session with a chosen vessel and captain.
- [ ] Dispatch work from planning and confirm voyage/mission links appear on the backlog item.
- [ ] Draft a release from the same backlog item and confirm release linkage.
- [ ] Drive the item through deployment and confirm history/timeline linkage remains intact.
- [ ] Verify the same item is accessible through REST, MCP, Helm, the .NET SDK, and Postman.
- [ ] Verify backlog CRUD, filtering, and reprioritization from an MCP client, not just existing objective reads.
- [ ] Restart the server mid-stream and confirm normalized objectives and refinement transcripts persist on each supported database type.
- [ ] Review all new and updated backlog/objective dashboard views on real pages for visual consistency and ease of use, not just functional correctness.

## Definition Of Done

- [x] A developer can keep future work inside Armada without relying on an external backlog tool for the core happy path.
- [x] Backlog items can be categorized, prioritized, ranked, filtered, and linked to fleets/vessels.
- [x] A developer can refine a backlog item with a model inside Armada before repository-aware planning.
- [x] A developer can choose a specific captain for backlog refinement as part of the normal backlog workflow.
- [x] A refined backlog item can be promoted into planning and then dispatched without copy-paste.
- [x] The same objective/backlog item remains the system of record through planning, dispatch, release, deployment, incident response, and history.
- [x] Objective current-state storage is normalized and no longer depends on scanning events for basic reads and lists.
- [x] Event-backed history remains intact and continues to power audit/timeline scenarios.
- [x] SQLite, PostgreSQL, MySQL, and SQL Server all ship with migrations, upgrade scripts, CRUD implementations, and tests for the backlog capability.
- [x] MCP clients can manage backlog items directly through explicit MCP backlog/objective tools and related promotion flows.
- [~] New and updated dashboard views have received explicit styling and usability review to ensure consistency with Armada's existing UI patterns and ease of use for dense operational workflows.
- [x] Dashboard, REST, MCP, Helm, SDK, Postman, README, docs, and changelog are all updated before release.
