---
type: "Reference"
title: "TUI preferences: ~/.armada/tui.json (no secrets) and server profiles"
description: "tui.json holds profiles (name, URL, last user/tenant/login method), theme, Glyphs, language, sidebar/dock state, last route, per-table columns and page sizes, refresh intervals, notifications, KeyBindings, Telemetry; ARMADA_TUI_PREFERENCES moves it."
timestamp: "2026-10-05T05:38:59.0447770Z"
created: "2026-10-05T05:38:58.5415150Z"
slug: "tui-preferences-and-profiles"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-credential-store", "tui-login-and-localhost-prefill", "tui-telemetry", "test-temp-profile-sandbox"]
version: "1"
salience: "0.5"
---
Services/PreferencesService.cs with TuiPreferences, ServerProfile, TablePreferences, WorkspacePreferences, TuiTelemetrySettings. File: ~/.armada/tui.json (data dir follows ARMADA_DATA_DIR; TuiPaths), or the path in ARMADA_TUI_PREFERENCES (tests always set this via TestTemp). Contents: server profiles (name, URL, last user, last tenant, last login method), theme (Dark/Light/HighContrast/Auto), Glyphs (Auto/Unicode/ASCII), language, sidebar and Ask dock state, last route and last Ask thread, per-table columns, sort, and page size, auto-refresh intervals per screen (off, 15, 30, 60, 120, 180, 300 s), notification settings including OsNotifications (Osc9, Osc777, Native), KeyBindings (command id to chord), Telemetry section, Ask show-thinking and draft captain. The file never holds a secret: tokens go to the credential store. Profile switching: login Server picker (Add server, e to edit) or File > Switch server profile. The first-run profile follows the local admiralPort until edited.
