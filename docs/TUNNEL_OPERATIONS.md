# Tunnel Operations

**Version:** 0.6.0

This guide covers the shipped remote-control tunnel and control-plane MVP surfaces in Armada `v0.6.0`.

---

## Scope

`v0.6.0` now includes:

- the Armada-side outbound websocket tunnel client
- remote tunnel configuration in Armada settings and dashboards
- a minimal `Armada.ControlPlane` service with websocket termination and instance registry APIs
- live forwarded status/health requests from the control plane into a connected Armada instance

Still not included:

- user-facing SaaS auth
- delegated identity or local-session brokerage
- guarded remote actions
- notification delivery
- persistent control-plane storage

Treat the current control plane as an implementation-stage operator service, not a hardened public SaaS surface.

---

## Armada Instance Configuration

Armada stores remote tunnel configuration in `settings.json`:

```json
{
  "remoteControl": {
    "enabled": false,
    "tunnelUrl": null,
    "instanceId": null,
    "enrollmentToken": null,
    "connectTimeoutSeconds": 15,
    "heartbeatIntervalSeconds": 30,
    "reconnectBaseDelaySeconds": 5,
    "reconnectMaxDelaySeconds": 60,
    "allowInvalidCertificates": false
  }
}
```

### Recommendations

- Leave `enabled` off unless you are actively testing the tunnel.
- Prefer `wss://` endpoints outside local development.
- Leave `instanceId` empty unless you want an operator-friendly override.
- Use `allowInvalidCertificates = true` only for local development with self-signed certificates.

---

## Control Plane Configuration

`Armada.ControlPlane` reads configuration from `ArmadaControlPlane`:

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

### Key Fields

- `hostname`
- `port`
- `requireEnrollmentToken`
- `enrollmentTokens`
- `handshakeTimeoutSeconds`
- `staleAfterSeconds`
- `requestTimeoutSeconds`
- `maxRecentEvents`

---

## Starting The Control Plane

From the repo root:

```powershell
dotnet run --project src/Armada.ControlPlane/Armada.ControlPlane.csproj --framework net10.0
```

Default endpoints:

- health: `http://localhost:7893/api/v1/status/health`
- instance list: `http://localhost:7893/api/v1/instances`
- tunnel websocket: `ws://localhost:7893/tunnel`

Point Armada at the control plane by setting:

```json
{
  "remoteControl": {
    "enabled": true,
    "tunnelUrl": "ws://localhost:7893/tunnel",
    "enrollmentToken": null
  }
}
```

---

## Where To Inspect State

### On The Armada Instance

- Server dashboard -> `Server`
- Legacy dashboard -> `Server Settings`
- `GET /api/v1/status`
- `GET /api/v1/status/health`
- `GET /api/v1/settings`
- `armada status`

Key fields:

- `state`
- `tunnelUrl`
- `instanceId`
- `lastConnectAttemptUtc`
- `connectedUtc`
- `lastHeartbeatUtc`
- `lastDisconnectUtc`
- `lastError`
- `reconnectAttempts`
- `latencyMs`

### On The Control Plane

- `GET /api/v1/status/health`
- `GET /api/v1/instances`
- `GET /api/v1/instances/{instanceId}`
- `GET /api/v1/instances/{instanceId}/status/snapshot`
- `GET /api/v1/instances/{instanceId}/health`

Control-plane instance states:

- `connected`
- `stale`
- `offline`

---

## Common Failure Modes

### Armada enabled but no tunnel URL

Symptoms:

- Armada tunnel state becomes `Error`
- `lastError` says no tunnel URL is configured

Fix:

- set `remoteControl.tunnelUrl`
- or disable `remoteControl.enabled`

### Invalid tunnel scheme

Symptoms:

- Armada tunnel state becomes `Error`
- `lastError` says the URL must use `ws`, `wss`, `http`, or `https`

Fix:

- correct the URL scheme

### Control plane rejects handshake

Symptoms:

- websocket connects and then closes quickly
- the control plane logs a handshake rejection
- Armada eventually reports a disconnect/error cycle

Fix:

- check `instanceId` presence
- verify `ArmadaControlPlane.requireEnrollmentToken`
- verify the instance `remoteControl.enrollmentToken`
- verify the token exists in `ArmadaControlPlane.enrollmentTokens`

### TLS validation failure

Symptoms:

- Armada tunnel state becomes `Error`
- connection attempts keep retrying

Fix:

- use a valid server certificate
- for local-only development, temporarily enable `allowInvalidCertificates`

### Unreachable control plane

Symptoms:

- Armada cycles through `Connecting` -> `Error`
- `reconnectAttempts` increases
- control-plane instance list never shows the Armada instance

Fix:

- verify DNS and network reachability
- verify the websocket endpoint path
- inspect firewall and egress rules

### Stale control-plane instance

Symptoms:

- control-plane instance state becomes `stale`
- detail endpoints still show the instance, but live activity has stopped

Fix:

- inspect Armada tunnel heartbeats
- inspect process/network sleep or captive-network interruptions
- verify `staleAfterSeconds` is appropriate for the environment

---

## Live Request Notes

The control plane currently supports live requests for:

- `armada.status.snapshot`
- `armada.status.health`

If the instance is offline, those endpoints return an error instead of cached data.

Recent forwarded events are retained in memory only and are bounded by `maxRecentEvents`.

---

## Release Notes

The `v0.5.0 -> v0.6.0` release does not require a database schema migration.

Migration scripts still exist in `migrations/` so release automation and operator workflows have a versioned handoff point:

- `migrations/migrate_v0.5.0_to_v0.6.0.sh`
- `migrations/migrate_v0.5.0_to_v0.6.0.bat`
