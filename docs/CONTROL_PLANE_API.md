# Control Plane API

**Version:** 0.6.0

This document describes the first shipped Armada control-plane API surface in `v0.6.0`.

`v0.6.0` includes:

- websocket tunnel termination at `/tunnel`
- in-memory instance registration keyed by `instanceId`
- live instance summary and detail endpoints
- live request/response forwarding for Armada health and status snapshots

`v0.6.0` does not yet include:

- SaaS user accounts
- enrollment workflows beyond static token validation
- delegated identity or remote authorization mapping
- notification inboxes
- guarded remote action APIs

---

## Default Bind

The control plane binds to:

- Host: `localhost`
- Port: `7893`

Configuration is read from the `ArmadaControlPlane` section:

```json
{
  "ArmadaControlPlane": {
    "hostname": "localhost",
    "port": 7893,
    "requireEnrollmentToken": false,
    "enrollmentTokens": [],
    "handshakeTimeoutSeconds": 15,
    "staleAfterSeconds": 90,
    "requestTimeoutSeconds": 20,
    "maxRecentEvents": 50
  }
}
```

---

## REST Endpoints

### GET /api/v1/status/health

Returns control-plane process health and instance counts.

```json
{
  "healthy": true,
  "product": "Armada.ControlPlane",
  "version": "0.6.0",
  "protocolVersion": "2026-04-03",
  "port": 7893,
  "startedUtc": "2026-04-03T21:00:00Z",
  "instances": {
    "total": 2,
    "connected": 1,
    "stale": 1,
    "offline": 0
  }
}
```

### GET /api/v1/instances

Returns summary rows for all known instances.

```json
{
  "count": 1,
  "instances": [
    {
      "instanceId": "armada-1f2e3d4c5b6a",
      "state": "connected",
      "armadaVersion": "0.6.0",
      "protocolVersion": "2026-04-03",
      "capabilities": [
        "remoteControl.handshake",
        "remoteControl.heartbeat",
        "remoteControl.events",
        "remoteControl.requests",
        "status.health",
        "status.snapshot",
        "settings.remoteControl"
      ],
      "remoteAddress": "127.0.0.1",
      "firstSeenUtc": "2026-04-03T21:00:00Z",
      "connectedUtc": "2026-04-03T21:00:00Z",
      "lastSeenUtc": "2026-04-03T21:02:00Z",
      "lastEventUtc": "2026-04-03T21:01:30Z",
      "lastDisconnectUtc": null,
      "lastError": null,
      "recentEventCount": 3,
      "pendingRequestCount": 0
    }
  ]
}
```

`state` can be:

- `connected`
- `stale`
- `offline`

### GET /api/v1/instances/{instanceId}

Returns the current summary plus recent inbound event history for an instance.

```json
{
  "summary": {
    "instanceId": "armada-1f2e3d4c5b6a",
    "state": "connected",
    "armadaVersion": "0.6.0",
    "protocolVersion": "2026-04-03",
    "capabilities": [
      "remoteControl.handshake",
      "remoteControl.heartbeat",
      "remoteControl.events",
      "remoteControl.requests",
      "status.health",
      "status.snapshot",
      "settings.remoteControl"
    ],
    "remoteAddress": "127.0.0.1",
    "firstSeenUtc": "2026-04-03T21:00:00Z",
    "connectedUtc": "2026-04-03T21:00:00Z",
    "lastSeenUtc": "2026-04-03T21:02:00Z",
    "lastEventUtc": "2026-04-03T21:01:30Z",
    "lastDisconnectUtc": null,
    "lastError": null,
    "recentEventCount": 3,
    "pendingRequestCount": 0
  },
  "recentEvents": [
    {
      "method": "mission.completed",
      "correlationId": "b0f3d61d59d74e5a855f6b2e3953c64f",
      "message": null,
      "timestampUtc": "2026-04-03T21:01:30Z",
      "payload": {
        "message": "Mission completed: Update prompt templates",
        "missionId": "msn_abc123"
      }
    }
  ]
}
```

### GET /api/v1/instances/{instanceId}/status/snapshot

Sends a live tunnel request to the connected Armada instance using method `armada.status.snapshot`.

```json
{
  "correlationId": "62cf28fa232f49a5aab48debe031eb89",
  "success": true,
  "statusCode": 200,
  "errorCode": null,
  "message": "Armada status snapshot captured.",
  "payload": {
    "totalCaptains": 2,
    "idleCaptains": 1,
    "workingCaptains": 1,
    "stalledCaptains": 0,
    "activeVoyages": 1,
    "missionsByStatus": {
      "Pending": 2,
      "InProgress": 1
    },
    "voyages": [],
    "recentSignals": [],
    "remoteTunnel": {
      "enabled": true,
      "state": "Connected",
      "tunnelUrl": "wss://control-plane.example.com/tunnel",
      "instanceId": "armada-1f2e3d4c5b6a",
      "lastError": null,
      "reconnectAttempts": 0,
      "latencyMs": 42
    },
    "timestampUtc": "2026-04-03T21:02:00Z"
  }
}
```

### GET /api/v1/instances/{instanceId}/health

Sends a live tunnel request to the connected Armada instance using method `armada.status.health`.

```json
{
  "correlationId": "a81ec0a5ee024679b719046d1bf8de85",
  "success": true,
  "statusCode": 200,
  "errorCode": null,
  "message": "Armada health snapshot captured.",
  "payload": {
    "status": "healthy",
    "timestamp": "2026-04-03T21:02:00Z",
    "startUtc": "2026-04-03T20:00:00Z",
    "uptime": "0.01:02:00",
    "version": "0.6.0",
    "ports": {
      "admiral": 7890,
      "mcp": 7891,
      "webSocket": 7892
    },
    "remoteTunnel": {
      "enabled": true,
      "state": "Connected",
      "tunnelUrl": "wss://control-plane.example.com/tunnel",
      "instanceId": "armada-1f2e3d4c5b6a",
      "lastError": null,
      "reconnectAttempts": 0,
      "latencyMs": 42
    }
  }
}
```

If the instance is offline, the live endpoints return a `400` response with:

```json
{
  "error": "Instance armada-1f2e3d4c5b6a is not connected."
}
```

---

## Tunnel Endpoint

### GET /tunnel

`/tunnel` is a websocket endpoint, not a normal REST resource.

Expected first message:

- `type = request`
- `method = armada.tunnel.handshake`

See [docs/TUNNEL_PROTOCOL.md](TUNNEL_PROTOCOL.md) for envelope details.

---

## Current Guardrails

- the registry is process-local and in-memory
- only static enrollment-token validation is supported
- routed requests currently support:
  - `armada.status.snapshot`
  - `armada.status.health`
- recent event history is bounded by `maxRecentEvents`
- there is no authn/authz layer on control-plane REST yet; this is still an implementation-stage service
