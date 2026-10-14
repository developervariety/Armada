---
type: "Reference"
title: "Upgrades: automatic migrations, SQLite pre-migration backups, no downgrades"
description: "Any 0.9.x upgrades directly to 1.0; on start the Admiral copies SQLite files to backups/pre-migration-<UTC>-v<from>-to-v<to>/ (keeps 5) then applies migrations in transactions; downgrades unsupported."
timestamp: "2026-10-05T05:31:12.1787180Z"
created: "2026-10-05T05:31:11.5213620Z"
slug: "upgrade-migrations-and-backups"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["server-db-backup-confirmation", "backup-and-restore", "upgrade-test-script", "password-hashing-pbkdf2"]
version: "1"
salience: "0.5"
---
docs/UPGRADING.md. Schema version = highest row in schema_migrations. At startup, if newer migrations ship:
- SQLite: armada.db plus -wal/-shm are copied to {DataDirectory}/backups/pre-migration-<UTC timestamp>-v<from>-to-v<to>/ before migrating (database.migrationBackupRetentionCount, default 5, range 1-100). If the copy fails (full disk), startup stops instead of migrating without a backup.
- PostgreSQL/MySQL/SQL Server: logs a DATABASE UPGRADE warning with a filled-in dump command (pg_dump -Fc, mysqldump --single-transaction --routines --triggers, sqlcmd BACKUP DATABASE). Optional hard stop: see server-db-backup-confirmation.
Each migration runs in a transaction with its version row; MySQL cannot roll back DDL, so migrations are re-runnable and additive (checked by Database.MigrationHygiene). After migrating, built-in prompt templates, personas, pipelines, fleet actions are seeded; unedited built-ins pick up new defaults, edited ones keep yours, except built-in FullPipeline stages are always reset.
Starting an older Admiral on a newer schema logs a warning and does not migrate; restore the pre-upgrade backup instead. 1.0 rewrites password hashes to PBKDF2 at startup, so an upgraded DB cannot be used for password login by an older Admiral. Rollback for SQLite: stop, copy the pre-migration folder files over the live DB (delete stale -wal/-shm if the folder has none), start.
