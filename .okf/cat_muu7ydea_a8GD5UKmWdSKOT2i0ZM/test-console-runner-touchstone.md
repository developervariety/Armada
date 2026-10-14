---
type: "Reference"
title: "Run all tests with the Test.Automated console runner (Touchstone)"
description: "dotnet run --project src/Test.Automated --framework net10.0 (or net8.0) runs every Touchstone suite; exit code non-zero on failure; --results writes JSON."
timestamp: "2026-10-05T05:26:06.4941640Z"
created: "2026-10-05T05:26:06.0922400Z"
slug: "test-console-runner-touchstone"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["test-suite-filter-armada-test-suites", "test-suite-id-naming", "test-xunit-nunit-adapters", "test-db-provider-arguments"]
version: "1"
salience: "0.5"
---
All test cases are runner-agnostic Touchstone descriptors in src/Test.Shared (Touchstone.Core 0.2.0; about 3,400 cases in 334 suites as of 2026-10). Run them all:

  dotnet run --project src/Test.Automated --framework net10.0
  dotnet run --project src/Test.Automated --framework net8.0

src/Test.Automated/Program.cs (Touchstone.Cli ConsoleRunner) accepts: `--results <path>` (JSON results; CI uses test-results/test-automated-<os>-<fw>.json), `--suites <prefixes>` (sets ARMADA_TEST_SUITES), `--db-type/--db-host/--db-port/--db-user/--db-pass/--db-name` (set ARMADA_TEST_DB_*), and `--generate-api-surface <dir>` (runs no tests; regenerates the API surface baseline). Returns 0 only when every test passes.

Note: docs/TESTING.md still shows older flags (`--suite`, `--type`, `-h`, `--no-cleanup`) and says suites are registered in Program.cs; the code uses `--suites`/`--db-*` and reflection discovery. Trust the code. A full run takes many minutes; prefer a filtered run while iterating and the full run before committing.
