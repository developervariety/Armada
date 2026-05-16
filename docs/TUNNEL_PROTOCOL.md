# Tunnel Protocol

**Version:** 0.8.0

This document describes the shipped tunnel contract between `Armada.Server` and `Armada.Proxy` in `v0.8.0`.

The shipped contract now includes:

- outbound websocket tunnel initiation from Armada
- proxy websocket termination at `/tunnel`
- handshake with protocol version, instance ID, shared-password proof, optional enrollment token, and capability manifest
- request/response correlation IDs
- event forwarding from Armada to the proxy
- bounded remote request routing for summary, work intake, planning, delivery, diagnostics, and reference views
- `ping` / `pong` heartbeat handling
- reconnect with capped exponential backoff and jitter
- proxy stale/offline instance semantics

Not yet shipped:

- subscription lifecycle management
- resumable subscriptions
- chunked streaming for large payloads
- delegated remote identity
- unbounded arbitrary remote action routing or policy evaluation

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
  "timestampUtc": "2026-04-04T18:30:00Z",
  "payload": {
    "protocolVersion": "2026-04-04",
    "armadaVersion": "0.8.0",
    "instanceId": "armada-1f2e3d4c5b6a",
    "enrollmentToken": "optional-token",
    "passwordProofSha256": "9c9be4bdc9b3d11f2c4a9a482d0f36d93fb7357db10ee3119af7c0a0c38e4d54",
    "passwordNonce": "4f3a0c7a8f6c49d9b6711d2c1a7b5e90",
    "passwordTimestampUtc": "2026-04-04T18:30:00Z",
    "capabilities": [
      "remoteControl.handshake",
      "remoteControl.heartbeat",
      "remoteControl.events",
      "remoteControl.requests",
      "instance.summary",
      "objectives.list",
      "planning-session.dispatch",
      "workflow-profile.create",
      "deployment.rollback",
      "captain.tools",
      "workspace.search",
      "status.health",
      "status.snapshot"
    ]
  }
}
```

The `capabilities` array above is representative, not exhaustive. The live manifest now also advertises the wider mission, playbook, backlog, refinement, planning, delivery, diagnostics, and reference route families implemented by the proxy/server pair.

The proxy validates:

- `instanceId` is present
- `protocolVersion` is present
- `passwordProofSha256`, `passwordNonce`, and `passwordTimestampUtc` are present
- the shared-password proof matches the configured `ArmadaProxy.password`
- the password-proof timestamp is fresh enough to reject stale or replayed handshakes
- enrollment token rules from `ArmadaProxy`, when enabled

If either side leaves the password blank, it defaults to `armadaadmin`.

The tunnel does not send the raw shared password. The proof is a SHA-256 value derived from the instance ID, timestamp, nonce, and the password hash.

Accepted handshake response:

```json
{
  "type": "response",
  "correlationId": "5a9b9ed0cc4343e5882e5f4abaf9d0e0",
  "timestampUtc": "2026-04-04T18:30:00Z",
  "statusCode": 200,
  "success": true,
  "message": "Handshake accepted.",
  "payload": {
    "accepted": true,
    "proxyVersion": "0.8.0",
    "protocolVersion": "2026-04-04",
    "instanceId": "armada-1f2e3d4c5b6a",
    "message": "Handshake accepted.",
    "capabilities": [
      "instances.summary",
      "instances.detail",
      "instances.shell.summary",
      "instances.backlog.list",
      "instances.planning.dispatch",
      "instances.workflowprofile.create",
      "instances.deployment.rollback",
      "instances.captain.tools",
      "instances.workspace.search",
      "armada.status.snapshot",
      "armada.status.health"
    ]
  }
}
```

The proxy capability list above is also representative. The live proxy handshake now advertises the expanded Phase 1-4 route families implemented by `Armada.Proxy`.

Rejected handshake response:

```json
{
  "type": "response",
  "correlationId": "5a9b9ed0cc4343e5882e5f4abaf9d0e0",
  "timestampUtc": "2026-04-04T18:30:00Z",
  "statusCode": 401,
  "success": false,
  "errorCode": "handshake_rejected",
  "message": "Handshake shared password proof is invalid."
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

The proxy answers:

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

The proxy currently issues requests across these method families:

- summary and status:
  - `armada.instance.summary`
  - `armada.status.snapshot`
  - `armada.status.health`
- core inspection and management:
  - fleets, vessels, pipelines, playbooks
  - missions, voyages, captains
  - mission log and diff
- work intake:
  - `armada.objectives.*`
  - `armada.backlog.*`
  - `armada.objective-refinement-sessions.*`
  - `armada.objective-refinement-session.*`
  - `armada.planning-sessions.*`
  - `armada.planning-session.*`
- delivery:
  - `armada.workflow-profiles.*`
  - `armada.workflow-profile.*`
  - `armada.check-runs.*`
  - `armada.check-run.*`
  - `armada.environments.*`
  - `armada.environment.*`
  - `armada.releases.*`
  - `armada.release.*`
  - `armada.deployments.*`
  - `armada.deployment.*`
  - `armada.incidents.*`
  - `armada.incident.*`
  - `armada.runbooks.*`
  - `armada.runbook.*`
  - `armada.runbook-executions.*`
  - `armada.runbook-execution.*`
- diagnostics and reference:
  - `armada.captain.tools`
  - `armada.request-history.*`
  - `armada.workspace.*`
  - `armada.personas.*`
  - `armada.persona.*`
  - `armada.prompt-templates.*`
  - `armada.prompt-template.*`

Example request:

```json
{
  "type": "request",
  "correlationId": "62cf28fa232f49a5aab48debe031eb89",
  "method": "armada.planning-session.dispatch",
  "timestampUtc": "2026-04-03T18:32:00Z",
  "payload": {
    "id": "pls_abc123",
    "body": {
      "messageId": "psm_abc123",
      "title": "Proxy backlog dispatch"
    }
  }
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
  "message": "Planning session dispatched.",
  "payload": {
    "id": "vyg_abc123",
    "title": "Proxy backlog dispatch",
    "sourcePlanningSessionId": "pls_abc123"
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

Armada forwards server-side events to the proxy as `event` envelopes.

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

The proxy stores recent inbound events per instance for detail inspection.

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

The proxy computes instance state as:

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

The proxy exposes tunnel-derived state through:

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
