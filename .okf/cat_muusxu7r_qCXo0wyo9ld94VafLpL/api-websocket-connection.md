---
type: "Reference"
title: "WebSocket /ws: connecting, authenticating, message envelope"
description: "ws://localhost:7890/ws (wss with Rest.Ssl); authenticate with REST headers, ?token=<session|bearer|apikey> (browsers), or Sec-WebSocket-Protocol armada-token.<base64url>; client sends {\"Route\":\"subscribe\"|\"command\",...}; server sends camelCase {type, message?, data, timestamp}."
timestamp: "2026-10-05T05:49:54.1814010Z"
created: "2026-10-05T05:49:53.1169070Z"
slug: "api-websocket-connection"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["arch-websocket-hub", "websocket-authentication", "api-websocket-events", "api-websocket-commands"]
version: "1"
salience: "0.5"
---
docs/WEBSOCKET_API.md; contract class src/Armada.Server/WebSocket/WebSocketSurface.cs; internals [[arch-websocket-hub]]; auth enforcement [[websocket-authentication]].
Endpoint: same port as REST, path /ws; port discoverable via GET /api/v1/status/health. Auth (upgrade refused with 401 before the handshake if missing/invalid): (1) Authorization/X-Token/X-Api-Key headers (non-browser), (2) ?token= percent-encoded session token, bearer token, or API key (recommended for browsers; session tokens contain + / =), (3) Sec-WebSocket-Protocol entry armada-token.<base64url(token)> (Watson 7.2 does not echo the subprotocol, so browsers must use the query form). Thread-scoped Ask tokens refused.
Client messages: Route (subscribe | command), action, id, data, query, entityType, captainId, lines, offset, outputPath, filePath, status, allTenants; field names case-insensitive. Server envelope: type, message (generic events only, not stable), data, timestamp (ISO UTC); command replies command.result / command.error {type, action, data, error}. Unknown action -> command.error "Unknown action: <action>". subscribe -> immediate status.snapshot (ArmadaStatus + allTenants); global admins may send {"Route":"subscribe","AllTenants":true}. Casing: everything server-sent is camelCase (differs from REST PascalCase). Clients must ignore unknown event types/fields (minor releases add them). Through Armada.Proxy the token query/subprotocol are forwarded.
