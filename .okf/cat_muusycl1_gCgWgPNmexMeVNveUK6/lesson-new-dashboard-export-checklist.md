---
type: "Project"
title: "Adding a dashboard api/client.ts export: tests that must change"
description: "A new client.ts export needs an ArmadaClient method, a call in a ClientContract*.cs suite (Client.Coverage), TUI parity.json entry (no \"planned\" allowed), mobile parity mapping, and the hard-coded export count in TuiParitySuite."
timestamp: "2026-10-08T20:54:02.3033870Z"
created: "2026-10-08T20:54:02.0886390Z"
slug: "lesson-new-dashboard-export-checklist"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
version: "1"
salience: "0.5"
---
Learned 2026-10-08 adding getHarborMetrics. Tui.Parity api_exports_have_client_methods asserts an exact export count (AssertEqual(N, exports.Count)) in src/Test.Shared/Suites/Tui/TuiParitySuite.cs; bump it. Client.Coverage requires every ArmadaClient method to be called in a Suites/Client/ClientContract*.cs file or listed in _CoveredElsewhere. TUI parity gate rejects "planned" entries, so map the export to the ArmadaClient method. Mobile parity:check uses --forbid-planned. Also: macOS BSD grep has no -P; check ASCII with python. Dashboard responses are camelized from PascalCase server JSON; dashboard login screenshots need localStorage armada_session_token, armada_password_change_skipped:default, armada_setup_completed and at least one fleet (an empty deployment forces the setup wizard).
