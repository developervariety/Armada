---
type: "Project"
title: "MCP tools must resolve by-id entities through McpCallerScope"
description: "Every MCP tool taking an entity id uses McpCallerScope.Read*Async (REST rule); E2E.McpTenantIsolation fails on unmapped new by-id tools."
timestamp: "2026-10-04T23:19:33.6273340Z"
created: "2026-10-04T23:19:30.6975960Z"
slug: "mcp-tool-caller-scoping"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
version: "1"
salience: "0.5"
---
Since W1.9 (O-01/F-25), src/Armada.Server/Mcp/Tools/McpCallerScope.cs resolves entities for MCP tools with the REST rule: global admin any tenant, tenant admin own tenant, regular user own entities; out-of-scope ids return null so tools answer the same {"Error":"X not found"} as for a missing id. Referenced ids on create/update (vesselId, voyageId, fleetId, captainId, objectiveId, memory source ids) must also be validated through it. Secondary lookups of entities owned by an already-authorized parent (mission.CaptainId, mission.DockId) use raw reads. The generic test E2E.McpTenantIsolation (src/Test.Shared/Suites/E2E/McpTenantIsolationSuite.cs) lists every tool via tools/list, maps id-like schema args to seeded tenant A ids, calls as tenant B, and fails if a new by-id tool's id argument cannot be mapped (seed it or add it to _UnseededIdTools). McpToolClient.ListToolsAsync follows nextCursor (tools/list pages at 100). The MCP server rate-limits 100 tool calls/second.
