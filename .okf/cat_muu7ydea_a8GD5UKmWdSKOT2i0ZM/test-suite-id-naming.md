---
type: "Reference"
title: "Test suite ids, folders, and reflection discovery"
description: "Suites implement IArmadaTestSuite with a public parameterless ctor and SuiteId \"Area.Name\" (Client, Database, E2E, Models, Runtimes, Services, Tui, Upgrade); discovered by reflection."
timestamp: "2026-10-05T05:26:25.6779130Z"
created: "2026-10-05T05:26:25.4428020Z"
slug: "test-suite-id-naming"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["test-suite-filter-armada-test-suites", "test-console-runner-touchstone"]
version: "1"
salience: "0.5"
---
Layout: src/Test.Shared/Suites/{Client,Database,E2E,Models,Runtimes,Services,Tui,Upgrade}. Each suite class implements IArmadaTestSuite (Test.Shared.Infrastructure) and returns a Touchstone TestSuiteDescriptor from Build() with SuiteId "<Area>.<Name>", for example Database.MissionDatabase, Services.LoginRateLimiter, E2E.McpTenantIsolation, Tui.Ops.Missions, Client.Contract.Vessels, Upgrade.FromBaseline. Counts (2026-10): about 145 Services, 62 Tui, 47 E2E, 41 Database, 16 Models, 13 Runtimes, 9 Client, 1 Upgrade.

ArmadaTestSuites.All discovers every non-abstract IArmadaTestSuite in the assembly by reflection (no manual registration, so a written suite can never be silently skipped); a suite without a public parameterless constructor throws at discovery. Suites run in ordinal SuiteId order so runs are stable across machines. Cases carry TestTags (positive or negative on every behavioral case; reliability, process, database, e2e as applicable) from Infrastructure/TestTags.cs. docs/TESTING.md's "register in Program.cs via runner.AddSuite" is outdated.
