---
type: "Reference"
title: "MCP authorization: McpToolAuthorizationRegistry wraps every tool"
description: "Each MCP tool has one requirement line in Armada.Core/Authorization/McpToolAuthorizationRegistry.cs; reads Authenticated, writes/executions TenantAdmin, backup/restore/stop_server AdminOnly; plus McpCallerScope for by-id entities."
timestamp: "2026-10-05T05:33:13.3395820Z"
created: "2026-10-05T05:33:13.1019090Z"
slug: "mcp-tool-authorization-registry"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["mcp-tool-caller-scoping", "authorization-coverage-test", "loopback-mcp-unauthenticated", "route-authorization-registry"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Authorization/McpToolAuthorizationRegistry.cs holds one line per tool name. Every registered tool is wrapped so the requirement is checked against the caller of each call: the MCP request's credential, the loopback default context, or the Ask Armada caller an approved proposal runs as. Levels (W1): reads and caller-owned writes (memories, model endpoints, harbors) Authenticated; other writes and executions TenantAdmin (persona, pipeline, prompt template, playbook writes too, because the MCP tools are not owner-scoped like REST); backup, restore, stop_server AdminOnly (a credential is required even on loopback, so the loopback default caller cannot use them). Before W1 no MCP tool checked permissions (F-03). Tenant scoping of by-id entities is a second layer: McpCallerScope (see mcp-tool-caller-scoping). The MCP server rate-limits tool calls per client (mcp.toolCallsPerSecond, default 100, 0 disables; over-limit calls get isError results). New tools need a registry line or E2E.AuthorizationCoverage fails, and a by-id argument the E2E.McpTenantIsolation suite can map.
