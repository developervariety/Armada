---
type: "Project"
title: "O-03 (Medium, open): GET /api/v1/status and the WebSocket snapshot are server-wide"
description: "Captain and mission counts, active voyage titles, and last signals of every tenant reach any authenticated user via /status, the subscribe snapshot, and the MCP status tool. Owner: W2.3."
timestamp: "2026-10-05T05:36:04.3558010Z"
created: "2026-10-05T05:36:04.0987280Z"
slug: "open-o03-status-server-wide"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["websocket-authentication", "security-review-living-document"]
version: "1"
salience: "0.5"
---
GET /api/v1/status (Status:Read, Authenticated), the WebSocket subscribe snapshot, and the MCP status tool return a server-wide shape: captain and mission counts, active voyage titles, and the last signals of every tenant. In a multi-tenant install any authenticated user learns other tenants' activity. Planned fix W2.3: scope the status shape per caller. Status as of 2026-10-04: open. Do not add more cross-tenant fields to these payloads.
