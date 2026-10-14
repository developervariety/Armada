---
type: "Reference"
title: "Run an isolated Armada server for dashboard or TUI manual testing"
description: "ARMADA_DATA_DIR=<scratch> with settings.json {admiralPort, mcpPort, dashboardPath} gives a throwaway Admiral; log in as admin@armada/password tenant default; never ~/.armada or 7890."
timestamp: "2026-10-05T05:28:59.2183330Z"
created: "2026-10-04T19:33:46.4449400Z"
slug: "isolated-server-for-dashboard-testing"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["isolated-server-settings-keys-camelcase", "data-directory-and-env", "dashboard-localstorage-keys", "tui-launch-and-options"]
version: "2"
salience: "0.5"
---
Set ARMADA_DATA_DIR to a scratch dir containing settings.json in camelCase, for example {"admiralPort": 17990, "mcpPort": 17991, "dashboardPath": "<worktree>/src/Armada.Dashboard/dist"}, and run `dotnet src/Armada.Server/bin/Debug/net10.0/Armada.Server.dll` (or dotnet run --project src/Armada.Server --framework net10.0). Never use ~/.armada or ports 7890/7891. Confirm "Admiral running on port 17990" in the output.

Auth: POST /api/v1/authenticate {"Email":"admin@armada","Password":"password","TenantId":"default"} returns Token; send it as X-Token. The dashboard keeps it in localStorage armada_session_token. For Playwright set localStorage armada_setup_completed=true and armada_sidebar_collapsed=false; an empty deployment auto-opens the setup wizard. For the TUI: `armada tui --server http://127.0.0.1:17990` (or dotnet run --project src/Armada.Helm -- tui --server ...) with ARMADA_TUI_PREFERENCES pointing at a scratch file.

To avoid launching real agents when seeding: create captains Quarantined or busy and keep missions out of Pending. Dashboard App.css uses CRLF line endings; preserve them when editing via scripts. i18n catalog: src/Armada.Server/wwwroot/i18n/armada.json (locales.<code>.phrases).
