---
type: "Project"
title: "Generate MCP client URLs from the bound host (in progress)"
description: "Branch work/fx-mcphost: ClientHostFor(restHostname) threaded explicitly (no static state), localhost/127.0.0.1/::1 treated as the same Armada server, startup warning for loopback IP literals."
timestamp: "2026-10-05T05:26:30.2705300Z"
created: "2026-10-05T05:26:30.0323510Z"
slug: "open-mcp-client-host"
category: "cat_muusyecr_sLYyb3rdwRqCNn0kT8M"
links: ["lesson-mcp-host-binding"]
version: "1"
salience: "0.5"
---
Status 2026-10-05: an agent is implementing the product fix for lesson-mcp-host-binding on work/fx-mcphost: ArmadaMcpConfigBuilder.ClientHostFor(restHostname) (wildcards to localhost, IPv6 bracketed), host threaded through builder overloads and planner request objects (McpHost, default localhost), server and Helm pass the configured host, tool catalog treats localhost/127.0.0.1/::1 on the MCP port as the Armada server, and a startup Warn when rest.hostname is 127.0.0.1 or ::1; plus an E2E test that a generated URL answers initialize. A first attempt with a static ClientHost was reverted because process-wide state makes tests order-dependent.
