---
type: "Project"
title: "O-20 (Medium, open): any local process can call MCP as the default tenant's tenant admin"
description: "With mcp.allowUnauthenticatedLoopback on (default, D2), local mission captains (no token) and any host process act in the default tenant regardless of the mission's tenant. Planned: per-mission MCP tokens, then default the exception off for multi-tenant."
timestamp: "2026-10-05T05:37:30.3441970Z"
created: "2026-10-05T05:37:29.9164570Z"
slug: "open-o20-loopback-mcp-default-tenant"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["loopback-mcp-unauthenticated", "open-o04-harbor-mcp-credential", "decision-v1-mcp-auth-d2", "mcp-tool-caller-scoping"]
version: "1"
salience: "0.5"
---
Any process on the Admiral host can call MCP without a credential as the default tenant's tenant admin. That includes local mission captains (they get no token, so their calls act in the default tenant whatever the mission's tenant) and, in principle, an Ask captain that avoids its configured token. Mitigations: since F-25 the caller is confined to the default tenant; Ask turns use a strict per-turn MCP config with the thread token (F-31 makes it win), and with auto-approve off (F-27) the CLI cannot run arbitrary shell to reach the port another way. Not changed because the README's local Claude Code setup relies on it (D2). Guidance: operators exposing Ask to untrusted users or running several tenants should set mcp.allowUnauthenticatedLoopback false and give captains credentials. Planned W1 follow-up: mission-scoped MCP tokens for local captains (same token type as O-04), then default the loopback exception off for multi-tenant installs. Status 2026-10-04: open.
