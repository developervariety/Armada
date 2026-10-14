---
type: "Reference"
title: "Schema migrations: SchemaMigration list per provider, current highest version 77"
description: "Each driver holds an ordered List<SchemaMigration>(version, description, statements); schema_migrations records applied versions; the current highest is v77 (missions.failure_kind + wait_for_voyage_workers) on all four providers; numbers increase but are not contiguous and per-provider counts differ."
timestamp: "2026-10-05T05:45:12.9896300Z"
created: "2026-10-05T05:45:12.3790850Z"
slug: "data-schema-migration-numbering"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["data-migration-rules", "data-dialect-add-column", "data-pre-migration-backup"]
version: "1"
salience: "0.5"
---
Class src/Armada.Core/Database/SchemaMigration.cs (Version >= 1, Description, Statements, optional AlreadyAppliedCheckSql). Where the lists live: SQLite Sqlite/Queries/TableQueries.cs (76 entries), PostgreSQL Postgresql/Queries/TableQueries.cs (60), MySQL MysqlDatabaseDriver.GetMigrations with statements in Mysql/Queries/TableQueries.cs as MigrationV<N>Statements (61), SQL Server SqlServer/Queries/TableQueries.GetMigrations (59). Server providers consolidated early history into a v1 initial schema, so counts differ; the version NUMBERS of later migrations are shared.

As of 2026-10-05 the latest are: v73 vessel_health, vessel_health_findings, vessel_dependencies, vessel_health_overrides; v74 import discovery/categorization columns + vessel_import_fleet_recommendations tables; v75 ask_threads, ask_messages, ask_message_tool_calls, ask_action_proposals, ask_tracked_work; v76 nullable vessels.auto_approve; v77 missions.failure_kind (text) and missions.wait_for_voyage_workers (bool, default false). Earlier landmarks: 19-23 personas/pipelines/prompt templates (v0.4), 24-27 captain model + mission runtime (v0.5), 43 normalized objectives + refinement sessions (v0.8), 44 dock lease/captain liveness/review deadline/merge retry/coordination leases (v0.9), 55 per-step captain selection. Startup applies every pending migration in order (InitializeAsync); GetLatestSchemaVersion = last list entry. To add one: append the next number to ALL four lists in the same change, re-runnable and additive ([[data-migration-rules]]).
