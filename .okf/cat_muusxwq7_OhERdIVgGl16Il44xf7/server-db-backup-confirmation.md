---
type: "Reference"
title: "Require backup confirmation before migrating a server database"
description: "database.requireBackupConfirmationForMigrations true makes startup exit code 3 until database.confirmedBackupForSchemaVersion or ARMADA_CONFIRM_BACKUP_FOR_SCHEMA_VERSION is at least the target version."
timestamp: "2026-10-05T05:31:17.6069360Z"
created: "2026-10-05T05:31:17.3498380Z"
slug: "server-db-backup-confirmation"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["upgrade-migrations-and-backups", "backup-and-restore"]
version: "1"
salience: "0.5"
---
For PostgreSQL, MySQL, and SQL Server the Admiral cannot copy the database, so by default it warns and migrates. Settings (settings.json, database section): requireBackupConfirmationForMigrations (default false) and confirmedBackupForSchemaVersion (default 0). With the requirement on and no confirmation for the target version, startup logs the dump command, prints "Startup refused: ..." (MigrationBackupRequiredException caught in Program.RunServerAsync), and exits with code 3. Take the dump, then set confirmedBackupForSchemaVersion to the target version shown, or set the environment variable ARMADA_CONFIRM_BACKUP_FOR_SCHEMA_VERSION (handy for Docker and one-off runs), and start again. The confirmation names a version so confirming one upgrade never approves the next. Restore procedures per provider are in docs/UPGRADING.md (dropdb/createdb/pg_restore --no-owner; mysql DROP/CREATE then import; sqlcmd RESTORE DATABASE WITH REPLACE in SINGLE_USER).
