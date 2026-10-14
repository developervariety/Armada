---
type: "Reference"
title: "TestTemp: profile sandbox, armada_ temp prefix, sweep, and exit cleanup"
description: "A Test.Shared module initializer points ARMADA_DATA_DIR and TUI prefs/credentials at a per-run sandbox so tests never touch ~/.armada; it fails fast if Constants already resolved."
timestamp: "2026-10-05T05:26:38.7376320Z"
created: "2026-10-05T05:26:38.4419760Z"
slug: "test-temp-profile-sandbox"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["test-ports-random-range", "test-git-environment-pinning", "lesson-test-overwrote-user-settings", "pref-protect-owner-environment"]
version: "1"
salience: "0.5"
---
src/Test.Shared/Infrastructure/TestTemp.cs has a [ModuleInitializer] Initialize() that runs once for any runner (console, xUnit, NUnit):
1. Sweep(): deletes armada_* entries in the system temp dir older than 2 hours (age-gated so a concurrent run's fresh files survive).
2. EnsureExitHook(): deletes tracked paths on ProcessExit.
3. IsolateUserProfile(): creates armada_profile_<guid>, sets ARMADA_DATA_DIR to it, ARMADA_TUI_PREFERENCES=<sandbox>/tui.json, ARMADA_TUI_CREDENTIALS=<sandbox>/tui-credentials.json, ARMADA_TUI_CREDENTIAL_STORE=file (never the OS keychain), and throws InvalidOperationException if Armada.Core.Constants.DefaultDataDirectory already resolved to something else (it is a static readonly read once).
4. TestGitEnvironment.Pin().

Use TestTemp.NewDirectory(label) / NewFile(label, ext) for every temp artifact (names start with armada_ so the sweep can reclaim them), Track(path) for others, TryDelete(path) to remove early. Why: a test once overwrote the owner's ~/.armada/settings.json (see lesson-test-overwrote-user-settings); tests must never read or write the real profile, database, TUI prefs, or keychain, nor bind 7890/7891.
