---
type: "Reference"
title: "Dashboard WebSocket: WebSocketContext over lib/armadaSocket.ts"
description: "One socket per dashboard, token passed as ?token= (browsers cannot set headers), exponential backoff 1 s to 30 s with jitter, onReconnected lets pages refetch; events fan out to subscribers."
timestamp: "2026-10-05T05:41:20.4166260Z"
created: "2026-10-05T05:41:19.8150720Z"
slug: "dashboard-websocket"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["websocket-authentication", "tui-notifications", "tui-parity-manifest"]
version: "1"
salience: "0.5"
---
src/Armada.Dashboard/src/lib/armadaSocket.ts is React-free (unit-tested with a fake socket in armadaSocket.test.ts): the server requires authentication at upgrade, and browsers cannot set Authorization on a WebSocket, so the session token travels as the token query parameter on /ws; dropped connections reconnect with exponential backoff (1 s, 2 s, 4s ... capped at 30 s, with jitter), reset once open; every open after the first fires onReconnected so pages refetch what they missed. src/context/WebSocketContext.tsx (WebSocketProvider) owns the single connection and fans every event out to subscribers (tested in WebSocketContext.test.tsx). Events are tenant scoped by the server (ask.* to the owning user). Event types the dashboard handles (28 plus status.snapshot) include mission.*, voyage.*, captain.*, deployment.*, objective.*, incident.*, planning-session.*, objective-refinement-session.*, ask.*; the parity generator extracts them with a regex, so new event types must be added to the TUI. Fragility R4: route events by EntityType, not by text.
