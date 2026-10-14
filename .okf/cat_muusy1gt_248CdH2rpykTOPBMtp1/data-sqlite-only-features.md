---
type: "Reference"
title: "Features that work only on SQLite"
description: "Planning-session persistence (others throw NotSupportedException -> 501), built-in backup/restore ZIPs, pre-migration file copies, and DataExpiryService (dataRetentionDays) are SQLite-only; everything else has four-provider parity."
timestamp: "2026-10-05T05:46:46.6116010Z"
created: "2026-10-05T05:45:52.5878270Z"
slug: "data-sqlite-only-features"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["data-four-providers", "backup-and-restore", "retention-settings", "term-planning-session"]
version: "2"
salience: "0.5"
---
Verified in code (2026-10-05):
- Planning sessions: Postgresql/Mysql/SqlServer Implementations/PlanningSessionMethods.cs and PlanningSessionMessageMethods.cs throw NotSupportedException("Planning sessions are currently implemented for SQLite-backed Armada deployments."), surfaced as HTTP 501 (and MCP ErrorCode Unavailable). Backlog refinement sessions DO work on all providers.
- Backup/restore endpoints and MCP tools: SQLite only (400 otherwise); see operations memory [[backup-and-restore]].
- Pre-migration backup is a file copy for SQLite; server providers only get a warning/dump command/confirmation gate ([[data-pre-migration-backup]]).
- DataExpiryService (purges completed voyages and their missions, old signals, old events after DataRetentionDays, default 30) talks to SQLite directly; ArmadaServer runs it only when Database.Type == Sqlite. RetentionService (Ask threads, jobs, import batches), request-history purge, and fleet action run pruning work on every provider (see [[retention-settings]]).
Factory reset scripts (docker/armada/factory/reset.*) delete the SQLite file only and never touch an external database.
