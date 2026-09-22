# Tunnel Operations

**Version:** 0.9.0

This guide covers the shipped remote-control tunnel and proxy MVP surfaces in Armada `v0.9.0`.

For a step-by-step operator setup path, see [REMOTE_MGMT.md](REMOTE_MGMT.md).

---

## Scope

The tunnel supports:

- the Armada-side outbound websocket tunnel client
- remote tunnel configuration in Armada settings and dashboards
- a minimal `Armada.Proxy` service with websocket termination and instance registry APIs
- a proxy-hosted remote operations shell served at `/`
- live forwarded status/health requests from the proxy into a connected Armada instance
- focused remote inspection requests for recent activity, missions, voyages, captains, logs, and diffs
- bounded remote management requests for fleets, vessels, playbooks, backlog/objectives, refinement sessions, planning sessions, workflow profiles, checks, environments, releases, deployments, incidents, runbooks, runbook executions, and captain control
- shell workflows for fleet and vessel editing, voyage dispatch and cancellation, mission create/update/cancel/restart, backlog/planning handoff, delivery operations, diagnostics, and read-only reference inspection

Still not included:

- user-facing SaaS auth
- delegated identity or local-session brokerage
- notification delivery
- persistent proxy storage
- server-side remote action policy evaluation beyond current shell confirmation prompts
- secret-bearing admin editing such as credentials or token overrides

Treat the current proxy as an implementation-stage operator service, not a hardened public SaaS surface.

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
    "password": "armadaadmin",
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
- Keep `remoteControl.password` aligned with `ArmadaProxy.password`.
- Use `allowInvalidCertificates = true` only for local development with self-signed certificates.

---

## Proxy Configuration

`Armada.Proxy` reads configuration from `ArmadaProxy`:

```json
{
  "ArmadaProxy": {
    "dataDirectory": "/app/data",
    "logDirectory": "/app/data/logs",
    "hostname": "localhost",
    "port": 7893,
    "syslogServers": [
      {
        "hostname": "127.0.0.1",
        "port": 514
      }
    ],
    "requireEnrollmentToken": false,
    "enrollmentTokens": [],
    "password": "armadaadmin",
    "handshakeTimeoutSeconds": 15,
    "staleAfterSeconds": 90,
    "requestTimeoutSeconds": 20,
    "maxRecentEvents": 50
  }
}
```

### Key Fields

- `dataDirectory`
- `logDirectory`
- `hostname`
- `port`
- `syslogServers`
- `requireEnrollmentToken`
- `enrollmentTokens`
- `password`
- `handshakeTimeoutSeconds`
- `staleAfterSeconds`
- `requestTimeoutSeconds`
- `maxRecentEvents`

---

## Starting The Proxy

From the repo root:

```powershell
dotnet run --project src/Armada.Proxy/Armada.Proxy.csproj --framework net10.0
```

Default endpoints:

- health: `http://localhost:7893/proxy-api/v1/status/health`
- instance list (after proxy login): `http://localhost:7893/proxy-api/v1/instances`
- remote shell: `http://localhost:7893/`
- tunnel websocket: `ws://localhost:7893/tunnel`

Point Armada at the proxy by setting:

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

### On The Proxy

- `GET /api/v1/status/health`
- `GET /api/v1/instances`
- `GET /api/v1/instances/{instanceId}`
- `GET /api/v1/instances/{instanceId}/status/snapshot`
- `GET /api/v1/instances/{instanceId}/health`
- `GET /api/v1/instances/{instanceId}/fleets`
- `GET /api/v1/instances/{instanceId}/vessels`
- `GET /api/v1/instances/{instanceId}/voyages`
- `GET /api/v1/instances/{instanceId}/missions`
- `POST /api/v1/instances/{instanceId}/fleets`
- `PUT /api/v1/instances/{instanceId}/fleets/{fleetId}`
- `POST /api/v1/instances/{instanceId}/vessels`
- `PUT /api/v1/instances/{instanceId}/vessels/{vesselId}`
- `POST /api/v1/instances/{instanceId}/voyages/dispatch`
- `DELETE /api/v1/instances/{instanceId}/voyages/{voyageId}`
- `POST /api/v1/instances/{instanceId}/missions`
- `PUT /api/v1/instances/{instanceId}/missions/{missionId}`
- `DELETE /api/v1/instances/{instanceId}/missions/{missionId}`
- `POST /api/v1/instances/{instanceId}/missions/{missionId}/restart`
- `POST /api/v1/instances/{instanceId}/captains/{captainId}/stop`

Proxy instance states:

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

### Proxy rejects handshake

Symptoms:

- websocket connects and then closes quickly
- the proxy logs a handshake rejection
- Armada eventually reports a disconnect/error cycle

Fix:

- check `instanceId` presence
- verify `ArmadaProxy.requireEnrollmentToken`
- verify the instance `remoteControl.enrollmentToken`
- verify the token exists in `ArmadaProxy.enrollmentTokens`

### TLS validation failure

Symptoms:

- Armada tunnel state becomes `Error`
- connection attempts keep retrying

Fix:

- use a valid server certificate
- for local-only development, temporarily enable `allowInvalidCertificates`

### Unreachable proxy

Symptoms:

- Armada cycles through `Connecting` -> `Error`
- `reconnectAttempts` increases
- proxy instance list never shows the Armada instance

Fix:

- verify DNS and network reachability
- verify the websocket endpoint path
- inspect firewall and egress rules

### Stale proxy instance

Symptoms:

- proxy instance state becomes `stale`
- detail endpoints still show the instance, but live activity has stopped

Fix:

- inspect Armada tunnel heartbeats
- inspect process/network sleep or captive-network interruptions
- verify `staleAfterSeconds` is appropriate for the environment

---

## Live Request Notes

The proxy reaches a live instance through the generic relay methods only:

- `armada.http.request` forwards one dashboard REST request under `/api/v1/`
- `armada.ws.open`, `armada.ws.message` and `armada.ws.close` carry the dashboard websocket

Armada answers any other tunnel method with `404 unsupported_method`. Remote dashboard reads and writes are REST routes relayed through `armada.http.request`.

The remote shell uses simple browser confirmation prompts before destructive actions such as mission cancellation, voyage cancellation, mission restart, and captain stop. These are operator safety rails, not a substitute for the future delegated identity and policy model.

If the instance is offline, those endpoints return an error instead of cached data.

Recent forwarded events are retained in memory only and are bounded by `maxRecentEvents`.

---

## Release Notes

The `v0.7.0 -> v0.8.0` release adds Armada backlog/objective normalization schema changes on the server side.

`Armada.Server` applies those schema migrations automatically on first startup after upgrade. If you need a controlled DBA-managed rollout, the versioned handoff scripts in `migrations/` emit the backend-specific SQL and precheck guidance:

- `migrations/migrate_v0.7.0_to_v0.8.0.sh`
- `migrations/migrate_v0.7.0_to_v0.8.0.bat`
