---
type: "Reference"
title: "Database providers: SQLite (default), PostgreSQL, MySQL, SQL Server"
description: "settings.database.type selects Sqlite (default, file armada.db under the data dir, WAL + foreign keys), Postgresql, Mysql, or SqlServer; DatabaseDriverFactory.Create builds the driver; server providers support pooling and requireEncryption."
timestamp: "2026-10-05T05:44:53.9509810Z"
created: "2026-10-05T05:44:53.0932690Z"
slug: "data-four-providers"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["data-methods-pattern", "data-pre-migration-backup", "data-sqlite-only-features", "data-parity-and-upgrade-tests"]
version: "1"
salience: "0.5"
---
Code: src/Armada.Core/Database/DatabaseDriverFactory.cs (Create(DatabaseSettings, LoggingModule) -> SqliteDatabaseDriver | MysqlDatabaseDriver | PostgresqlDatabaseDriver | SqlServerDatabaseDriver; CreateAndInitializeAsync for tools/tests), DatabaseDriver.cs (abstract; ~50 I*Methods properties; InitializeAsync, GetSchemaVersionAsync, GetLatestSchemaVersion, GetPendingMigrationCountAsync), src/Armada.Core/Settings/DatabaseSettings.cs (DatabaseTypeEnum Sqlite, Postgresql, SqlServer, Mysql).

SQLite: connection string "Data Source=<filename>" (default armada.db, resolved under DataDirectory ~/.armada or ARMADA_DATA_DIR); NormalizeConnectionString forces ForeignKeys=true and DefaultTimeout >= 30 on every connection (the pragma is per-connection); InitializeAsync sets PRAGMA journal_mode=WAL. Server providers: hostname, port (0 = provider default 5432/3306/1433), username, password, databaseName, schema (PostgreSQL), minPoolSize 0-100, maxPoolSize 1-200, connectionLifetimeSeconds (>= 30), connectionIdleTimeoutSeconds (>= 10), requireEncryption, logQueries; plus migration backup settings ([[data-pre-migration-backup]]). The v0.1 -> v0.2 settings change replaced databasePath with the database object (migrations/migrate_v0.1.0_to_v0.2.0.*). Parity across all four is enforced by the DB parity script ([[data-parity-and-upgrade-tests]]); a few features remain SQLite-only ([[data-sqlite-only-features]]).
