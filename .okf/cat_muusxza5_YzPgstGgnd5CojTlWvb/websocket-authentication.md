---
type: "Reference"
title: "WebSocket /ws authentication and tenant-scoped events"
description: "The /ws upgrade requires a credential (browsers send the session token as ?token=); every command requires a global admin; events reach only sockets of the entity's tenant (ask.* only the owning user); allTenants for global admins only."
timestamp: "2026-10-05T05:35:41.6947860Z"
created: "2026-10-05T05:35:41.4341500Z"
slug: "websocket-authentication"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["open-o03-status-server-wide", "open-o13-unscrubbed-broadcasts", "dashboard-websocket"]
version: "1"
salience: "0.5"
---
The WebSocket hub on the Admiral port at /ws requires a credential at upgrade. Browsers cannot set Authorization on a WebSocket, so the dashboard and TUI pass the session token as the token query parameter (request logs omit query strings, F-15). Every hub command (list/get/create/update/delete vessels, backup, restore, and so on) requires a global admin. Server broadcasts (mission, voyage, captain, check-run, objective, deployment, incident, runbook-execution, approval-needed, ask.*) reach only sockets in the entity's tenant; ask.* go to the owning user only; the subscribe route's allTenants flag is honored for global admins only. Ask thread tokens are refused on WebSocket. Open items: the subscribe status snapshot is server-wide (O-03); planning-session tool output and check-run output are broadcast unscrubbed within the tenant (O-13); the upgrade is not login rate limited (O-05). Test: E2E.WebSocketSecurity.
