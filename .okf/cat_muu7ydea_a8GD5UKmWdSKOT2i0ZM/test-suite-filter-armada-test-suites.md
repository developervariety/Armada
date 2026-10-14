---
type: "Reference"
title: "Filter test runs with ARMADA_TEST_SUITES (suite id prefixes)"
description: "ARMADA_TEST_SUITES=E2E.McpTool,Database runs only suites whose SuiteId starts with a listed prefix (case-insensitive); --suites sets it."
timestamp: "2026-10-05T05:26:18.2711340Z"
created: "2026-10-05T05:26:17.9481340Z"
slug: "test-suite-filter-armada-test-suites"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["test-suite-id-naming", "test-console-runner-touchstone"]
version: "1"
salience: "0.5"
---
src/Test.Shared/ArmadaTestSuites.cs reads ARMADA_TEST_SUITES (comma-separated) and keeps suites whose SuiteId starts with any prefix (StartsWith, OrdinalIgnoreCase, trimmed). Examples:

  ARMADA_TEST_SUITES="Database,Services.MergeQueue" dotnet run --project src/Test.Automated --framework net10.0
  dotnet run --project src/Test.Automated --framework net10.0 -- --suites E2E.ApiContract
  ARMADA_TEST_SUITES=Tui.Parity dotnet run --project src/Test.Automated --framework net10.0

Prefix matching means `Tui.Ops` runs every Tui.Ops.* suite and `E2E.Mcp` runs E2E.McpTool, E2E.McpToolClient, E2E.McpTenantIsolation, E2E.McpAuthScoping. The filter works for Test.Automated and the xUnit/NUnit adapters. The old `--suite "<display name fragment>"` option in docs/TESTING.md no longer exists.
