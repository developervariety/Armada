---
type: "Project"
title: "Settings: nested config objects with validation in setters"
description: "Settings classes in Armada.Core/Settings are nested objects whose property setters validate or clamp (throw ArgumentOutOfRangeException or clamp to documented ranges); configurable values are members, not constants."
timestamp: "2026-10-05T05:43:42.8341100Z"
created: "2026-10-05T05:43:42.5141350Z"
slug: "settings-validation-in-setters"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["settings-json-loading", "api-surface-generator-and-contract-test", "tui-parity-manifest"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md Patterns): settings are nested config objects (ArmadaSettings with DatabaseSettings, RestSettings, McpSettings, LoginRateLimitSettings, RemoteControlSettings, TelemetrySettings, HarborServerSettings, AskSettings, FleetActionSettings, RepositoryHealthSettings, RetentionSettings, VesselImportSettings, MessageTemplateSettings) with validation in setters: e.g. AdmiralPort throws ArgumentOutOfRangeException outside 1-65535; LoginRateLimitSettings clamps maxFailuresPerAccount 1-1000, windowMinutes 1-1440; McpSettings.ToolCallsPerSecond clamped 0-1000000; null section setters fall back to defaults. Each setting has an XML doc stating default and range. Configurable values are members rather than hard-coded constants. Adding a settings key changes the API surface (settings keys are part of docs/api-surface-1.0.json), so regenerate the baseline; the TUI parity manifest also tracks Server-page settings fields.
