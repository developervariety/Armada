# Proxy API

**Version:** 0.8.0

This document describes the currently shipped Armada proxy API surface in `v0.8.0`.

The proxy is no longer limited to summary, dispatch, and light fleet/vessel management. The shipped surface now includes:

- browser auth and instance selection
- remote shell summary and recent activity
- fleet, vessel, pipeline, playbook, voyage, mission, and captain inspection/management
- backlog/objective intake and captain-backed refinement
- planning-session creation, transcript summary, and dispatch handoff
- delivery-oriented workflow, check, environment, release, deployment, incident, and runbook routes
- captain tool inventory and request-history diagnostics
- bounded workspace, persona, pipeline, and prompt-template reference routes

The proxy still intentionally does not include:

- SaaS user accounts
- delegated identity or remote authorization mapping
- tenant, user, or credential administration
- setup wizard or local deployment management
- secret editing
- full remote workspace editing parity with the main dashboard

---

## Default Bind

The proxy binds to:

- Host: `localhost`
- Port: `7893`

Configuration is read from the `ArmadaProxy` section:

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

---

## Authentication Model

The proxy exposes a small auth surface for the browser app:

- `GET /api/v1/auth/challenge`
- `POST /api/v1/auth/login`
- `POST /api/v1/auth/logout`

`GET /api/v1/status/health` remains unauthenticated.

All other `/api/v1/*` routes require the `X-Armada-Proxy-Session` header returned by `POST /api/v1/auth/login`.

The browser does not send the raw shared password. It first requests a nonce from `/api/v1/auth/challenge`, computes a SHA-256 proof in the browser, and submits that proof to `/api/v1/auth/login`.

If `ArmadaProxy.password` is omitted or blank, the proxy defaults it to `armadaadmin`.

### GET /api/v1/auth/challenge

Returns a one-time challenge for browser login.

```json
{
  "nonce": "4f3a0c7a8f6c49d9b6711d2c1a7b5e90",
  "expiresUtc": "2026-04-04T05:10:00Z"
}
```

### POST /api/v1/auth/login

Validates the browser proof and returns a short-lived session token.

```json
{
  "nonce": "4f3a0c7a8f6c49d9b6711d2c1a7b5e90",
  "proofSha256": "8f5c4e1e1d7b5d8b2f6c6c987bfb76f5d55a75b8b940f882c817d39de42d83cc"
}
```

```json
{
  "token": "0f34455311b54e719f50927df5ecdfd798f8f27ed4ae45e2a73c0c3b2d194f73",
  "expiresUtc": "2026-04-04T17:05:00Z"
}
```

### POST /api/v1/auth/logout

Invalidates the current browser session identified by `X-Armada-Proxy-Session`.

---

## Core Status And Instance Endpoints

### GET /

Serves the proxy remote-operations shell.

The shipped shell now includes these major sections:

- summary
- activity
- missions
- voyages
- captains
- fleets
- vessels
- dispatch
- playbooks
- backlog
- planning
- delivery
- diagnostics
- reference

### GET /api/v1/status/health

Returns proxy process health and instance counts.

```json
{
  "healthy": true,
  "product": "Armada.Proxy",
  "version": "0.8.0",
  "protocolVersion": "2026-04-04",
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

### GET /api/v1/instances/{instanceId}

Returns the current summary plus recent inbound event history for an instance.

### GET /api/v1/instances/{instanceId}/summary

Returns the aggregated remote-shell summary for a connected instance by issuing `armada.instance.summary` over the tunnel and unwrapping the successful payload.

### GET /api/v1/instances/{instanceId}/status/snapshot

Issues `armada.status.snapshot` through the tunnel and unwraps the successful payload.

### GET /api/v1/instances/{instanceId}/health

Issues `armada.status.health` through the tunnel and unwraps the successful payload.

If the instance is offline, live instance routes return a `400` response like:

```json
{
  "error": "Instance armada-1f2e3d4c5b6a is not connected."
}
```

---

## Focused Inspection Endpoints

These routes are read-only proxy views over current Armada runtime state:

- `GET /api/v1/instances/{instanceId}/activity?limit=20`
- `GET /api/v1/instances/{instanceId}/missions/recent?limit=10`
- `GET /api/v1/instances/{instanceId}/voyages/recent?limit=10`
- `GET /api/v1/instances/{instanceId}/captains/recent?limit=10`
- `GET /api/v1/instances/{instanceId}/missions/{missionId}`
- `GET /api/v1/instances/{instanceId}/missions/{missionId}/log?lines=200&offset=0`
- `GET /api/v1/instances/{instanceId}/missions/{missionId}/diff`
- `GET /api/v1/instances/{instanceId}/voyages/{voyageId}`
- `GET /api/v1/instances/{instanceId}/captains/{captainId}`
- `GET /api/v1/instances/{instanceId}/captains/{captainId}/log?lines=50&offset=0`

Example mission detail response:

```json
{
  "mission": {
    "id": "msn_abc123",
    "title": "Update prompt templates",
    "status": "Review",
    "persona": "Judge"
  },
  "captain": {
    "id": "cpt_abc123",
    "name": "judge-1",
    "runtime": "ClaudeCode"
  },
  "voyage": {
    "id": "vyg_abc123",
    "title": "Remote control tranche"
  },
  "vessel": {
    "id": "vsl_abc123",
    "name": "armada-repo"
  },
  "dock": {
    "id": "dck_abc123",
    "branchName": "armada/remote-shell"
  }
}
```

---

## Core Remote Management Endpoints

### Fleets

- `GET /api/v1/instances/{instanceId}/fleets?limit=25`
- `GET /api/v1/instances/{instanceId}/fleets/{fleetId}`
- `POST /api/v1/instances/{instanceId}/fleets`
- `PUT /api/v1/instances/{instanceId}/fleets/{fleetId}`

### Vessels

- `GET /api/v1/instances/{instanceId}/vessels?limit=25&fleetId={fleetId}`
- `GET /api/v1/instances/{instanceId}/vessels/{vesselId}`
- `POST /api/v1/instances/{instanceId}/vessels`
- `PUT /api/v1/instances/{instanceId}/vessels/{vesselId}`

### Pipelines

- `GET /api/v1/instances/{instanceId}/pipelines?limit=25`
- `GET /api/v1/instances/{instanceId}/pipelines/{name}`

### Playbooks

- `GET /api/v1/instances/{instanceId}/playbooks?limit=25`
- `GET /api/v1/instances/{instanceId}/playbooks/{playbookId}`
- `POST /api/v1/instances/{instanceId}/playbooks`
- `PUT /api/v1/instances/{instanceId}/playbooks/{playbookId}`
- `DELETE /api/v1/instances/{instanceId}/playbooks/{playbookId}`

### Voyages

- `GET /api/v1/instances/{instanceId}/voyages?limit=25&status=InProgress`
- `POST /api/v1/instances/{instanceId}/voyages/dispatch`
- `DELETE /api/v1/instances/{instanceId}/voyages/{voyageId}`

### Missions

- `GET /api/v1/instances/{instanceId}/missions?limit=25&status=Review&voyageId={voyageId}&vesselId={vesselId}`
- `POST /api/v1/instances/{instanceId}/missions`
- `PUT /api/v1/instances/{instanceId}/missions/{missionId}`
- `DELETE /api/v1/instances/{instanceId}/missions/{missionId}`
- `POST /api/v1/instances/{instanceId}/missions/{missionId}/restart`

### Captains

- `POST /api/v1/instances/{instanceId}/captains/{captainId}/stop`

Example voyage dispatch request:

```json
{
  "title": "Remote release hardening",
  "description": "Ship the v0.8.0 proxy management surface.",
  "vesselId": "vsl_abc123",
  "pipelineId": "FullPipeline",
  "selectedPlaybooks": [
    {
      "playbookId": "plb_abc123",
      "deliveryMode": "InlineFullContent"
    }
  ],
  "missions": [
    {
      "title": "Tighten remote shell UX",
      "description": "Add mission and voyage browser filters to the proxy shell."
    },
    {
      "title": "Update proxy docs",
      "description": "Document the shipped management endpoints and shell workflows."
    }
  ]
}
```

---

## Phase 1: Backlog, Refinement, And Planning

### Objectives And Backlog

Both `/objectives` and `/backlog` map to the same remote objective store. The proxy exposes both because operators may think in either neutral lineage terms or backlog terminology.

- `GET /api/v1/instances/{instanceId}/objectives`
- `POST /api/v1/instances/{instanceId}/objectives/enumerate`
- `GET /api/v1/instances/{instanceId}/objectives/{objectiveId}`
- `POST /api/v1/instances/{instanceId}/objectives`
- `PUT /api/v1/instances/{instanceId}/objectives/{objectiveId}`
- `DELETE /api/v1/instances/{instanceId}/objectives/{objectiveId}`
- `GET /api/v1/instances/{instanceId}/backlog`
- `POST /api/v1/instances/{instanceId}/backlog/enumerate`
- `GET /api/v1/instances/{instanceId}/backlog/{objectiveId}`
- `POST /api/v1/instances/{instanceId}/backlog`
- `PUT /api/v1/instances/{instanceId}/backlog/{objectiveId}`
- `DELETE /api/v1/instances/{instanceId}/backlog/{objectiveId}`

Example backlog create request:

```json
{
  "title": "Proxy objective intake",
  "description": "Add planning and delivery parity to Armada.Proxy.",
  "status": "Draft",
  "backlogState": "Inbox",
  "vesselIds": [
    "vsl_abc123"
  ],
  "fleetIds": [
    "flt_abc123"
  ],
  "tags": [
    "proxy",
    "phase1"
  ]
}
```

### Objective Refinement Sessions

- `GET /api/v1/instances/{instanceId}/objectives/{objectiveId}/refinement-sessions`
- `POST /api/v1/instances/{instanceId}/objectives/{objectiveId}/refinement-sessions`
- `GET /api/v1/instances/{instanceId}/backlog/{objectiveId}/refinement-sessions`
- `POST /api/v1/instances/{instanceId}/backlog/{objectiveId}/refinement-sessions`
- `GET /api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}`
- `POST /api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}/messages`
- `POST /api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}/summarize`
- `POST /api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}/apply`
- `POST /api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}/stop`
- `DELETE /api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}`

### Planning Sessions

- `GET /api/v1/instances/{instanceId}/planning-sessions`
- `POST /api/v1/instances/{instanceId}/planning-sessions/enumerate`
- `POST /api/v1/instances/{instanceId}/planning-sessions`
- `GET /api/v1/instances/{instanceId}/planning-sessions/{sessionId}`
- `POST /api/v1/instances/{instanceId}/planning-sessions/{sessionId}/messages`
- `POST /api/v1/instances/{instanceId}/planning-sessions/{sessionId}/summarize`
- `POST /api/v1/instances/{instanceId}/planning-sessions/{sessionId}/dispatch`
- `POST /api/v1/instances/{instanceId}/planning-sessions/{sessionId}/stop`
- `DELETE /api/v1/instances/{instanceId}/planning-sessions/{sessionId}`

Example planning dispatch request:

```json
{
  "messageId": "psm_abc123",
  "title": "Proxy backlog dispatch"
}
```

---

## Phase 2: Delivery Surfaces

The proxy now exposes compact operator-facing routes for workflow governance and delivery follow-through.

### Workflow Profiles

- `GET /api/v1/instances/{instanceId}/workflow-profiles`
- `POST /api/v1/instances/{instanceId}/workflow-profiles/enumerate`
- `POST /api/v1/instances/{instanceId}/workflow-profiles`
- `GET /api/v1/instances/{instanceId}/workflow-profiles/{id}`
- `PUT /api/v1/instances/{instanceId}/workflow-profiles/{id}`
- `DELETE /api/v1/instances/{instanceId}/workflow-profiles/{id}`

### Check Runs

- `GET /api/v1/instances/{instanceId}/check-runs`
- `POST /api/v1/instances/{instanceId}/check-runs/enumerate`
- `POST /api/v1/instances/{instanceId}/check-runs`
- `GET /api/v1/instances/{instanceId}/check-runs/{id}`
- `POST /api/v1/instances/{instanceId}/check-runs/{id}/retry`
- `DELETE /api/v1/instances/{instanceId}/check-runs/{id}`

### Environments

- `GET /api/v1/instances/{instanceId}/environments`
- `POST /api/v1/instances/{instanceId}/environments/enumerate`
- `POST /api/v1/instances/{instanceId}/environments`
- `GET /api/v1/instances/{instanceId}/environments/{id}`
- `PUT /api/v1/instances/{instanceId}/environments/{id}`
- `DELETE /api/v1/instances/{instanceId}/environments/{id}`

### Releases

- `GET /api/v1/instances/{instanceId}/releases`
- `POST /api/v1/instances/{instanceId}/releases/enumerate`
- `POST /api/v1/instances/{instanceId}/releases`
- `GET /api/v1/instances/{instanceId}/releases/{id}`
- `PUT /api/v1/instances/{instanceId}/releases/{id}`
- `POST /api/v1/instances/{instanceId}/releases/{id}/refresh`
- `DELETE /api/v1/instances/{instanceId}/releases/{id}`

### Deployments

- `GET /api/v1/instances/{instanceId}/deployments`
- `POST /api/v1/instances/{instanceId}/deployments/enumerate`
- `POST /api/v1/instances/{instanceId}/deployments`
- `GET /api/v1/instances/{instanceId}/deployments/{id}`
- `PUT /api/v1/instances/{instanceId}/deployments/{id}`
- `POST /api/v1/instances/{instanceId}/deployments/{id}/approve`
- `POST /api/v1/instances/{instanceId}/deployments/{id}/deny`
- `POST /api/v1/instances/{instanceId}/deployments/{id}/verify`
- `POST /api/v1/instances/{instanceId}/deployments/{id}/rollback`
- `DELETE /api/v1/instances/{instanceId}/deployments/{id}`

### Incidents

- `GET /api/v1/instances/{instanceId}/incidents`
- `POST /api/v1/instances/{instanceId}/incidents/enumerate`
- `POST /api/v1/instances/{instanceId}/incidents`
- `GET /api/v1/instances/{instanceId}/incidents/{id}`
- `PUT /api/v1/instances/{instanceId}/incidents/{id}`
- `DELETE /api/v1/instances/{instanceId}/incidents/{id}`

### Runbooks And Runbook Executions

- `GET /api/v1/instances/{instanceId}/runbooks`
- `POST /api/v1/instances/{instanceId}/runbooks/enumerate`
- `POST /api/v1/instances/{instanceId}/runbooks`
- `GET /api/v1/instances/{instanceId}/runbooks/{id}`
- `PUT /api/v1/instances/{instanceId}/runbooks/{id}`
- `DELETE /api/v1/instances/{instanceId}/runbooks/{id}`
- `GET /api/v1/instances/{instanceId}/runbook-executions`
- `POST /api/v1/instances/{instanceId}/runbook-executions/enumerate`
- `GET /api/v1/instances/{instanceId}/runbook-executions/{id}`
- `POST /api/v1/instances/{instanceId}/runbooks/{id}/executions`
- `PUT /api/v1/instances/{instanceId}/runbook-executions/{id}`
- `DELETE /api/v1/instances/{instanceId}/runbook-executions/{id}`

Example deployment denial request:

```json
{
  "comment": "Hold until the next change window."
}
```

---

## Phase 3: Diagnostics

### Captain Tool Inventory

- `GET /api/v1/instances/{instanceId}/captains/{captainId}/tools`

This route surfaces runtime-visible tools, configured MCP servers, runtime-only sources, and reachability metadata when the selected captain runtime exposes them.

Example response summary:

```json
{
  "captainId": "cpt_abc123",
  "captainName": "codex-main",
  "runtime": "Codex",
  "toolsAccessible": true,
  "availabilityVerified": true,
  "availabilitySource": "runtime-catalog",
  "summary": "Codex reports 2 configured MCP server(s); 1 responded and exposed 107 tool(s). Remaining configured MCP servers did not respond at query time and may simply be offline.",
  "configuredServerCount": 2,
  "reachableServerCount": 1,
  "effectiveToolCount": 107,
  "servers": [],
  "tools": []
}
```

### Request History

- `GET /api/v1/instances/{instanceId}/request-history`
- `POST /api/v1/instances/{instanceId}/request-history/enumerate`
- `GET /api/v1/instances/{instanceId}/request-history/summary`
- `POST /api/v1/instances/{instanceId}/request-history/summary`
- `GET /api/v1/instances/{instanceId}/request-history/{id}`

### API Explorer

There is no dedicated `/api/v1/instances/{instanceId}/api-explorer` endpoint.

The proxy shell's API Explorer is a bounded browser-side tool that issues authenticated requests against existing proxy routes. It is intentionally route-based rather than a second generic server surface.

It only targets the selected deployment route family under /api/v1/instances/{instanceId} and now seeds the UI with safe common GET presets before operators branch into write calls.

---

## Phase 4: Workspace And Reference Views

### Workspace

- `GET /api/v1/instances/{instanceId}/workspace/vessels/{vesselId}/status`
- `GET /api/v1/instances/{instanceId}/workspace/vessels/{vesselId}/tree?path=...`
- `GET /api/v1/instances/{instanceId}/workspace/vessels/{vesselId}/file?path=...`
- `GET /api/v1/instances/{instanceId}/workspace/vessels/{vesselId}/search?query=...&maxResults=...`
- `GET /api/v1/instances/{instanceId}/workspace/vessels/{vesselId}/changes`

Example workspace file response:

```json
{
  "vesselId": "vsl_abc123",
  "path": "notes.txt",
  "name": "notes.txt",
  "content": "Proxy workspace token",
  "contentHash": "2f8f5a0e7c4a1c5d6e0f...",
  "isEditable": true,
  "isBinary": false,
  "isLarge": false,
  "previewTruncated": false,
  "sizeBytes": 21,
  "lastWriteUtc": "2026-05-15T22:15:00Z",
  "language": "plaintext"
}
```

### Personas And Prompt Templates

- `GET /api/v1/instances/{instanceId}/personas`
- `POST /api/v1/instances/{instanceId}/personas/enumerate`
- `GET /api/v1/instances/{instanceId}/personas/{name}`
- `GET /api/v1/instances/{instanceId}/prompt-templates`
- `POST /api/v1/instances/{instanceId}/prompt-templates/enumerate`
- `GET /api/v1/instances/{instanceId}/prompt-templates/{name}`

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
- browser API access is protected by a shared-password session gate, not a multi-user identity model
- tunnel registration requires a valid shared-password proof and optional enrollment-token validation
- the proxy now supports high-value remote-operations routes for:
  - summary and live inspection
  - fleet/vessel/pipeline/playbook/voyage/mission/captain control
  - backlog, refinement, and planning
  - workflow, checks, environments, releases, deployments, incidents, and runbooks
  - captain tools, request history, and bounded API exploration
  - read-only workspace, pipeline, persona, and prompt-template reference flows
- recent event history is bounded by `maxRecentEvents`
- destructive actions are still client-confirmed in the shell, but there is still no per-user authz or policy engine beyond the current proxy session gate
