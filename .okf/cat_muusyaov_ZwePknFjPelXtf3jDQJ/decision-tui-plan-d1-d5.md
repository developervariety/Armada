---
type: "Project"
title: "TUI plan decisions: hosted by Helm, Armada.Client, TUIKit upstream, keychain credentials"
description: "TUI is a library hosted by Helm as `armada tui`; Armada.Client generated from OpenAPI; general widgets upstreamed to TUIKit; credentials in OS keychain with 0600 file fallback."
timestamp: "2026-10-05T05:24:08.7526700Z"
created: "2026-10-05T05:24:08.3006370Z"
slug: "decision-tui-plan-d1-d5"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["open-tuikit-upstream-release", "open-tui-plan-remaining"]
version: "1"
salience: "0.5"
---
Decided 2026-10-04 (TUI_APP_PLAN.md): D1 packaging - Armada.Tui is a library hosted by Helm as `armada tui` (one install gives CLI and TUI), not a separate binary. D2 - Armada.Client is generated from the dashboard client/OpenAPI and hand-finished; Helm moves to it over time. D3 - general widgets and fixes go upstream to TUIKit (workstream U); Armada-specific widgets stay in Armada.Tui. D4 - credentials at rest in the OS keychain (macOS Keychain, Windows Credential Manager, libsecret) with a 0600 file fallback. D5 - built in parallel with v1.0; compatibility coverage decided at the first RC.
