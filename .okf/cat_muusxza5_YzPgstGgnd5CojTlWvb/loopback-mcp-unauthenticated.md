---
type: "Reference"
title: "MCP is authenticated by default with a loopback exception (D2)"
description: "Unauthenticated MCP calls are accepted only when the listener is loopback-bound and the caller is on loopback with mcp.allowUnauthenticatedLoopback true; they act as the default tenant's tenant admin; invalid credentials get 401."
timestamp: "2026-10-05T05:34:11.8986630Z"
created: "2026-10-05T05:34:11.5357900Z"
slug: "loopback-mcp-unauthenticated"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["decision-v1-mcp-auth-d2", "open-o20-loopback-mcp-default-tenant", "mcp-tool-authorization-registry", "lesson-mcp-host-binding"]
version: "1"
salience: "0.5"
---
McpSettings.AllowUnauthenticatedLoopback (src/Armada.Core/Settings/McpSettings.cs, default true): MCP calls without credentials are accepted only while the MCP listener hostname is loopback (localhost, 127.0.0.1, ::1) and the caller connects from loopback; such callers act as the default tenant's tenant admin, which keeps the README's local Claude Code setup working without a token. When false, or whenever the listener is bound to a non-loopback hostname, every MCP call needs a credential (Authorization: Bearer, X-Token, or X-Api-Key). A presented but invalid credential gets 401 (no silent fallback; F-02). Since F-25 the loopback caller is confined to the default tenant; admin-only tools (backup, restore, stop_server) still need a credential. Residual risk O-20: any local process, including local mission captains (which get no token), acts in the default tenant. Multi-tenant or untrusted-Ask installs should set allowUnauthenticatedLoopback false and give captains credentials. Docker exposes MCP on a non-loopback listener, so host clients must send a header (claude mcp add --transport http armada http://localhost:7891/mcp --header "Authorization: Bearer <token>").
