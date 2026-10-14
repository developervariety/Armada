---
type: "Reference"
title: "Migration guarantees: transactional, re-runnable, additive (Database.MigrationHygiene)"
description: "Each migration runs in a transaction with its version row; every migration must be re-runnable (IF NOT EXISTS guards or AlreadyAppliedCheckSql) and additive (no drop/rename/delete); the Database.MigrationHygiene suite checks both on every provider; downgrades unsupported."
timestamp: "2026-10-05T05:45:20.8365100Z"
created: "2026-10-05T05:45:20.4820680Z"
slug: "data-migration-rules"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["data-schema-migration-numbering", "data-dialect-add-column", "data-parity-and-upgrade-tests"]
version: "1"
salience: "0.5"
---
Source: docs/UPGRADING.md "Migration guarantees"; driver code ExecuteMigrationAsync in each <Provider>DatabaseDriver; tests Database.MigrationHygiene (run by the DB parity script on all four providers).

Execution: compare MAX(version) in schema_migrations with the newest shipped migration; apply each pending migration in its own transaction and insert its version in the same transaction. SQLite, PostgreSQL, SQL Server roll back DDL on failure; MySQL does not (implicit commits), so a crash mid-migration can leave it partly applied, which is safe only because migrations are re-runnable (next start re-applies). If AlreadyAppliedCheckSql returns nonzero, the statements are skipped (used by the SQLite v15 multi-tenant table rebuild and the SQL Server v1 initial schema). ReplayMigrationsAsync / GetMigrationsForVerification (internal) let tests re-apply the full list on a populated DB and assert no schema or data change.
Rules for new migrations: re-runnable (guarded DDL, see [[data-dialect-add-column]]); additive only (add tables/columns/indexes, backfill empty values; never DROP/RENAME/DELETE; only reviewed exceptions are SQLite v8 removal of captains.max_parallelism and the v15 rebuild); no destructive change slips by because the hygiene check fails on anything new. Older Admiral against newer schema: MigrationBackupService logs a warning naming both versions and does not migrate; restore a backup instead.
