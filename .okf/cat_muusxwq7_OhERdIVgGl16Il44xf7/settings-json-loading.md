---
type: "Reference"
title: "settings.json: loading, camelCase keys, validation, restart to apply"
description: "Admiral reads <dataDir>/settings.json into ArmadaSettings (case-insensitive read, camelCase write, setters validate ranges); edits apply on next start; PUT /api/v1/settings and the Server page also save it."
timestamp: "2026-10-05T05:29:26.9085400Z"
created: "2026-10-05T05:29:26.6106880Z"
slug: "settings-json-loading"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["data-directory-and-env", "important-settings-keys", "isolated-server-settings-keys-camelcase", "settings-validation-in-setters"]
version: "1"
salience: "0.5"
---
Armada.Server Program.RunServerAsync loads Path.Combine(Constants.DefaultDataDirectory, "settings.json") with ArmadaSettings.LoadAsync (src/Armada.Core/Settings/ArmadaSettings.cs). A missing file yields defaults. JSON options: PropertyNameCaseInsensitive, CamelCase naming, indented, nulls omitted. Setters validate (for example admiralPort/mcpPort must be 1-65535 or ArgumentOutOfRangeException; many values clamp). The server rewrites the file when it generates the local apiKey on first start and when settings are saved through PUT /api/v1/settings or the dashboard/TUI Server page. Hand edits take effect on the next server start.

Nested sections (each its own class in Armada.Core/Settings): database (DatabaseSettings), rest (RestSettings: hostname, ssl), mcp (McpSettings), loginRateLimit, messageTemplates, remoteControl, telemetry, harbor (HarborServerSettings), import (VesselImportSettings), ask (AskSettings), fleetActions, repositoryHealth, retention, agents, escalationRules. Docker mounts docker/armada/armada.json at /app/data/settings.json. A gold default copy lives at factory/settings.json. The CLI has `armada config show|set|init` for its own view of the same file.
