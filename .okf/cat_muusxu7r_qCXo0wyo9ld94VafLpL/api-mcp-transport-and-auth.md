---
type: "Reference"
title: "MCP server: endpoints, transports, auth, rate limit"
description: "Voltaic McpHttpServer on McpPort 7891: Streamable HTTP at /mcp (legacy /rpc + /events), plus `armada mcp stdio`; same tools via McpToolRegistrar.RegisterAll; credential headers like REST, unauthenticated only for loopback callers of a loopback-bound listener; Mcp.ToolCallsPerSecond (100, 0 = no limit) per client."
timestamp: "2026-10-05T05:48:55.1291150Z"
created: "2026-10-05T05:48:54.3309220Z"
slug: "api-mcp-transport-and-auth"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["loopback-mcp-unauthenticated", "mcp-tool-authorization-registry", "mcp-tool-caller-scoping", "api-mcp-tool-families", "api-mcp-error-codes"]
version: "1"
salience: "0.5"
---
Code: ArmadaServer (McpHttpServer(mcpHostname, McpPort); "0.0.0.0" becomes "*" for HttpListener; ServerName "Armada", ServerVersion 1.0.0; IncludeToolExceptionMessages = true; RateLimits.ToolCallsPerSecond = settings.Mcp.ToolCallsPerSecond; AuthenticationHandler = AuthenticateMcpRequestAsync; RegisterMcpTools), src/Armada.Server/Mcp/McpToolRegistrar.cs, Armada.Helm McpStdioCommand; docs/MCP_API.md.
Transports: HTTP POST /mcp (JSON-RPC), GET /mcp (SSE notifications), DELETE /mcp (end session); legacy /rpc + /events kept for older clients; stdio via `armada mcp stdio` (runs anonymously as the default tenant admin; no network surface; O-18). Hostname shared with Rest.Hostname; with "localhost" the listener may bind [::1] only, so use localhost not 127.0.0.1.
Auth: Authorization: Bearer, X-Token, or X-Api-Key; a presented invalid credential -> 401 (no anonymous fallback); credential claims (tenantId, userId, isAdmin, isTenantAdmin, authMethod, askThreadId) flow to handlers via Voltaic RpcCallContext (McpToolHelpers.ResolveCallerContext). No credential is accepted only when Mcp.AllowUnauthenticatedLoopback (true) AND Rest.Hostname is loopback AND the caller is loopback; it then runs as the default tenant's tenant admin (decision D2; security memories [[loopback-mcp-unauthenticated]], [[mcp-tool-authorization-registry]]). Guessable credentials count toward login rate limiting (429 + Retry-After). Per-tool permission levels from McpToolAuthorizationRegistry; by-id lookups via McpCallerScope ([[mcp-tool-caller-scoping]]). Rate-limited calls and gate refusals return isError tool results. MCP is not relayed by Armada.Proxy. Client setup: `claude mcp add --transport http --scope user armada http://localhost:7891/mcp` or `armada mcp install`.
