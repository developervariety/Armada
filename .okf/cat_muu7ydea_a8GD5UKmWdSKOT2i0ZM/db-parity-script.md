---
type: "Reference"
title: "Database provider parity: run-db-parity-tests.sh and db-parity.yml"
description: "scripts/common/run-db-parity-tests.sh runs the Database suites on SQLite plus throwaway PostgreSQL, MySQL, SQL Server containers; must print RESULT: PARITY OK."
timestamp: "2026-10-15T15:25:00.0000000Z"
created: "2026-10-05T05:27:25.9788860Z"
slug: "db-parity-script"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["test-db-provider-arguments", "release-checklist", "upgrade-test-script"]
version: "2"
salience: "0.5"
---
Usage: `scripts/common/run-db-parity-tests.sh [--providers sqlite,postgresql,mysql,sqlserver] [--framework net10.0] [--no-build]` (wrappers in scripts/linux, scripts/macos, scripts/windows/run-db-parity-tests.bat). Requires docker and dotnet. SQLite runs in-process; each server provider gets a throwaway container on a random host port (images overridable: ARMADA_POSTGRES_IMAGE default postgres:17-alpine, ARMADA_MYSQL_IMAGE mysql:8.4, ARMADA_SQLSERVER_IMAGE mcr.microsoft.com/mssql/server:2022-latest), removed afterward. The identical Touchstone Database suites (including Database.MigrationHygiene: migrations re-runnable and additive) run against every provider. Exits non-zero on any failure; success prints RESULT: PARITY OK.

.github/workflows/db-parity.yml (renamed from nightly-parity.yml on 2026-10-15; manual workflow_dispatch only, no schedule) runs it on ubuntu-latest (native amd64 avoids SQL Server timeouts seen under emulation on Apple Silicon), with workflow_dispatch inputs providers and framework; log uploaded as parity-results. A release needs a manual run green on the release commit. Run it after any schema migration change.
