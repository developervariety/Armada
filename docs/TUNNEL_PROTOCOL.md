# Tunnel Protocol

**Version:** 0.6.0

This document describes the shipped tunnel contract between `Armada.Server` and `Armada.ControlPlane` in `v0.6.0`.

`v0.6.0` now ships:

- outbound websocket tunnel initiation from Armada
- control-plane websocket termination at `/tunnel`
- handshake with protocol version, instance ID, enrollment token, and capability manifest
- request/response correlation IDs
- event forwarding from Armada to the control plane
- `ping` / `pong` heartbeat handling
- reconnect with capped exponential backoff and jitter
- control-plane stale/offline instance semantics

Not yet shipped:

- subscription lifecycle management
- resumable subscriptions
- chunked streaming for large payloads
- delegated remote identity
- general-purpose remote action routing

---

## Envelope Shape

All tunnel messages use the same JSON envelope:

```json
{
  "type": "request",
  "correlationId": "5a9b9ed0cc4343e5882e5f4abaf9d0e0",
  "method": "armada.tunnel.handshake",
  "timestampUtc": "2026-04-03T18:30:00Z",
  "statusCode": null,
  "success": null,
  "errorCode": null,
  "message": null,
  "payload": {}
}
```

Recognized `type` values:

- `request`
- `response`
- `event`
- `ping`
- `pong`
- `error`

### Field Rules

- `correlationId` is required for request/response pairing.
- `method` is required for `request` and `event`.
- `statusCode` and `success` are used on `response` and `error`.
- `payload` is optional and JSON-typed.
- `timestampUtc` is optional but included by shipped emitters.

---

## Handshake

The first message from Armada must be:

```json
{
  "type": "request",
  "correlationId": "5a9b9ed0cc4343e5882e5f4abaf9d0e0",
  "method": "armada.tunnel.handshake",
  "timestampUtc": "2026-04-03T18:30:00Z",
  "payload": {
    "protocolVersion": "2026-04-03",
    "armadaVersion": "0.6.0",
    "instanceId": "armada-1f2e3d4c5b6a",
    "enrollmentToken": "optional-token",
    "capabilities": [
      "remoteControl.handshake",
      "remoteControl.heartbeat",
      "remoteControl.events",
      "remoteControl.requests",
      "status.health",
      "status.snapshot",
      "settings.remoteControl"
    ]
  }
}
```

The control plane validates:

- `instanceId` is present
- `protocolVersion` is present
- enrollment token rules from `ArmadaControlPlane`

Accepted handshake response:

```json
{
  "type": "response",
  "correlationId": "5a9b9ed0cc4343e5882e5f4abaf9d0e0",
  "timestampUtc": "2026-04-03T18:30:00Z",
  "statusCode": 200,
  "success": true,
  "message": "Handshake accepted.",
  "payload": {
    "accepted": true,
    "controlPlaneVersion": "0.6.0",
    "protocolVersion": "2026-04-03",
    "instanceId": "armada-1f2e3d4c5b6a",
    "message": "Handshake accepted.",
    "capabilities": [
      "instances.summary",
      "instances.detail",
      "armada.status.snapshot",
      "armada.status.health"
    ]
  }
}
```

Rejected handshake response:

```json
{
  "type": "response",
  "correlationId": "5a9b9ed0cc4343e5882e5f4abaf9d0e0",
  "timestampUtc": "2026-04-03T18:30:00Z",
  "statusCode": 401,
  "success": false,
  "errorCode": "handshake_rejected",
  "message": "Handshake enrollment token is invalid."
}
```

---

## Heartbeats

Armada periodically sends:

```json
{
  "type": "ping",
  "correlationId": "0a8ce9bdb1ea4857a97d8bbd6d388df0",
  "timestampUtc": "2026-04-03T18:31:00Z"
}
```

The control plane answers:

```json
{
  "type": "pong",
  "correlationId": "0a8ce9bdb1ea4857a97d8bbd6d388df0",
  "timestampUtc": "2026-04-03T18:31:00Z"
}
```

Armada records round-trip latency from matching `pong` envelopes.

Armada also responds to inbound `ping` messages with a matching `pong`.

---

## Routed Requests

The control plane currently issues these live requests:

- `armada.status.snapshot`
- `armada.status.health`

Example request:

```json
{
  "type": "request",
  "correlationId": "62cf28fa232f49a5aab48debe031eb89",
  "method": "armada.status.snapshot",
  "timestampUtc": "2026-04-03T18:32:00Z"
}
```

Example response:

```json
{
  "type": "response",
  "correlationId": "62cf28fa232f49a5aab48debe031eb89",
  "timestampUtc": "2026-04-03T18:32:00Z",
  "statusCode": 200,
  "success": true,
  "message": "Armada status snapshot captured.",
  "payload": {
    "totalCaptains": 2,
    "workingCaptains": 1,
    "activeVoyages": 1
  }
}
```

Unsupported requests return:

```json
{
  "type": "response",
  "correlationId": "62cf28fa232f49a5aab48debe031eb89",
  "timestampUtc": "2026-04-03T18:32:00Z",
  "statusCode": 404,
  "success": false,
  "errorCode": "unsupported_method",
  "message": "Unsupported tunnel method armada.unknown."
}
```

---

## Forwarded Events

Armada forwards server-side events to the control plane as `event` envelopes.

Example:

```json
{
  "type": "event",
  "correlationId": "b0f3d61d59d74e5a855f6b2e3953c64f",
  "method": "mission.completed",
  "timestampUtc": "2026-04-03T18:33:00Z",
  "payload": {
    "message": "Mission completed: Update prompt templates",
    "missionId": "msn_abc123",
    "voyageId": "vyg_abc123"
  }
}
```

The control plane stores recent inbound events per instance for detail inspection.

---

## URL Handling

`remoteControl.tunnelUrl` accepts:

- `ws://...`
- `wss://...`
- `http://...`
- `https://...`

`http` is normalized to `ws`, and `https` is normalized to `wss`.

Any other scheme is rejected and surfaced through `RemoteTunnel.LastError`.

---

## Reconnect Behavior

When the tunnel is enabled and a connection attempt fails, Armada:

1. records the failure in `RemoteTunnel.LastError`
2. increments `RemoteTunnel.ReconnectAttempts`
3. waits using capped exponential backoff with jitter
4. retries until the server stops or the feature is disabled

The timing is controlled by:

- `remoteControl.connectTimeoutSeconds`
- `remoteControl.heartbeatIntervalSeconds`
- `remoteControl.reconnectBaseDelaySeconds`
- `remoteControl.reconnectMaxDelaySeconds`

---

## Offline And Stale Semantics

The control plane computes instance state as:

- `connected`: websocket open and recent tunnel activity is within `staleAfterSeconds`
- `stale`: websocket still attached but no tunnel activity has been observed within `staleAfterSeconds`
- `offline`: no active websocket session is attached

---

## Status Surfaces

Armada exposes tunnel state through:

- `GET /api/v1/status`
- `GET /api/v1/status/health`
- `GET /api/v1/settings`
- `armada status`
- the React and legacy server dashboards

The control plane exposes tunnel-derived state through:

- `GET /api/v1/status/health`
- `GET /api/v1/instances`
- `GET /api/v1/instances/{instanceId}`

Current Armada tunnel states:

- `Disabled`
- `Disconnected`
- `Connecting`
- `Connected`
- `Error`
- `Stopping`
