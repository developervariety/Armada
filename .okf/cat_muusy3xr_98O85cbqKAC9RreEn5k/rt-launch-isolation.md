---
type: "Reference"
title: "IsolateCaptainLaunch: scoped config so captains see only Armada's MCP server"
description: "With settings IsolateCaptainLaunch=true (default false), CaptainLaunchIsolationPlanner writes a scoped config dir (temp/armada/isolation/<captainId>) containing only the Armada MCP server and points the CLI at it; OpenCode is not covered."
timestamp: "2026-10-05T05:35:22.0806050Z"
created: "2026-10-05T05:35:21.7332160Z"
slug: "rt-launch-isolation"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-mcp-config-generation", "rt-ask-thread-mcp-token"]
version: "1"
salience: "0.5"
---
Planner: src/Armada.Core/Services/CaptainLaunchIsolationPlanner.cs (pure; returns CaptainLaunchIsolationPlan with extra args, env overrides, IsolationConfigFile list). Applied by BaseAgentRuntime when StartAsync(isolateLaunch: true). Setting ArmadaSettings.IsolateCaptainLaunch (default false: the CLI uses the user's normal configuration as written by `armada mcp install`).

Per runtime: Claude Code --setting-sources project,local --strict-mcp-config --mcp-config <dir>/armada-mcp.json; Codex config.toml + CODEX_HOME; Gemini .gemini/settings.json + HOME/USERPROFILE redirect; Cursor .cursor/mcp.json + HOME/USERPROFILE; Mux mcp-servers.json + MUX_CONFIG_DIR; OpenCode none (always user config); ApiEndpoint not applicable. Scoped dir: <system temp>/armada/isolation/<captainId>. When BaseAgentRuntime has a session token (thread-scoped Ask token), the scoped config carries X-Token and is written to a per-launch directory deleted when the process exits so concurrent launches do not collide. Isolation blocks the user's global MCP servers and settings but not the filesystem; it is not a sandbox.
