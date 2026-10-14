---
type: "Project"
title: "O-18 (Low, accepted): `armada mcp stdio` has no authentication"
description: "The Helm stdio MCP server opens the database directly as the OS user with the same tool handlers but no MCP authorization wrapper, in the default tenant; trust boundary is the OS account that can read ~/.armada."
timestamp: "2026-10-05T05:37:19.8687020Z"
created: "2026-10-05T05:37:19.6268520Z"
slug: "open-o18-stdio-mcp-no-auth"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["helm-cli-server-commands", "mcp-tool-authorization-registry"]
version: "1"
salience: "0.5"
---
`armada mcp stdio` (Helm) runs the MCP tool handlers in-process over stdio, opening the Armada database directly, without the McpToolAuthorizationRegistry wrapper; it acts in the default tenant. Accepted: anyone who can run it as that OS user can already read ~/.armada (database, settings, API key). Status 2026-10-04: accepted. Prefer the HTTP MCP server (port 7891) for agents so authorization applies.
