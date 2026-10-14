---
type: "Reference"
title: "How captains reach Armada MCP: generated configs and the localhost:McpPort/mcp URL"
description: "ArmadaMcpConfigBuilder emits per-runtime MCP configs pointing at http://localhost:<McpPort>/mcp (keyed mcpServers JSON for Claude/Gemini/Cursor, servers array for Mux, TOML for Codex), optionally with an X-Token header; Harbors get Harbor.AdvertisedMcpBaseUrl."
timestamp: "2026-10-05T05:35:15.0386110Z"
created: "2026-10-05T05:35:14.5321280Z"
slug: "rt-mcp-config-generation"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-launch-isolation", "rt-ask-thread-mcp-token", "api-mcp-transport-and-auth"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/ArmadaMcpConfigBuilder.cs (pure, unit-tested). GetMcpUrl(mcpPort) = "http://localhost:" + port + "/mcp" (Voltaic Streamable HTTP endpoint; same URL `armada mcp install` writes via Armada.Helm/Commands/McpConfigHelper.cs). Builders:
- BuildKeyedMcpServersJson(port, sessionToken?) -> {"mcpServers":{"armada":{"type":"http","url":...,"headers":{"X-Token":...}}}} for Claude Code, Gemini, Cursor.
- BuildMuxServersJson(port) -> {"servers":[{"name":"armada","transport":"http","url":"http://localhost:<port>","mcpPath":"/mcp"}]}.
- BuildCodexConfigToml(port, token?) -> [mcp_servers.armada] url = ... plus http_headers X-Token.
Host and port: always "localhost" and settings McpPort (default 7891); AgentLifecycleHandler passes mcpPort to StartAsync. Caveat: with Rest.Hostname "localhost" the MCP HttpListener may bind [::1] only, so tooling should use "localhost" rather than 127.0.0.1. In split mode the Admiral advertises Harbor.AdvertisedMcpBaseUrl (or GetMcpUrl(McpPort)) to Harbors in handshakeAck. Without isolation, captains rely on the user's own MCP config written by `armada mcp install` (managed block markers <!-- armada:mcp:begin/end --> in instruction files). Unauthenticated loopback MCP calls run as the default tenant's tenant admin.
