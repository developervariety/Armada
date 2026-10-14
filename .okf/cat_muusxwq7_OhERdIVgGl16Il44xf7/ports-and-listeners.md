---
type: "Reference"
title: "Ports: 7890 REST/dashboard/WebSocket/Harbor link, 7891 MCP, 9464 metrics, 7893 proxy"
description: "Admiral 7890 (REST, /dashboard, /ws, /v1.0/harbor/connect, /openapi.json), 7891 MCP (/mcp), 9464 Prometheus when telemetry on; proxy 7893; Docker extras 3000/9090/3100/3001."
timestamp: "2026-10-05T05:29:44.7254680Z"
created: "2026-10-05T05:29:44.3622310Z"
slug: "ports-and-listeners"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["important-settings-keys", "tls-and-network-exposure", "lesson-mcp-host-binding", "proxy-operations"]
version: "1"
salience: "0.5"
---
Defaults (all configurable):
- 7890 Admiral (admiralPort, rest.hostname): REST API, OpenAPI /openapi.json and /swagger, React dashboard /dashboard, WebSocket /ws, Harbor link /v1.0/harbor/connect, health /api/v1/status/health.
- 7891 Admiral MCP (mcpPort, same rest.hostname): /mcp. With hostname localhost the HttpListener binds [::1] only on macOS, so use http://localhost:7891/mcp, not 127.0.0.1 (lesson-mcp-host-binding). 0.0.0.0 is mapped to * for the MCP listener (F-20).
- 9464 Prometheus /metrics (telemetry.prometheusPort) only when telemetry.enabled.
- 7893 Armada.Proxy (portal, relayed dashboard, /tunnel).
- Docker extras: 3000 standalone dashboard (nginx container port 8080), 9090 Prometheus, 3100 Loki, 3001 Grafana (admin/admin).
- 514/udp outbound syslog to 127.0.0.1 by default.
- TUI Prometheus (optional) 9465.
rest.hostname localhost keeps 7890 and 7891 off the network. If you change admiralPort, export ARMADA_BASE_URL (for example http://localhost:9000) before running install/update/healthcheck scripts. "Address in use" at start usually means a second Armada; the Admiral fails startup if the MCP port is taken.
