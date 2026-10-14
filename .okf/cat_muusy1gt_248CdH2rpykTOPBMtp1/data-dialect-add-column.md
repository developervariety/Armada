---
type: "Reference"
title: "Dialect rules for re-runnable ADD COLUMN per provider"
description: "PostgreSQL uses ADD COLUMN IF NOT EXISTS; SQL Server wraps in IF COL_LENGTH(...) IS NULL; SQLite driver parses ADD COLUMN and checks pragma_table_info before running; MySQL has no ADD COLUMN IF NOT EXISTS, so plain ALTER is used and duplicate errors 1060/1061/1826 are ignored."
timestamp: "2026-10-05T05:45:30.7037250Z"
created: "2026-10-05T05:45:30.0841590Z"
slug: "data-dialect-add-column"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["data-migration-rules", "data-json-and-typed-columns"]
version: "1"
salience: "0.5"
---
Example v77 on each provider:
- PostgreSQL (Postgresql/Queries/TableQueries.cs): ALTER TABLE missions ADD COLUMN IF NOT EXISTS failure_kind TEXT NULL; ... wait_for_voyage_workers BOOLEAN NOT NULL DEFAULT FALSE;
- SQL Server (SqlServer/Queries/TableQueries.cs): IF COL_LENGTH('missions', 'failure_kind') IS NULL ALTER TABLE missions ADD failure_kind NVARCHAR(64) NULL; (bit columns for booleans, NVARCHAR for text; table existence via OBJECT_ID(N'name', N'U')).
- SQLite (Sqlite/Queries/TableQueries.cs): plain ALTER TABLE missions ADD COLUMN failure_kind TEXT NULL; booleans are INTEGER 0/1. SqliteDatabaseDriver.ExecuteMigrationAsync uses TryParseAddColumn (regex) + ColumnExistsAsync (SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column) to skip columns that already exist; it checks the schema rather than matching error text.
- MySQL (Mysql/Queries/TableQueries.cs MigrationV77Statements): ALTER TABLE missions ADD COLUMN failure_kind VARCHAR(64) NULL; ... wait_for_voyage_workers TINYINT(1) NOT NULL DEFAULT 0; MySQL lacks ADD COLUMN IF NOT EXISTS, so MysqlDatabaseDriver.ExecuteMigrationStatementAsync ignores only MySqlException numbers 1060 (duplicate column), 1061 (duplicate key name), 1826 (duplicate FK name) during (re)application.
Other dialect notes: timestamps stored as ISO-8601 text on SQLite (FromIso8601) and native datetime on server providers; JSON lists stored as text columns *_json; enums stored as their string names.
