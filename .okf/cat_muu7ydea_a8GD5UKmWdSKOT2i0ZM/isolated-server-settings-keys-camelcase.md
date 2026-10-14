---
type: "Reference"
title: "Isolated Admiral: write settings.json in camelCase and confirm the bound port"
description: "ArmadaSettings reads case-insensitively but writes camelCase (and rewrites the file when it generates an API key); use camelCase (admiralPort/mcpPort) and always confirm \"Admiral running on port N\" before probing."
timestamp: "2026-10-05T05:28:51.8161650Z"
created: "2026-10-04T22:55:57.5798660Z"
slug: "isolated-server-settings-keys-camelcase"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["isolated-server-for-dashboard-testing", "settings-json-loading", "lesson-mcp-host-binding", "pref-protect-owner-environment"]
version: "2"
salience: "0.5"
---
When starting a throwaway Admiral with ARMADA_DATA_DIR=<dir> and `dotnet src/Armada.Server/bin/Debug/net10.0/Armada.Server.dll`, the server loads <dir>/settings.json via ArmadaSettings.LoadAsync (src/Armada.Core/Settings/ArmadaSettings.cs: JsonSerializerOptions with PropertyNameCaseInsensitive=true and PropertyNamingPolicy=CamelCase). On first start, when apiKey is empty, ArmadaServer.EnsureApiKeyAsync generates one and SaveAsync rewrites the whole file in camelCase. On 2026-10-04 a hand-written PascalCase file ({"AdmiralPort": 29010, ...}) was observed to end up rewritten with defaults and the probe bound 7890/7891 (the owner's ports). Code reads PascalCase case-insensitively, so the exact cause was not confirmed; regardless, write camelCase keys exactly as the server writes them ("admiralPort", "mcpPort", "dashboardPath", nested "loginRateLimit": {"maxFailuresPerAccount": 5}), or start once, stop, edit the generated file, and restart.

Always confirm the console line "Admiral running on port N" / "MCP server on port N" before probing, and never use 7890/7891. The MCP listener for hostname localhost binds [::1] only, so probe MCP at http://localhost:<mcpPort>/mcp, not 127.0.0.1 (see lesson-mcp-host-binding).
