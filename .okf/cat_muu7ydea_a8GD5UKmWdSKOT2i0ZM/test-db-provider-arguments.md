---
type: "Reference"
title: "Run tests against PostgreSQL, MySQL, or SQL Server"
description: "Test.Automated --db-type postgresql|mysql|sqlserver|sqlite plus --db-host/--db-port/--db-user/--db-pass/--db-name (ARMADA_TEST_DB_* env); default is temp SQLite."
timestamp: "2026-10-05T05:26:31.9312990Z"
created: "2026-10-05T05:26:31.3875900Z"
slug: "test-db-provider-arguments"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["db-parity-script", "test-console-runner-touchstone"]
version: "1"
salience: "0.5"
---
Default: every suite runs against a temporary SQLite database (cleaned up by TestTemp). To target a server provider:

  dotnet run --project src/Test.Automated --framework net10.0 -- --db-type postgresql --db-host localhost --db-port 5432 --db-user postgres --db-pass <pw> --db-name armada_test

The flags map onto ARMADA_TEST_DB_TYPE, ARMADA_TEST_DB_HOST, ARMADA_TEST_DB_PORT, ARMADA_TEST_DB_USER, ARMADA_TEST_DB_PASS, ARMADA_TEST_DB_NAME, which the shared harness (TestDatabaseHelper/TestDatabaseProvisioner) reads, so the same descriptors run unchanged. In practice use scripts/common/run-db-parity-tests.sh, which starts throwaway Docker containers per provider. docs/TESTING.md documents an older `--type ... -h -u -w -d` syntax that is no longer parsed. Never put real credentials in commits.
