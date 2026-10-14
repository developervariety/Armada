---
type: "Project"
title: "MCP listener answers only its bound host name (localhost vs 127.0.0.1)"
description: "Bound as 127.0.0.1, http://localhost:7891/mcp returns 404; bound as localhost, 127.0.0.1 is refused. Armada-generated MCP URLs use localhost, so rest.hostname must match."
timestamp: "2026-10-05T05:25:14.0862480Z"
created: "2026-10-05T05:25:13.4592220Z"
slug: "lesson-mcp-host-binding"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
links: ["open-mcp-client-host", "lesson-test-overwrote-user-settings"]
version: "1"
salience: "0.5"
---
Found 2026-10-05 when Ask Armada reported Claude Code not connected to Armada MCP although Claude Code was configured. The Voltaic McpHttpServer (created in ArmadaServer with Settings.Rest.Hostname) only serves requests whose Host matches its bind name: with rest.hostname "127.0.0.1", curl http://localhost:7891/mcp -> 404 and http://127.0.0.1:7891/mcp -> 200; with "localhost", 127.0.0.1 -> connection refused. REST (Watson, port 7890) accepts both. Every MCP client URL Armada generates (ArmadaMcpConfigBuilder.GetMcpUrl, CaptainThreadMcpPlanner Mux url, Helm McpConfigHelper used by `armada mcp install`) hard-codes localhost, and Codex captains logged "rmcp ... HTTP 404". Root cause in this case: the owner's settings restore kept the test value 127.0.0.1; fix was setting rest.hostname back to the default "localhost" and restarting the server. Product fix in progress (open-mcp-client-host): generate client URLs from the bound host, recognize localhost/127.0.0.1/::1 as the same Armada server, warn at startup for loopback IP literals. Diagnose with GET /api/v1/captains/{id}/tools (Servers[].Status "Unreachable at query time").
