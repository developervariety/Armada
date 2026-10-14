---
type: "Reference"
title: "Pre-migration backup and the server-provider confirmation gate (MigrationBackupService)"
description: "Before migrating, MigrationBackupService copies SQLite armada.db(+wal/shm) to <DataDir>/backups/pre-migration-<UTC>-v<from>-to-v<to>/ (keep newest 5); for PostgreSQL/MySQL/SQL Server it logs a DATABASE UPGRADE banner with a dump command and can refuse (exit 3) until a backup is confirmed for the target version."
timestamp: "2026-10-05T05:46:54.4532060Z"
created: "2026-10-05T05:45:38.9450390Z"
slug: "data-pre-migration-backup"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["upgrade-migrations-and-backups", "server-db-backup-confirmation", "data-migration-rules", "arch-admiral-startup-sequence"]
version: "2"
salience: "0.5"
---
src/Armada.Core/Services/MigrationBackupService.cs, called first in ArmadaServer.StartAsync via PrepareAsync(driver) before _Database.InitializeAsync. Nothing happens on a brand-new database (version 0) or an up-to-date one; if the database is newer than the build it warns and does not migrate (downgrades unsupported).

SQLite (BackupSqlite): SqliteConnection.ClearAllPools(), copy the db file plus -wal and -shm into backups/pre-migration-<yyyyMMddTHHmmssfffZ>-v<cur>-to-v<target>[-n]/; log the folder and rollback steps; PruneBackups to database.migrationBackupRetentionCount (default 5, clamped 1-100); a failed copy stops startup rather than migrating unprotected.
Server providers (CheckServerProvider): BuildDumpCommand returns pg_dump -Fc / mysqldump --single-transaction --routines --triggers / sqlcmd BACKUP DATABASE ... WITH INIT, CHECKSUM, filled with host, port, user, database and file armada-before-schema-v<N>. If database.requireBackupConfirmationForMigrations (default false) and the confirmed version (database.confirmedBackupForSchemaVersion or env ARMADA_CONFIRM_BACKUP_FOR_SCHEMA_VERSION, whichever is higher) is below the target, it throws MigrationBackupRequiredException; Program prints "Startup refused" and exits with code 3. Confirmation names a version so one confirmation never approves the next upgrade. Operator procedures: [[upgrade-migrations-and-backups]], [[server-db-backup-confirmation]].
