---
type: "Reference"
title: "Regenerate the TUI parity manifest after dashboard changes (Tui.Parity)"
description: "After adding a dashboard route, tab, client.ts function, event, or Server setting: python3 scripts/tui/generate-parity-manifest.py, then implement and run ARMADA_TEST_SUITES=Tui.Parity."
timestamp: "2026-10-05T05:27:40.3736470Z"
created: "2026-10-05T05:27:39.8110050Z"
slug: "tui-parity-generator-and-suite"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["tui-parity-manifest", "api-surface-generator-and-contract-test", "dashboard-api-client"]
version: "1"
salience: "0.5"
---
The Tui.Parity suite (src/Test.Shared/Suites/Tui/TuiParitySuite.cs) parses the dashboard source (App.tsx routes, hub tabs, api/client.ts exports, WebSocket event types, Server-page settings fields) and src/Armada.Tui/parity.json, and fails when a dashboard surface has no manifest entry, an implemented entry points at a missing screen or command, a client.ts export has no ArmadaClient method, or a dashboard route does not resolve in the TUI router.

Procedure after a dashboard change:
1. `python3 scripts/tui/generate-parity-manifest.py` (adds new surfaces as "planned", keeps existing statuses and notes).
2. `python3 scripts/tui/generate-client-methods.py --missing` lists client.ts exports without a C# method; `--out <dir>` emits starter ArmadaClient.<Area>.cs partials (hand-finish NotImplementedException stubs marked HAND-FINISH).
3. Implement the TUI side, flip the entry to implemented.
4. `ARMADA_TEST_SUITES=Tui.Parity dotnet run --project src/Test.Automated --framework net10.0`.
The "no planned entries in release builds" check is described in TUI_APP_PLAN.md but not enforced yet.
