---
type: "Project"
title: "A test overwrote the owner's ~/.armada/settings.json (2026-10-04)"
description: "SecurityTestServer saved settings to the default path; fixed by TestTemp sandboxing ARMADA_DATA_DIR and TUI paths for every test run, plus a Services.TestIsolation suite."
timestamp: "2026-10-05T05:25:06.5866170Z"
created: "2026-10-05T05:25:06.2387330Z"
slug: "lesson-test-overwrote-user-settings"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
links: ["pref-protect-owner-environment", "lesson-mcp-host-binding"]
version: "1"
salience: "0.5"
---
What happened: a security test server (Test.Shared/Infrastructure/SecurityTestServer.cs) did not set SettingsFilePath, so saving settings wrote the test's temp data paths and ports (21000-21099 range) into the owner's real ~/.armada/settings.json; his server then ran against test values. Fix: SecurityTestServer writes settings into its own temp dir; TestTemp's [ModuleInitializer] calls IsolateUserProfile(), pointing ARMADA_DATA_DIR, ARMADA_TUI_PREFERENCES, ARMADA_TUI_CREDENTIALS and ARMADA_TUI_CREDENTIAL_STORE=file at a sandbox and failing fast if Constants.DefaultDataDirectory does not resolve there; TuiPaths honors ARMADA_DATA_DIR; Services.TestIsolation asserts it. Recovery: the bad file was kept as ~/.armada/settings.json.corrupted-by-test.bak and paths/ports restored; customizations in the old file were lost, and the restore mistakenly kept rest.hostname 127.0.0.1 from the test values (see lesson-mcp-host-binding). Detect: diff ~/.armada before/after a full test run.
