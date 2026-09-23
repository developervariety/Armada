# Armada WebSocket API Reference

**Version:** 0.9.0
**Default URL:** `ws://localhost:7890/ws`
**Protocol:** WebSocket (RFC 6455) via Watson7
**Transport:** JSON text frames

Each connected client has an independent bounded output queue. Armada preserves
message order for that client without waiting for its network send in the event
producer. Armada disconnects a client when its queue is full or its send fails,
so one slow monitor cannot delay other clients.

## Remote Proxy Note

When the dashboard is opened directly from `Armada.Server`, connect to `/ws` on the Armada origin as usual.

When the dashboard is opened from `Armada.Proxy`, the browser still connects to `/ws` on the current origin, but the proxy relays that websocket session through the outbound tunnel to the selected Armada deployment. The dashboard websocket message format does not change in proxy mode; only the transport path changes.

Proxy prerequisites:

- an authenticated proxy browser session
- a selected connected deployment in the proxy session
- an active tunnel connection for that deployment

If the selected deployment disconnects or the tunnel drops, the proxy closes the browser websocket and the dashboard must reconnect through the proxy origin.

---

## Table of Contents

- [Connection](#connection)
  - [URL Construction](#url-construction)
  - [Port Discovery](#port-discovery)
  - [SSL/TLS](#ssltls)
- [Message Format](#message-format)
- [Routes](#routes)
  - [subscribe](#subscribe)
  - [command](#command)
- [Server-Pushed Events](#server-pushed-events)
  - [status.snapshot](#statussnapshot)
  - [mission.changed](#missionchanged)
  - [voyage.changed](#voyagechanged)
  - [captain.changed](#captainchanged)
  - [objective.changed](#objectivechanged)
  - [objective.deleted](#objectivedeleted)
  - [objective-refinement-session.changed](#objective-refinement-sessionchanged)
  - [objective-refinement-session.message.created](#objective-refinement-sessionmessagecreated)
  - [objective-refinement-session.message.updated](#objective-refinement-sessionmessageupdated)
  - [objective-refinement-session.summary.created](#objective-refinement-sessionsummarycreated)
  - [objective-refinement-session.applied](#objective-refinement-sessionapplied)
  - [check-run.changed](#check-runchanged)
  - [deployment.changed](#deploymentchanged)
  - [deployment.progress](#deploymentprogress)
  - [environment.health](#environmenthealth)
  - [approval-needed](#approval-needed)
  - [Generic Events](#generic-events)
- [Command Actions](#command-actions)
  - [Status & Control](#status--control)
  - [Fleet Actions](#fleet-actions)
  - [Vessel Actions](#vessel-actions)
  - [Voyage Actions](#voyage-actions)
  - [Mission Actions](#mission-actions)
  - [Captain Actions](#captain-actions)
  - [Signal Actions](#signal-actions)
  - [Event Actions](#event-actions)
  - [Dock Actions](#dock-actions)
  - [Merge Queue Actions](#merge-queue-actions)
  - [Backup and Restore Actions](#backup-and-restore-actions)
  - [Enumerate](#enumerate)
- [Pagination](#pagination)
- [Mission Status Transitions](#mission-status-transitions)
- [Error Handling](#error-handling)
- [Data Types](#data-types)
  - [Models](#models)
  - [Enumerations](#enumerations)
- [Client Examples](#client-examples)
  - [JavaScript](#javascript)
  - [C# / .NET](#c--net)
  - [Python](#python)

---

## Connection

### URL Construction

The WebSocket server runs on the same port as the Admiral REST API, at the `/ws` path. The default configuration is:

```
ws://localhost:7890/ws
```

The port is configurable via `ArmadaSettings.AdmiralPort` (default: `7890`). The hostname matches the REST API's `RestSettings.Hostname` (default: `localhost`).

### Port Discovery

The WebSocket endpoint is always available at `/ws` on the Admiral port. Clients can confirm the Admiral port by querying the REST API health endpoint:

```
GET http://localhost:7890/api/v1/status/health
```

The response includes the Admiral port. The WebSocket endpoint is at `/ws` on the same port.

### SSL/TLS

When `RestSettings.Ssl` is enabled, use `wss://` instead of `ws://`:

```
wss://localhost:7890/ws
```

SSL applies to both the REST API and the WebSocket server.

### Authentication

A session must authenticate before it can subscribe or send a command. The
server resolves the credentials through the same authentication service as the
REST API. Send an `authenticate` message as the first frame:

```json
{
  "Route": "authenticate",
  "token": "<bearer credential token or dashboard session token>"
}
```

```json
{
  "Route": "authenticate",
  "apiKey": "<admiral API key>"
}
```

A browser cannot set WebSocket request headers, so the credentials travel in
this frame and not in the URL. Query strings appear in request logs.

- Valid credentials: the server sends `auth.result` with `authenticated`,
  `tenantId`, `userId`, `isAdmin` and `isTenantAdmin`.
- Invalid credentials: the server sends `auth.failed` and closes the session.
- Any other route before authentication: the server sends `auth.required` and
  closes the session.
- No authentication within 15 seconds of connecting: the server sends
  `auth.required` and closes the session.
- Each `command` action has a declared authorization rule; see
  [Command Authorization](#command-authorization). Most actions need a global
  administrator and return `command.error` with `code`
  `global_administrator_required` to other sessions. `get_persona`,
  `get_pipeline` and `get_prompt_template` are open to any authenticated session
  and read through the caller's scope. `create_persona`, `update_persona`,
  `delete_persona`, `create_pipeline`, `update_pipeline` and `delete_pipeline`
  are open to global and tenant administrators, as on REST and MCP, and change
  only records of the caller's own tenant; a tenant user receives
  `tenant_administrator_required`. `list_missions_summary` reads through the
  same caller-scoped query as `GET /api/v1/missions/summaries`, so it returns
  exactly what REST returns to the same caller.
- A create command (`create_fleet`, `create_vessel`, `create_voyage`,
  `create_mission`, `create_captain`, `send_signal`, `enqueue_merge`,
  `create_persona`, `create_pipeline`) records the session caller's tenant and
  user as the owner, as the matching REST create does, and replaces any owner
  the `data` object names. Without an authenticated caller it returns
  `command.error` with `code` `authentication_required` and writes nothing. `create_voyage` with missions dispatches
  through the admiral, so the voyage and missions take the vessel's owner.
  `create_voyage` also accepts `skipStages` and `skipStagesReason`: when
  `skipStages` names a stage, the voyage materialises the vessel's effective
  pipeline without those stages, under the same rule as `armada_dispatch`. A
  refused skip returns `command.error` with `code` `stage_skip_judge_refused`
  or `stage_skip_unknown_persona`. The
  `restart_mission` progress signal takes the mission's owner.
- `subscribe` is open to any authenticated session. Each event carries a
  delivery scope and reaches only the sessions that may read the record it
  describes: the owning user, administrators of the owning tenant, and global
  administrators. An event with no known owner, such as a coordination board
  note or a scheduler event, reaches global administrators only. Replayed and
  catch-up events obey the same rule, so reconnecting never reveals an event the
  session could not have received live.
- The `status.snapshot` fleet status and reconciliation aggregate every tenant,
  so only a global administrator receives them. For any other session both are
  `null` and `data.scoped` is `true`; such a session loads its own records
  through the REST API, which applies its scope.
- Ask chat `ask.chunk`, `ask.tool` and `ask.thinking` events reach only the
  caller's own sessions and global administrators. Planning and objective
  refinement session events follow the session's owner. A mission status
  change applied by `transition_mission_status` follows the mission's owner,
  like the same change made through REST or MCP. The mission and voyage change
  events that `cancel_voyage`, `cancel_mission` and `restart_mission` cause
  follow the changed record's owner in the same way. The calling session is a
  global administrator, so it receives them too; the administrators of the
  record's tenant and its owning user receive them, and another tenant's
  sessions do not.
- Cursors are positions in one stream shared by every session. A scoped session
  does not receive events it may not read, so its cursors can skip. Detect lost
  history from `event.gap` frames; cursor arithmetic is reliable only for a
  global-administrator session, which receives every event.

The server handles frames in order, so a client can send `authenticate` and
`subscribe` together.

---

## Message Format

All messages are JSON text frames. The `Route` property in the client-to-server message envelope must be **PascalCase**. Server responses use **camelCase** property naming.

### Client-to-Server

Messages sent from the client to the server must include a `Route` field to select the handler:

```json
{
  "Route": "subscribe"
}
```

```json
{
  "Route": "command",
  "action": "status"
}
```

### Server-to-Client

All server messages include a `type` field indicating the event kind, and a `timestamp` field with the UTC time:

```json
{
  "type": "mission.changed",
  "data": {
    "id": "msn_abc123",
    "status": "Complete",
    "title": "Implement feature X",
    "voyageId": "vyg_abc123"
  },
  "streamId": "8af7caa37157484ca80587eadac99747",
  "cursor": 42,
  "timestamp": "2026-03-07T12:34:56.789Z"
}
```

Broadcast events have a process `streamId` and a monotonic `cursor`.
`status.snapshot` and `stream.ready` include a safe resume position.
`event.gap` identifies the stream but does not claim an event cursor.

---

## Routes

### subscribe

Subscribe to real-time event broadcasts. A new subscription needs only the
route. A reconnect can send the last complete `streamId` and `cursor` pair.
`voyageId` limits the authoritative reconciliation snapshot to one voyage.

**Client sends:**

```json
{
  "Route": "subscribe",
  "streamId": "8af7caa37157484ca80587eadac99747",
  "cursor": 42,
  "voyageId": "vyg_abc123"
}
```

The server can first replay retained events after the cursor. It then sends a
[`status.snapshot`](#statussnapshot), any events that occurred while it built
the snapshot, and `stream.ready`. If complete replay is not possible, it sends
`event.gap` before the snapshot. Old clients that send only `Route` continue to
work.

After the initial snapshot, the client will receive all broadcast events ([`mission.changed`](#missionchanged), [`voyage.changed`](#voyagechanged), [`captain.changed`](#captainchanged), [`objective.changed`](#objectivechanged), [`objective.deleted`](#objectivedeleted), [`objective-refinement-session.changed`](#objective-refinement-sessionchanged), [`objective-refinement-session.message.created`](#objective-refinement-sessionmessagecreated), [`objective-refinement-session.message.updated`](#objective-refinement-sessionmessageupdated), [`objective-refinement-session.summary.created`](#objective-refinement-sessionsummarycreated), [`objective-refinement-session.applied`](#objective-refinement-sessionapplied), [`check-run.changed`](#check-runchanged), [`deployment.changed`](#deploymentchanged), [`deployment.progress`](#deploymentprogress), [`environment.health`](#environmenthealth), [`approval-needed`](#approval-needed), and [generic events](#generic-events)) as they occur.

---

### command

Send a command to the Admiral for execution. The `action` field determines which command to run.

**Client sends:**

```json
{
  "Route": "command",
  "action": "<action_name>",
  ...additional fields depending on action
}
```

**Server responds with:** a `command.result` or `command.error` message.

See [Command Actions](#command-actions) for the current operational action set. This WebSocket surface focuses on real-time monitoring and core orchestration commands; newer REST-only helpers such as Workspace, planning sessions, request history, GitHub objective import, GitHub Actions sync, GitHub PR evidence, and runtime discovery remain HTTP-only.

---

## Server-Pushed Events

These events are broadcast to all subscribed clients when state changes occur.
Clients do not receive live events before subscription reconciliation is ready.

### status.snapshot

Sent for each `subscribe` request. It contains the normal Armada status and an
authoritative reconciliation view at the stated cursor.

```json
{
  "type": "status.snapshot",
  "streamId": "8af7caa37157484ca80587eadac99747",
  "cursor": 42,
  "data": {
    "status": {
      "totalCaptains": 4,
      "idleCaptains": 1,
      "workingCaptains": 2,
      "stalledCaptains": 1,
      "activeVoyages": 2
    },
    "reconciliation": {
      "generatedUtc": "2026-03-07T12:34:56.789Z",
      "voyageId": null,
      "voyages": [],
      "missions": [],
      "captains": [],
      "checkRuns": []
    }
  },
  "timestamp": "2026-03-07T12:34:56.789Z"
}
```

For a global subscription, reconciliation includes Open and InProgress voyages,
their missions and Checks, active standalone missions and their Checks, and all captains. For a scoped subscription, it
includes the exact voyage even when it is terminal, plus its linked state.
Snapshots use bounded database pagination and fail instead of returning a
silently incomplete result.

### event.gap

The requested history is not complete. `data.reason` is `stream_changed`,
`cursor_ahead`, `history_evicted`, `snapshot_overflow`, or `replay_overflow`.
The client must use the following `status.snapshot` as authoritative state.

### stream.ready

Subscription reconciliation is complete. The frame gives the current
`streamId` and `cursor`. Live events follow it in cursor order.

---

### mission.changed

Broadcast when a mission's status changes (e.g., assigned, started, completed, failed).

```json
{
  "type": "mission.changed",
  "data": {
    "id": "msn_abc123def456ghi789jk",
    "status": "InProgress",
    "title": "Add input validation to signup form",
    "voyageId": "vyg_abc123def456ghi789jk"
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"mission.changed"` |
| `data.id` | string | Mission ID (prefix `msn_`) |
| `data.status` | string | New [MissionStatusEnum](#missionstatusenum) value |
| `data.title` | string \| null | Mission title |
| `data.voyageId` | string \| null | Parent voyage ID; null for a standalone mission |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### voyage.changed

Broadcast when a voyage state changes.

```json
{
  "type": "voyage.changed",
  "data": {
    "id": "vyg_abc123def456ghi789jk",
    "status": "Complete",
    "title": "API Hardening"
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"voyage.changed"` |
| `data.id` | string | Voyage ID (prefix `vyg_`) |
| `data.status` | string | Voyage status value |
| `data.title` | string \| null | Voyage title |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### captain.changed

Broadcast when a captain's state changes (e.g., idle to working, working to stalled).

```json
{
  "type": "captain.changed",
  "data": {
    "id": "cpt_abc123def456ghi789jk",
    "state": "Working",
    "name": "captain-1"
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"captain.changed"` |
| `data.id` | string | Captain ID (prefix `cpt_`) |
| `data.state` | string | New [CaptainStateEnum](#captainstateenum) value |
| `data.name` | string \| null | Captain display name |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### check-run.changed

Broadcast when a structured check run is created, imported, updated, or retried.

```json
{
  "type": "check-run.changed",
  "data": {
    "id": "chk_abc123def456ghi789jk",
    "status": "Passed",
    "type": "UnitTest",
    "vesselId": "vsl_abc123def456ghi789jk",
    "label": "Unit tests",
    "queueDurationMs": 4200,
    "durationMs": 18500
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"check-run.changed"` |
| `data` | object | Full serialized `CheckRun` payload with enum values emitted as strings |
| `data.queueDurationMs` | number \| null | Time from record creation until the host command slot was acquired |
| `data.durationMs` | number \| null | Command execution time; does not include queue wait |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### objective.changed

Broadcast when an objective or backlog record is created or updated.

```json
{
  "type": "objective.changed",
  "data": {
    "id": "obj_abc123def456ghi789jk",
    "status": "Scoped",
    "title": "Stabilize May rollout"
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"objective.changed"` |
| `data` | object | Full serialized `Objective` payload with enum values emitted as strings, including backlog metadata and linkage fields |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### objective.deleted

Broadcast when an objective or backlog record is deleted, through the REST API or MCP. It reaches the sessions that could read the objective (its owner scope and global administrators). Remove the objective from any list the client holds.

```json
{
  "type": "objective.deleted",
  "data": {
    "id": "obj_abc123def456ghi789jk",
    "tenantId": "ten_abc123def456ghi789jk",
    "userId": "usr_abc123def456ghi789jk"
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"objective.deleted"` |
| `data.id` | string | Deleted objective ID (prefix `obj_`) |
| `data.tenantId` | string \| null | Tenant that owned the objective |
| `data.userId` | string \| null | User that owned the objective |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### objective-refinement-session.changed

Broadcast when a backlog refinement session changes state.

```json
{
  "type": "objective-refinement-session.changed",
  "message": "Objective refinement session updated",
  "data": {
    "session": {
      "id": "ors_abc123def456ghi789jk",
      "objectiveId": "obj_abc123def456ghi789jk",
      "captainId": "cpt_abc123def456ghi789jk",
      "status": "Active"
    }
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"objective-refinement-session.changed"` |
| `message` | string | Human-readable event label |
| `data.session` | object | Full serialized `ObjectiveRefinementSession` payload |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### objective-refinement-session.message.created

Broadcast when a refinement transcript message is created.

```json
{
  "type": "objective-refinement-session.message.created",
  "message": "Objective refinement message created",
  "data": {
    "sessionId": "ors_abc123def456ghi789jk",
    "objectiveId": "obj_abc123def456ghi789jk",
    "message": {
      "id": "orm_abc123def456ghi789jk",
      "role": "User",
      "sequence": 1,
      "content": "Focus on rollback safety."
    }
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"objective-refinement-session.message.created"` |
| `message` | string | Human-readable event label |
| `data.sessionId` | string | Refinement session ID (prefix `ors_`) |
| `data.objectiveId` | string | Linked backlog/objective ID (prefix `obj_`) |
| `data.message` | object | Full serialized `ObjectiveRefinementMessage` payload |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### objective-refinement-session.message.updated

Broadcast when a refinement transcript message is updated, for example when an assistant turn finishes or selection state changes.

```json
{
  "type": "objective-refinement-session.message.updated",
  "message": "Objective refinement message updated",
  "data": {
    "sessionId": "ors_abc123def456ghi789jk",
    "objectiveId": "obj_abc123def456ghi789jk",
    "message": {
      "id": "orm_def456ghi789jkl012mn",
      "role": "Assistant",
      "sequence": 2,
      "isSelected": true
    }
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"objective-refinement-session.message.updated"` |
| `message` | string | Human-readable event label |
| `data.sessionId` | string | Refinement session ID (prefix `ors_`) |
| `data.objectiveId` | string | Linked backlog/objective ID (prefix `obj_`) |
| `data.message` | object | Full serialized `ObjectiveRefinementMessage` payload |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### objective-refinement-session.summary.created

Broadcast when Armada creates a structured refinement summary from the transcript.

```json
{
  "type": "objective-refinement-session.summary.created",
  "message": "Objective refinement summary created",
  "data": {
    "sessionId": "ors_abc123def456ghi789jk",
    "messageId": "orm_def456ghi789jkl012mn",
    "summary": {
      "sessionId": "ors_abc123def456ghi789jk",
      "messageId": "orm_def456ghi789jkl012mn",
      "summary": "Stabilize rollback and verification sequencing.",
      "acceptanceCriteria": ["Rollback completes in under five minutes"],
      "nonGoals": [],
      "rolloutConstraints": ["Staging first"],
      "suggestedPipelineId": null,
      "preparation": null,
      "method": "runtime-json"
    }
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

The event data is exactly `{ sessionId, messageId, summary }`. `data.summary` is the refinement summary object, not a string; its own `summary` field holds the summary text.

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"objective-refinement-session.summary.created"` |
| `message` | string | Human-readable event label |
| `data.sessionId` | string | Refinement session ID (prefix `ors_`) |
| `data.messageId` | string | Source transcript message ID (prefix `orm_`) |
| `data.summary` | object | Serialized `ObjectiveRefinementSummaryResponse` |
| `data.summary.sessionId` | string | Refinement session ID |
| `data.summary.messageId` | string \| null | Source transcript message ID |
| `data.summary.summary` | string | Summary text |
| `data.summary.acceptanceCriteria` | string[] | Proposed acceptance criteria |
| `data.summary.nonGoals` | string[] | Proposed non-goals |
| `data.summary.rolloutConstraints` | string[] | Proposed rollout constraints |
| `data.summary.suggestedPipelineId` | string \| null | Suggested pipeline ID (prefix `ppl_`) |
| `data.summary.preparation` | object \| null | Proposed objective preparation |
| `data.summary.method` | string | How the summary was produced: `runtime-json` or `assistant-fallback` |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### objective-refinement-session.applied

Broadcast when Armada applies a refinement summary back to the linked backlog item.

```json
{
  "type": "objective-refinement-session.applied",
  "message": "Objective refinement summary applied",
  "data": {
    "sessionId": "ors_abc123def456ghi789jk",
    "objectiveId": "obj_abc123def456ghi789jk",
    "summary": {
      "sessionId": "ors_abc123def456ghi789jk",
      "messageId": "orm_def456ghi789jkl012mn",
      "summary": "Stabilize rollback and verification sequencing.",
      "acceptanceCriteria": ["Rollback completes in under five minutes"],
      "nonGoals": [],
      "rolloutConstraints": ["Staging first"],
      "suggestedPipelineId": null,
      "preparation": null,
      "method": "runtime-json"
    }
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

The event data is exactly `{ sessionId, objectiveId, summary }`. It carries no `messageId` at the top level; the applied message is `data.summary.messageId`. `data.summary` has the same shape as in [`objective-refinement-session.summary.created`](#objective-refinement-sessionsummarycreated). The updated objective itself arrives separately as [`objective.changed`](#objectivechanged).

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"objective-refinement-session.applied"` |
| `message` | string | Human-readable event label |
| `data.sessionId` | string | Refinement session ID (prefix `ors_`) |
| `data.objectiveId` | string | Linked backlog/objective ID (prefix `obj_`) |
| `data.summary` | object | Serialized `ObjectiveRefinementSummaryResponse` (fields listed under `summary.created`) |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### deployment.changed

Broadcast when a deployment record changes, including approval, execution, verification, and rollback state.

```json
{
  "type": "deployment.changed",
  "data": {
    "id": "dpl_abc123def456ghi789jk",
    "status": "Running",
    "environmentName": "Staging",
    "verificationStatus": "Pending"
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"deployment.changed"` |
| `data` | object | Full serialized `Deployment` payload with enum values emitted as strings |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### deployment.progress

Broadcast when deployment progress or operator-facing execution messaging changes.

```json
{
  "type": "deployment.progress",
  "data": {
    "id": "dpl_abc123def456ghi789jk",
    "status": "Running",
    "message": "Running deploy command for Staging"
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"deployment.progress"` |
| `data.id` | string | Deployment ID (prefix `dpl_`) |
| `data.status` | string | Current deployment status |
| `data.message` | string | Human-readable progress message |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### environment.health

Broadcast when rollout monitoring or verification updates health-related evidence for an environment deployment.

```json
{
  "type": "environment.health",
  "data": {
    "deploymentId": "dpl_abc123def456ghi789jk",
    "environmentId": "env_abc123def456ghi789jk",
    "environmentName": "Staging",
    "verificationStatus": "Passed",
    "message": "Health endpoint returned 200 OK"
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"environment.health"` |
| `data.deploymentId` | string \| null | Deployment ID tied to the health update |
| `data.environmentId` | string \| null | Environment ID tied to the health update |
| `data.environmentName` | string \| null | Environment name |
| `data.verificationStatus` | string \| null | Current deployment verification status |
| `data.message` | string \| null | Human-readable health/verification message |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### approval-needed

Broadcast when a mission enters review and awaits an explicit approve or deny decision.

```json
{
  "type": "approval-needed",
  "data": {
    "entityType": "mission",
    "entityId": "msn_abc123def456ghi789jk",
    "missionId": "msn_abc123def456ghi789jk",
    "title": "Review login rate limiting",
    "status": "Review",
    "vesselId": "vsl_abc123def456ghi789jk",
    "voyageId": "vyg_abc123def456ghi789jk",
    "reviewRequestedUtc": "2026-03-07T12:35:00.000Z"
  },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Always `"approval-needed"` |
| `data.entityType` | string | Currently `"mission"` |
| `data.entityId` | string | Entity identifier requiring approval |
| `data.missionId` | string | Mission identifier |
| `data.title` | string \| null | Mission title |
| `data.status` | string | Current mission status, normally `Review` |
| `data.vesselId` | string \| null | Linked vessel ID |
| `data.voyageId` | string \| null | Linked voyage ID |
| `data.reviewRequestedUtc` | string \| null | Review request timestamp |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

### Generic Events

Broadcast for general system events (e.g., escalation triggers, merge queue updates, voyage completion).

```json
{
  "type": "voyage.completed",
  "message": "Voyage 'Feature batch 1' completed successfully",
  "data": { "voyageId": "vyg_abc123def456ghi789jk" },
  "timestamp": "2026-03-07T12:35:00.000Z"
}
```

| Field | Type | Description |
|---|---|---|
| `type` | string | Event type string (e.g., `"voyage.completed"`, `"escalation.triggered"`) |
| `message` | string | Human-readable event description |
| `data` | object \| null | Optional additional event data |
| `timestamp` | string | ISO 8601 UTC timestamp |

---

## Command Actions

Commands are sent via the `command` route. Each command returns a `command.result` message on success or a `command.error` message on failure. The WebSocket command surface covers Armada's real-time and core operational flows; it does not currently expose the REST-only Workspace, planning-session, request-history, GitHub objective import, GitHub Actions sync, GitHub PR evidence, runtime-helper, or OpenAPI discovery surfaces.

### Command Actions Summary

| Category | Action | Description | Required Fields |
|---|---|---|---|
| **Status & Control** | `status` | Get current ArmadaStatus | Ã¢â‚¬â€ |
| | `stop_captain` | Stop specific captain | `captainId` |
| | `stop_all` | Emergency stop of every working captain and active planning and refinement session; reports stopped and failed counts | Ã¢â‚¬â€ |
| **Fleet** | `list_fleets` | List/enumerate fleets | optional `query` |
| | `get_fleet` | Get fleet by ID | `id` |
| | `create_fleet` | Create fleet | `data` |
| | `update_fleet` | Update fleet | `id`, `data` |
| | `delete_fleet` | Delete fleet | `id` |
| **Vessel** | `list_vessels` | List/enumerate vessels | optional `query` |
| | `get_vessel` | Get vessel by ID | `id` |
| | `create_vessel` | Create vessel | `data` |
| | `update_vessel` | Update vessel | `id`, `data` |
| | `update_vessel_context` | Update vessel project context and style guide | `id`, `data` |
| | `delete_vessel` | Delete vessel | `id` |
| **Voyage** | `list_voyages` | List/enumerate voyages | optional `query` |
| | `get_voyage` | Get voyage by ID | `id` |
| | `create_voyage` | Create voyage | `data` |
| | `cancel_voyage` | Cancel voyage | `id` |
| | `purge_voyage` | Permanently delete voyage and all missions | `id` |
| **Mission** | `list_missions` | List/enumerate full mission objects | optional `query` |
| | `list_missions_summary` | List/enumerate lightweight mission summaries | optional `query` |
| | `get_mission` | Get mission by ID | `id` |
| | `create_mission` | Create and dispatch mission | `data` |
| | `update_mission` | Update mission | `id`, `data` |
| | `transition_mission_status` | Transition mission status | `id`, `status` |
| | `cancel_mission` | Cancel mission | `id` |
| | `purge_mission` | Permanently delete mission | `id` |
| | `restart_mission` | Restart failed/cancelled mission | `id`, optional `data.title`, `data.description` |
| **Captain** | `list_captains` | List/enumerate captains | optional `query` |
| | `get_captain` | Get captain by ID | `id` |
| | `create_captain` | Create captain | `data` |
| | `update_captain` | Update captain (preserves operational fields) | `id`, `data` |
| | `delete_captain` | Delete captain and its events and sessions; refused while Working, Planning or Refining or with an active mission | `id` |
| **Signal** | `list_signals` | List/enumerate signals | optional `query` |
| | `send_signal` | Create signal | `data` |
| **Event** | `list_events` | List/enumerate events | optional `query` |
| **Dock** | `list_docks` | List/enumerate docks | optional `query` |
| **Merge Queue** | `list_merge_queue` | List merge queue entries | optional `query` |
| | `get_merge_entry` | Get merge entry by ID | `id` |
| | `enqueue_merge` | Enqueue branch for merge | `data` |
| | `cancel_merge` | Cancel merge entry | `id` |
| | `process_merge_queue` | Process the merge queue | Ã¢â‚¬â€ |
| **Persona** | `get_persona` | Get a persona by name | `id` (persona name) |
| | `create_persona` | Create a persona | `data` (Persona object) |
| | `update_persona` | Update persona properties | `id` (persona name), `data` (partial Persona) |
| | `delete_persona` | Delete a custom persona (blocked for built-in) | `id` (persona name) |
| **Pipeline** | `get_pipeline` | Get a pipeline by name | `id` (pipeline name) |
| | `create_pipeline` | Create a pipeline with stages | `data` (Pipeline object with Stages) |
| | `update_pipeline` | Update pipeline and stages | `id` (pipeline name), `data` (partial Pipeline) |
| | `delete_pipeline` | Delete a custom pipeline (blocked for built-in) | `id` (pipeline name) |
| **Prompt Template** | `get_prompt_template` | Get a prompt template by name | `id` (template name) |
| | `update_prompt_template` | Update template content | `id` (template name), `data` (partial PromptTemplate) |

### Command Authorization

Every command has one declared rule in `WebSocketCommandRegistry`
(`src/Armada.Server/WebSocket/WebSocketCommandRegistry.cs`). The command handler
dispatches only declared commands and enforces the rule before a command runs,
for every caller:

1. An undeclared action returns `command.error` with `code` `unknown_command`.
2. A command without an authenticated caller returns `authentication_required`.
3. A `GlobalAdmin` command returns `global_administrator_required` to any caller
   who is not a global administrator.
4. A `TenantAdminScoped` command returns `tenant_administrator_required` to any
   caller who is neither a global nor a tenant administrator. The command finds
   its record through the shared caller scope and changes it only when
   `OwnershipPolicy.CanEdit` allows, so a tenant administrator changes only their
   own tenant's records.
5. A `ReadScoped` command finds its record through the shared caller scope that
   the REST route and MCP tool use (`OwnedRecordScope`, `OwnershipPolicy`). A
   record the caller may not read returns `not_found` with no record data, so
   the reply never confirms that another tenant's record exists.
6. A `ListScoped` command lists through the shared caller-scoped query, so the
   result holds only records the caller may read.

A refused command writes nothing. A command's rule is the stricter of its REST
route and its MCP tool. MCP reserves most tools for global administrators, so a
command is `GlobalAdmin` unless both its REST route and its MCP tool (when one
exists) admit any authenticated caller through the shared ownership rule, or
both admit tenant administrators (`TenantAdminScoped`). A command with no
counterpart would be `GlobalAdmin`. A unit test derives each
rule from `AuthorizationConfig` and `McpToolAccessPolicy` and fails when a
dispatched command has no declared rule.

The persona and pipeline changes find the record through the shared caller scope and apply `OwnershipPolicy.CanEdit`:
`not_found` for a record the caller cannot read, `forbidden` for one it can read
but not change.

**Census.** 59 commands. Before this rule, the handler checked no permission for
any command. Only the hub let a global administrator alone send commands, so a
narrower session could not reach the handler, but any other caller of the
handler could. The table records the state after the change and the gap each
command had against REST or MCP:

- **G0** - no gap beyond the missing handler rule (all 59 commands had that).
- **G1** - the record was found by name across every tenant with no ownership
  check (`get_persona`, `update_persona`, `delete_persona`, `get_pipeline`,
  `update_pipeline`, `delete_pipeline`, `get_prompt_template`). REST and MCP read
  through the caller scope and change only with `CanEdit`.
- **G2** - the body replaced server-owned fields that REST keeps: `update_fleet`
  and `update_vessel` could move the record to another tenant or owner, and
  `update_mission` replaced the whole mission, including status, owner, captain,
  vessel and voyage. They now share one merge rule with their REST routes
  (`FleetUpdateMerge`, `VesselUpdateMerge`, `MissionMetadataUpdate`); a mission
  vessel or voyage change returns `mission_binding_immutable`.
- **G3** - a create accepted `IsBuiltIn` from the body. REST forces it false.
- **G4** - a create stored any `DefaultCaptainId` without the default captain
  rule. REST `POST /api/v1/personas` and MCP `create_persona` had the same gap
  and now apply the rule too.

The same audit aligned REST and MCP with each other. `POST
/api/v1/captains/stop-all` and `POST /api/v1/merge-queue/process` act on every
tenant, so they need a global administrator on REST as on MCP. MCP persona and
pipeline changes admit tenant administrators as REST does; MCP `update_pipeline`
and `delete_pipeline` find the pipeline through the caller scope and apply
`CanEdit`. MCP `armada_update_mission` refuses a vessel or voyage change, as
REST and WebSocket do.

Change events: `cancel_voyage`, `cancel_mission`, `restart_mission` and
`transition_mission_status` deliver their events to the changed record's owner
scope and global administrators. No other command broadcasts.

| Command | Operation | REST route | REST level | REST record scope | MCP tool | Rule now | Gap fixed |
|---|---|---|---|---|---|---|---|
| `status` | Read | `GET /api/v1/status` | AdminOnly | fleet-wide, no record scope | `armada_status` (global admin) | GlobalAdmin | G0 |
| `stop_captain` | Action | `POST /api/v1/captains/{id}/stop` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_stop_captain` (global admin) | GlobalAdmin | G0 |
| `stop_all` | Action | `POST /api/v1/captains/stop-all` | AdminOnly | fleet-wide, no record scope | `armada_stop_all` (global admin) | GlobalAdmin | G0 |
| `stop_server` | Action | `POST /api/v1/server/stop` | AdminOnly | fleet-wide, no record scope | `armada_stop_server` (global admin) | GlobalAdmin | G0 |
| `list_fleets` | List | `GET /api/v1/fleets` | Authenticated | admin all / tenant admin tenant / user own | `armada_enumerate` (global admin) | GlobalAdmin | G0 |
| `get_fleet` | Read | `GET /api/v1/fleets/{id}` | Authenticated | admin all / tenant admin tenant / user own | `armada_get_fleet` (global admin) | GlobalAdmin | G0 |
| `create_fleet` | Create | `POST /api/v1/fleets` | TenantAdmin | owner = caller | `armada_create_fleet` (global admin) | GlobalAdmin | G0 |
| `update_fleet` | Update | `PUT /api/v1/fleets/{id}` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_update_fleet` (global admin) | GlobalAdmin | G2 |
| `delete_fleet` | Delete | `DELETE /api/v1/fleets/{id}` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_delete_fleet` (global admin) | GlobalAdmin | G0 |
| `list_vessels` | List | `GET /api/v1/vessels` | Authenticated | admin all / tenant admin tenant / user own | `armada_enumerate` (global admin) | GlobalAdmin | G0 |
| `get_vessel` | Read | `GET /api/v1/vessels/{id}` | Authenticated | admin all / tenant admin tenant / user own | `armada_get_vessel` (global admin) | GlobalAdmin | G0 |
| `create_vessel` | Create | `POST /api/v1/vessels` | TenantAdmin | owner = caller | `armada_add_vessel` (global admin) | GlobalAdmin | G0 |
| `update_vessel` | Update | `PUT /api/v1/vessels/{id}` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_update_vessel` (global admin) | GlobalAdmin | G2 |
| `update_vessel_context` | Update | `PATCH /api/v1/vessels/{id}/context` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_update_vessel_context` (global admin) | GlobalAdmin | G0 |
| `delete_vessel` | Delete | `DELETE /api/v1/vessels/{id}` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_delete_vessel` (global admin) | GlobalAdmin | G0 |
| `list_voyages` | List | `GET /api/v1/voyages` | Authenticated | admin all / tenant admin tenant / user own | `armada_enumerate` (global admin) | GlobalAdmin | G0 |
| `get_voyage` | Read | `GET /api/v1/voyages/{id}` | Authenticated | admin all / tenant admin tenant / user own | `armada_voyage_status` (global admin) | GlobalAdmin | G0 |
| `create_voyage` | Create | `POST /api/v1/voyages` | TenantAdmin | owner = caller | `armada_dispatch` (global admin) | GlobalAdmin | G0 |
| `cancel_voyage` | Action | `DELETE /api/v1/voyages/{id}` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_cancel_voyage` (global admin) | GlobalAdmin | G0 |
| `purge_voyage` | Delete | `DELETE /api/v1/voyages/{id}/purge` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_purge_voyage` (global admin) | GlobalAdmin | G0 |
| `list_missions` | List | `GET /api/v1/missions` | Authenticated | admin all / tenant admin tenant / user own | `armada_enumerate` (global admin) | GlobalAdmin | G0 |
| `list_missions_summary` | List | `GET /api/v1/missions/summaries` | Authenticated | caller-scoped query | - | ListScoped | G0 |
| `get_mission` | Read | `GET /api/v1/missions/{id}` | Authenticated | admin all / tenant admin tenant / user own | `armada_mission_status` (global admin) | GlobalAdmin | G0 |
| `create_mission` | Create | `POST /api/v1/missions` | TenantAdmin | owner = caller | `armada_create_mission` (global admin) | GlobalAdmin | G0 |
| `update_mission` | Update | `PUT /api/v1/missions/{id}` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_update_mission` (global admin) | GlobalAdmin | G2 |
| `transition_mission_status` | Action | `PUT /api/v1/missions/{id}/status` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_transition_mission_status` (global admin) | GlobalAdmin | G0 |
| `cancel_mission` | Action | `DELETE /api/v1/missions/{id}` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_cancel_mission` (global admin) | GlobalAdmin | G0 |
| `purge_mission` | Delete | `DELETE /api/v1/missions/{id}/purge` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_purge_mission` (global admin) | GlobalAdmin | G0 |
| `restart_mission` | Action | `POST /api/v1/missions/{id}/restart` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_restart_mission` (global admin) | GlobalAdmin | G0 |
| `get_mission_diff` | Read | `GET /api/v1/missions/{id}/diff` | Authenticated | admin all / tenant admin tenant / user own | `armada_get_mission_diff` (global admin) | GlobalAdmin | G0 |
| `get_mission_log` | Read | `GET /api/v1/missions/{id}/log` | Authenticated | admin all / tenant admin tenant / user own | `armada_get_mission_log` (global admin) | GlobalAdmin | G0 |
| `list_captains` | List | `GET /api/v1/captains` | Authenticated | admin all / tenant admin tenant / user own | `armada_enumerate` (global admin) | GlobalAdmin | G0 |
| `get_captain` | Read | `GET /api/v1/captains/{id}` | Authenticated | admin all / tenant admin tenant / user own | `armada_get_captain` (global admin) | GlobalAdmin | G0 |
| `create_captain` | Create | `POST /api/v1/captains` | TenantAdmin | owner = caller | `armada_create_captain` (global admin) | GlobalAdmin | G0 |
| `update_captain` | Update | `PUT /api/v1/captains/{id}` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_update_captain` (global admin) | GlobalAdmin | G0 |
| `delete_captain` | Delete | `DELETE /api/v1/captains/{id}` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_delete_captain` (global admin) | GlobalAdmin | G0 |
| `get_captain_log` | Read | `GET /api/v1/captains/{id}/log` | Authenticated | admin all / tenant admin tenant / user own | `armada_get_captain_log` (global admin) | GlobalAdmin | G0 |
| `list_signals` | List | `GET /api/v1/signals` | Authenticated | admin all / tenant admin tenant / user own | `armada_enumerate` (global admin) | GlobalAdmin | G0 |
| `send_signal` | Create | `POST /api/v1/signals` | TenantAdmin | owner = caller | `armada_send_signal` (global admin) | GlobalAdmin | G0 |
| `list_events` | List | `GET /api/v1/events` | Authenticated | admin all / tenant admin tenant / user own | `armada_enumerate` (global admin) | GlobalAdmin | G0 |
| `list_docks` | List | `GET /api/v1/docks` | Authenticated | admin all / tenant admin tenant / user own | `armada_enumerate` (global admin) | GlobalAdmin | G0 |
| `list_merge_queue` | List | `GET /api/v1/merge-queue` | Authenticated | admin all / tenant admin tenant / user own | `armada_enumerate` (global admin) | GlobalAdmin | G0 |
| `get_merge_entry` | Read | `GET /api/v1/merge-queue/{id}` | Authenticated | admin all / tenant admin tenant / user own | `armada_get_merge_entry` (global admin) | GlobalAdmin | G0 |
| `enqueue_merge` | Create | `POST /api/v1/merge-queue` | TenantAdmin | owner = caller | `armada_enqueue_merge` (global admin) | GlobalAdmin | G0 |
| `cancel_merge` | Delete | `DELETE /api/v1/merge-queue/{id}` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_cancel_merge` (global admin) | GlobalAdmin | G0 |
| `process_merge_queue` | Action | `POST /api/v1/merge-queue/process` | AdminOnly | fleet-wide, no record scope | `armada_process_merge_queue` (global admin) | GlobalAdmin | G0 |
| `enumerate` | List | `POST /api/v1/<entity>/enumerate` | TenantAdmin | admin all / tenant admin tenant / user own | `armada_enumerate` (global admin) | GlobalAdmin | G0 |
| `backup` | Action | `GET /api/v1/backup` | AdminOnly | fleet-wide, no record scope | `armada_backup` (global admin) | GlobalAdmin | G0 |
| `restore` | Action | `POST /api/v1/restore` | AdminOnly | fleet-wide, no record scope | `armada_restore` (global admin) | GlobalAdmin | G0 |
| `get_persona` | Read | `GET /api/v1/personas/{name}` | Authenticated | shared caller scope (CanView) | `get_persona` (any caller; shared caller scope) | ReadScoped | G1 |
| `create_persona` | Create | `POST /api/v1/personas` | TenantAdmin | owner = caller | `create_persona` (global or tenant admin) | TenantAdminScoped | G3, G4 |
| `update_persona` | Update | `PUT /api/v1/personas/{name}` | TenantAdmin | tenant lookup + CanEdit (admin: all tenants) | `update_persona` (global or tenant admin; tenant lookup + CanEdit) | TenantAdminScoped | G1 |
| `delete_persona` | Delete | `DELETE /api/v1/personas/{name}` | TenantAdmin | tenant lookup + CanEdit (admin: all tenants) | `delete_persona` (global or tenant admin; tenant lookup + CanEdit) | TenantAdminScoped | G1 |
| `get_prompt_template` | Read | `GET /api/v1/prompt-templates/{name}` | Authenticated | shared caller scope (CanView) | `get_prompt_template` (any caller; shared caller scope) | ReadScoped | G1 |
| `update_prompt_template` | Update | `PUT /api/v1/prompt-templates/{name}` | AdminOnly | by name, all tenants | `update_prompt_template` (global admin) | GlobalAdmin | G0 |
| `get_pipeline` | Read | `GET /api/v1/pipelines/{name}` | Authenticated | shared caller scope (CanView) | `get_pipeline` (any caller; shared caller scope) | ReadScoped | G1 |
| `create_pipeline` | Create | `POST /api/v1/pipelines` | TenantAdmin | owner = caller | `create_pipeline` (global or tenant admin) | TenantAdminScoped | G3 |
| `update_pipeline` | Update | `PUT /api/v1/pipelines/{name}` | TenantAdmin | tenant lookup + CanEdit (admin: all tenants) | `update_pipeline` (global or tenant admin; tenant lookup + CanEdit) | TenantAdminScoped | G1 |
| `delete_pipeline` | Delete | `DELETE /api/v1/pipelines/{name}` | TenantAdmin | tenant lookup + CanEdit (admin: all tenants) | `delete_pipeline` (global or tenant admin; tenant lookup + CanEdit) | TenantAdminScoped | G1 |

REST levels are those `AuthorizationConfig` returns. "admin all / tenant admin
tenant / user own" means the route reads every tenant for a global
administrator, the caller's tenant for a tenant administrator, and the caller's
own records for anyone else. `enumerate` covers fleets, vessels, captains,
missions, voyages, docks, signals, events and the merge queue under one rule.

---

### Status & Control

#### status

Get the current Armada status.

**Request:**

```json
{
  "Route": "command",
  "action": "status"
}
```

**Response:**

```json
{
  "type": "command.result",
  "action": "status",
  "data": {
    "totalCaptains": 4,
    "idleCaptains": 1,
    "workingCaptains": 2,
    "stalledCaptains": 1,
    "activeVoyages": 2,
    "missionsByStatus": { "Pending": 3, "InProgress": 2 },
    "voyages": [],
    "recentSignals": [],
    "timestampUtc": "2026-03-07T12:34:56.789Z"
  }
}
```

**`data` field:** [ArmadaStatus](#armadastatus) object.

---

#### stop_captain

Stop a specific captain. It runs the same service as REST
`POST /api/v1/captains/{id}/stop` and MCP `armada_stop_captain`: a Planning or
Refining captain is stopped through its active planning or objective refinement
session, and any other captain has its process stopped and is recalled to Idle.
A captain that is not found, or whose session cannot be resolved, returns
`command.error` with the reason and the `outcome`.

**Request:**

```json
{
  "Route": "command",
  "action": "stop_captain",
  "captainId": "cpt_abc123def456ghi789jk"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"stop_captain"` |
| `captainId` | string | Yes | ID of the captain to stop |

**Response:**

```json
{
  "type": "command.result",
  "action": "stop_captain",
  "data": {
    "outcome": "Completed",
    "status": "stopped",
    "captainId": "cpt_abc123def456ghi789jk",
    "planningSessionId": null,
    "objectiveRefinementSessionId": null,
    "message": "Captain stopped"
  }
}
```

---

#### stop_all

Emergency stop of every working captain, active planning session and active
objective refinement session. It runs the same service as REST
`POST /api/v1/captains/stop-all` and MCP `armada_stop_all`, and `data` is the same
`CaptainStopAllResult`: `status` is `all_stopped` when every stop succeeded and
`stopped_with_failures` otherwise, with stopped and failed counts per kind and one
`failures` entry per captain or session that could not be stopped.

**Request:**

```json
{
  "Route": "command",
  "action": "stop_all"
}
```

**Response:**

```json
{
  "type": "command.result",
  "action": "stop_all",
  "data": {
    "status": "all_stopped",
    "stopped": 2,
    "failed": 0,
    "captainsStopped": 1,
    "captainsFailed": 0,
    "planningSessionsStopped": 1,
    "planningSessionsFailed": 0,
    "refinementSessionsStopped": 0,
    "refinementSessionsFailed": 0,
    "failures": []
  }
}
```

---

#### stop_server

Initiate a graceful shutdown of the Admiral server. The server will respond before shutting down after a brief delay.

**Request:**

```json
{
  "Route": "command",
  "action": "stop_server"
}
```

**Response:**

```json
{
  "type": "command.result",
  "action": "stop_server",
  "data": {
    "status": "shutting_down"
  }
}
```

---

### Fleet Actions

#### list_fleets

List or enumerate fleets with optional pagination and filtering.

**Request:**

```json
{
  "Route": "command",
  "action": "list_fleets",
  "query": {
    "pageNumber": 1,
    "pageSize": 25,
    "order": "CreatedDescending"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"list_fleets"` |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

**Response:**

```json
{
  "type": "command.result",
  "action": "list_fleets",
  "data": {
    "success": true,
    "pageNumber": 1,
    "pageSize": 25,
    "totalPages": 1,
    "totalRecords": 3,
    "objects": [
      { "id": "flt_abc123", "name": "my-fleet", "...": "..." }
    ],
    "totalMs": 1.23
  }
}
```

---

#### get_fleet

Get a fleet by ID.

**Request:**

```json
{
  "Route": "command",
  "action": "get_fleet",
  "id": "flt_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"get_fleet"` |
| `id` | string | Yes | Fleet ID (prefix `flt_`) |

**Response:**

```json
{
  "type": "command.result",
  "action": "get_fleet",
  "data": {
    "id": "flt_abc123",
    "name": "my-fleet",
    "...": "..."
  }
}
```

---

#### create_fleet

Create a new fleet.

**Request:**

```json
{
  "Route": "command",
  "action": "create_fleet",
  "data": {
    "Name": "my-fleet"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"create_fleet"` |
| `data` | object | Yes | Fleet creation data |

**Response:**

```json
{
  "type": "command.result",
  "action": "create_fleet",
  "data": {
    "id": "flt_abc123",
    "name": "my-fleet",
    "...": "..."
  }
}
```

---

#### update_fleet

Update an existing fleet.

**Request:**

```json
{
  "Route": "command",
  "action": "update_fleet",
  "id": "flt_abc123",
  "data": {
    "Name": "renamed-fleet"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"update_fleet"` |
| `id` | string | Yes | Fleet ID (prefix `flt_`) |
| `data` | object | Yes | Fields to update |

Like `PUT /api/v1/fleets/{id}`, the stored tenant, owner and creation time are
kept whatever `data` names, and `Active` and `DefaultPlaybooks` keep their stored
values unless `data` names them.

**Response:**

```json
{
  "type": "command.result",
  "action": "update_fleet",
  "data": {
    "id": "flt_abc123",
    "name": "renamed-fleet",
    "...": "..."
  }
}
```

---

#### delete_fleet

Delete a fleet.

**Request:**

```json
{
  "Route": "command",
  "action": "delete_fleet",
  "id": "flt_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"delete_fleet"` |
| `id` | string | Yes | Fleet ID (prefix `flt_`) |

**Response:**

```json
{
  "type": "command.result",
  "action": "delete_fleet",
  "data": {
    "success": true
  }
}
```

---

### Vessel Actions

#### list_vessels

List or enumerate vessels with optional pagination and filtering.

**Request:**

```json
{
  "Route": "command",
  "action": "list_vessels",
  "query": {
    "pageNumber": 1,
    "pageSize": 50,
    "fleetId": "flt_abc123"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"list_vessels"` |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

**Response:**

```json
{
  "type": "command.result",
  "action": "list_vessels",
  "data": {
    "success": true,
    "pageNumber": 1,
    "pageSize": 50,
    "totalPages": 1,
    "totalRecords": 5,
    "objects": [
      { "id": "vsl_abc123", "name": "my-repo", "...": "..." }
    ],
    "totalMs": 0.89
  }
}
```

---

#### get_vessel

Get a vessel by ID.

**Request:**

```json
{
  "Route": "command",
  "action": "get_vessel",
  "id": "vsl_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"get_vessel"` |
| `id` | string | Yes | Vessel ID (prefix `vsl_`) |

---

#### create_vessel

Create a new vessel.

**Request:**

```json
{
  "Route": "command",
  "action": "create_vessel",
  "data": {
    "Name": "my-repo",
    "FleetId": "flt_abc123",
    "RepositoryUrl": "https://github.com/org/repo.git"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"create_vessel"` |
| `data` | object | Yes | Vessel creation data |

---

#### update_vessel

Update an existing vessel.

**Request:**

```json
{
  "Route": "command",
  "action": "update_vessel",
  "id": "vsl_abc123",
  "data": {
    "Name": "renamed-repo"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"update_vessel"` |
| `id` | string | Yes | Vessel ID (prefix `vsl_`) |
| `data` | object | Yes | Fields to update |

Like `PUT /api/v1/vessels/{id}`, the stored tenant, owner, creation time and auto-land calibration count are kept whatever `data` names.

`data.gitHubTokenOverride` is write-only, on `create_vessel` and `update_vessel` alike. Omit it to keep the stored value; pass an empty string to clear it; any other value replaces it, trimmed. No result or event returns the value; vessel results carry `hasGitHubTokenOverride` instead.

---

#### update_vessel_context

Partial update of a vessel's project context and style guide fields only. Unlike `update_vessel`, this only modifies the `projectContext` and `styleGuide` fields.

**Request:**

```json
{
  "Route": "command",
  "action": "update_vessel_context",
  "id": "vsl_abc123",
  "data": {
    "ProjectContext": "C#/.NET multi-agent orchestration system...",
    "StyleGuide": "Use explicit types, no var keyword..."
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"update_vessel_context"` |
| `id` | string | Yes | Vessel ID (prefix `vsl_`) |
| `data.ProjectContext` | string | No | Project context describing architecture, key files, and dependencies |
| `data.StyleGuide` | string | No | Style guide describing naming conventions, patterns, and library preferences |

**Response:**

```json
{
  "type": "command.result",
  "action": "update_vessel_context",
  "data": {
    "id": "vsl_abc123",
    "name": "my-repo",
    "projectContext": "C#/.NET multi-agent orchestration system...",
    "styleGuide": "Use explicit types, no var keyword...",
    "...": "..."
  }
}
```

**Errors:** `command.error` if vessel not found.

---

#### delete_vessel

Delete a vessel.

**Request:**

```json
{
  "Route": "command",
  "action": "delete_vessel",
  "id": "vsl_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"delete_vessel"` |
| `id` | string | Yes | Vessel ID (prefix `vsl_`) |

---

### Voyage Actions

#### list_voyages

List or enumerate voyages with optional pagination and filtering.

**Request:**

```json
{
  "Route": "command",
  "action": "list_voyages",
  "query": {
    "pageNumber": 1,
    "pageSize": 25,
    "status": "InProgress"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"list_voyages"` |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

---

#### get_voyage

Get a voyage by ID. Returns the voyage object along with its missions.

**Request:**

```json
{
  "Route": "command",
  "action": "get_voyage",
  "id": "vyg_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"get_voyage"` |
| `id` | string | Yes | Voyage ID (prefix `vyg_`) |

**Response:**

```json
{
  "type": "command.result",
  "action": "get_voyage",
  "data": {
    "voyage": { "id": "vyg_abc123", "title": "Feature batch", "...": "..." },
    "missions": [
      { "id": "msn_abc123", "title": "Task 1", "status": "Complete", "...": "..." }
    ]
  }
}
```

---

#### create_voyage

Create a new voyage. Optionally include a `vesselId` and `missions[]` array for immediate dispatch.

**Request (basic):**

```json
{
  "Route": "command",
  "action": "create_voyage",
  "data": {
    "Title": "Feature batch 1",
    "Description": "Implement auth features"
  }
}
```

**Request (with missions for dispatch):**

```json
{
  "Route": "command",
  "action": "create_voyage",
  "data": {
    "Title": "Feature batch 1",
    "VesselId": "vsl_abc123",
    "Missions": [
      { "Title": "Add login page", "Description": "Create login form" },
      { "Title": "Add signup page", "Description": "Create signup form" }
    ]
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"create_voyage"` |
| `data` | object | Yes | Voyage creation data |
| `data.VesselId` | string | No | Target vessel for missions |
| `data.Missions` | array | No | Array of mission objects to create and dispatch |

---

#### cancel_voyage

Cancel a voyage. Every `Pending`, `Assigned`, `InProgress`, `Testing`, or `Review` mission is also cancelled, and the agent process of each running mission is stopped first. This is the same operation as `DELETE /api/v1/voyages/{id}`.

**Request:**

```json
{
  "Route": "command",
  "action": "cancel_voyage",
  "id": "vyg_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"cancel_voyage"` |
| `id` | string | Yes | Voyage ID (prefix `vyg_`) |

---

#### purge_voyage

Permanently delete a voyage and all of its missions.

**Request:**

```json
{
  "Route": "command",
  "action": "purge_voyage",
  "id": "vyg_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"purge_voyage"` |
| `id` | string | Yes | Voyage ID (prefix `vyg_`) |

---

### Mission Actions

#### list_missions

List or enumerate full mission objects with optional pagination and filtering.

**Request:**

```json
{
  "Route": "command",
  "action": "list_missions",
  "query": {
    "pageNumber": 1,
    "pageSize": 50,
    "voyageId": "vyg_abc123",
    "status": "InProgress"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"list_missions"` |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

---

#### list_missions_summary

List or enumerate lightweight mission summaries with optional pagination and filtering.

This action returns `MissionSummary` rows instead of full `Mission` objects. Use it for dashboards and status views that need IDs, status, routing fields, timestamps, and payload length hints without transferring `Description`, `DiffSnapshot`, or `AgentOutput`.

The result is an `EnumerationResult<MissionSummary>`, the same shape `GET /api/v1/missions/summaries` returns. The action reads through the same caller-scoped query as that route: a global administrator receives every tenant's summaries, a tenant administrator receives its tenant's, and any other caller receives only its own. A command without an authenticated session caller is refused with `command.error`.

**Request:**

```json
{
  "Route": "command",
  "action": "list_missions_summary",
  "query": {
    "pageNumber": 1,
    "pageSize": 50,
    "voyageId": "vyg_abc123",
    "status": "InProgress"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"list_missions_summary"` |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

**Summary object fields:**

| Field | Type | Description |
|---|---|---|
| `id` | string | Mission ID |
| `title` | string | Mission title |
| `status` | string | [MissionStatusEnum](#missionstatusenum) value |
| `vesselId` | string \| null | Linked vessel ID |
| `voyageId` | string \| null | Linked voyage ID |
| `captainId` | string \| null | Assigned captain ID |
| `branchName` | string \| null | Working branch name |
| `dockId` | string \| null | Linked dock ID |
| `processId` | int \| null | Local process ID when active |
| `prUrl` | string \| null | Pull request URL |
| `commitHash` | string \| null | Last recorded commit hash |
| `priority` | int | Mission priority |
| `parentMissionId` | string \| null | Parent mission ID |
| `persona` | string \| null | Assigned persona |
| `dependsOnMissionId` | string \| null | Dependency mission ID |
| `createdUtc` | string | Creation timestamp |
| `lastUpdateUtc` | string | Last update timestamp |
| `startedUtc` | string \| null | Start timestamp |
| `completedUtc` | string \| null | Completion timestamp |
| `descriptionLength` | int | Saved description length |
| `diffSnapshotLength` | int | Saved diff length |
| `agentOutputLength` | int | Saved agent output length |

---

#### get_mission

Get a mission by ID.

**Request:**

```json
{
  "Route": "command",
  "action": "get_mission",
  "id": "msn_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"get_mission"` |
| `id` | string | Yes | Mission ID (prefix `msn_`) |

---

#### create_mission

Create a new mission and dispatch it for assignment.

**Request:**

```json
{
  "Route": "command",
  "action": "create_mission",
  "data": {
    "Title": "Implement feature X",
    "Description": "Add the feature X to the system",
    "VesselId": "vsl_abc123",
    "VoyageId": "vyg_abc123",
    "Priority": 50
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"create_mission"` |
| `data` | object | Yes | Mission creation data |

---

#### update_mission

Update an existing mission.

**Request:**

```json
{
  "Route": "command",
  "action": "update_mission",
  "id": "msn_abc123",
  "data": {
    "Title": "Updated title",
    "Priority": 10
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"update_mission"` |
| `id` | string | Yes | Mission ID (prefix `msn_`) |
| `data` | object | Yes | Fields to update |

Like `PUT /api/v1/missions/{id}`, the command writes metadata only: `Title`,
`Description`, `Priority`, `BranchName`, `PrUrl`, `ParentMissionId` and
`DependsOnMissionId`. Status, owner, captain, dock and timestamps stay as
stored; change a status with `transition_mission_status`. A `VesselId` or
`VoyageId` that differs from the stored value returns `command.error` with
`code` `mission_binding_immutable` and writes nothing.

---

#### transition_mission_status

Transition a mission to a new status. The transition must be valid according to the [Mission Status Transitions](#mission-status-transitions) rules.

The command uses the same operator transition path as `PUT /api/v1/missions/{id}/status`.
A transition to `Complete` must pass the manual completion gates described in the REST
API reference: review and Judge authority, captain process release, Check state, and,
with no active dock, target ancestry of the mission commit. A refusal leaves the mission
unchanged and returns a `command.error` frame that names the reason:

```json
{
  "type": "command.error",
  "action": "transition_mission_status",
  "error": "Manual completion blocked: manual_completion_ancestry_unavailable",
  "reason": "manual_completion_ancestry_unavailable"
}
```

The `command.result` or `command.error` frame is a reply to the session that sent
the command; no other session receives it. An applied transition is announced to
subscribers as a mission change that reaches only the sessions that may read the
mission.

**Request:**

```json
{
  "Route": "command",
  "action": "transition_mission_status",
  "id": "msn_abc123",
  "status": "Complete"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"transition_mission_status"` |
| `id` | string | Yes | Mission ID (prefix `msn_`) |
| `status` | string | Yes | Target [MissionStatusEnum](#missionstatusenum) value |

**Response:**

```json
{
  "type": "command.result",
  "action": "transition_mission_status",
  "data": {
    "id": "msn_abc123",
    "status": "Complete",
    "...": "..."
  }
}
```

---

#### cancel_mission

Cancel a mission.

**Request:**

```json
{
  "Route": "command",
  "action": "cancel_mission",
  "id": "msn_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"cancel_mission"` |
| `id` | string | Yes | Mission ID (prefix `msn_`) |

---

#### purge_mission

Permanently delete a mission from the database. This action is irreversible.

**Request:**

```json
{
  "Route": "command",
  "action": "purge_mission",
  "id": "msn_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"purge_mission"` |
| `id` | string | Yes | Mission ID (prefix `msn_`) |

**Response:**

```json
{
  "type": "command.result",
  "action": "purge_mission",
  "data": {
    "status": "deleted",
    "missionId": "msn_abc123"
  }
}
```

**Errors:** `command.error` if mission not found.

---

#### restart_mission

Restart a failed or cancelled mission, resetting it to `Pending` for re-dispatch. Optionally update the title and description before restarting.

**Request:**

```json
{
  "Route": "command",
  "action": "restart_mission",
  "id": "msn_abc123",
  "data": {
    "title": "Updated title",
    "description": "Updated instructions"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"restart_mission"` |
| `id` | string | Yes | Mission ID (prefix `msn_`) |
| `data.title` | string | No | New title. Omit to keep original. |
| `data.description` | string | No | New description. Omit to keep original. |

**Response:**

```json
{
  "type": "command.result",
  "action": "restart_mission",
  "data": {
    "id": "msn_abc123",
    "status": "Pending",
    "title": "Updated title",
    "..."
  }
}
```

**Errors:** `command.error` if mission not found or not in `Failed`/`Cancelled` status.

---

#### get_mission_diff

Get the git diff for a mission. Returns a saved diff file if available, otherwise attempts a live diff from the worktree.

**Request:**

```json
{
  "Route": "command",
  "action": "get_mission_diff",
  "id": "msn_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"get_mission_diff"` |
| `id` | string | Yes | Mission ID (prefix `msn_`) |

**Response:**

```json
{
  "type": "command.result",
  "action": "get_mission_diff",
  "data": {
    "missionId": "msn_abc123",
    "branch": "armada/msn_abc123",
    "diff": "diff --git a/file.cs..."
  }
}
```

---

#### get_mission_log

Get the session log for a mission with pagination support.

**Request:**

```json
{
  "Route": "command",
  "action": "get_mission_log",
  "id": "msn_abc123",
  "lines": 50,
  "offset": 0
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"get_mission_log"` |
| `id` | string | Yes | Mission ID (prefix `msn_`) |
| `lines` | integer | No | Number of lines to return (default 100) |
| `offset` | integer | No | Line offset to start from (default 0) |

**Response:**

```json
{
  "type": "command.result",
  "action": "get_mission_log",
  "data": {
    "missionId": "msn_abc123",
    "log": "line1\nline2\n...",
    "lines": 50,
    "totalLines": 200
  }
}
```

---

### Captain Actions

#### list_captains

List or enumerate captains with optional pagination and filtering.

**Request:**

```json
{
  "Route": "command",
  "action": "list_captains",
  "query": {
    "pageNumber": 1,
    "pageSize": 25
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"list_captains"` |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

---

#### get_captain

Get a captain by ID.

**Request:**

```json
{
  "Route": "command",
  "action": "get_captain",
  "id": "cpt_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"get_captain"` |
| `id` | string | Yes | Captain ID (prefix `cpt_`) |

---

#### create_captain

Create a new captain. `data` accepts configuration fields only: `Name`, `Runtime`,
`Model`, `ModelEndpointId`, `ApiKey`, `ApiBaseUrl`, `SystemInstructions`,
`AllowedPersonas`, `PreferredPersona`, `RuntimeOptionsJson`, `Tier` and
`DefaultPlaybooks`. The captain starts `Idle`, unassigned and not quarantined. A
server-owned field (`Id`, `TenantId`, `UserId`, `State`, `CurrentMissionId`,
`CurrentDockId`, `ProcessId`, `RecoveryAttempts`, `LastHeartbeatUtc`,
`LastProcessAliveUtc`, `QuarantineUntilUtc`, `QuarantineReason`, `CreatedUtc`,
`LastUpdateUtc`) with a non-default value returns `command.error` starting with
`captain_server_owned_field:` and naming the field. A `Name` another captain
already has returns `command.error` with `A captain with that name already exists.`
and creates nothing.

**Request:**

```json
{
  "Route": "command",
  "action": "create_captain",
  "data": {
    "Name": "captain-1",
    "Runtime": "ClaudeCode"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"create_captain"` |
| `data` | object | Yes | Captain creation data |

---

#### update_captain

Replace an existing captain's configuration fields (the same fields `create_captain`
accepts). Server-owned fields always keep their stored values, including the
quarantine, tenant and process liveness. A `data` field that sets a server-owned
field to a different value returns `command.error` starting with
`captain_server_owned_field:` and naming the field; nothing is written.

**Request:**

```json
{
  "Route": "command",
  "action": "update_captain",
  "id": "cpt_abc123",
  "data": {
    "Name": "captain-primary"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"update_captain"` |
| `id` | string | Yes | Captain ID (prefix `cpt_`) |
| `data` | object | Yes | Fields to update |

---

#### delete_captain

Delete a captain, then remove the events, planning sessions and objective
refinement sessions that reference it. The rule is shared with REST and MCP: a
captain that is Working, Planning or Refining, or owns an Assigned or InProgress
mission, is refused with `command.error` and nothing changes. Stop it first.

**Request:**

```json
{
  "Route": "command",
  "action": "delete_captain",
  "id": "cpt_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"delete_captain"` |
| `id` | string | Yes | Captain ID (prefix `cpt_`) |

---

#### get_captain_log

Get the current session log for a captain with pagination support. The log is resolved via the `.current` pointer file.

**Request:**

```json
{
  "Route": "command",
  "action": "get_captain_log",
  "id": "cpt_abc123",
  "lines": 50,
  "offset": 0
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"get_captain_log"` |
| `id` | string | Yes | Captain ID (prefix `cpt_`) |
| `lines` | integer | No | Number of lines to return (default 100) |
| `offset` | integer | No | Line offset to start from (default 0) |

**Response:**

```json
{
  "type": "command.result",
  "action": "get_captain_log",
  "data": {
    "captainId": "cpt_abc123",
    "log": "line1\nline2\n...",
    "lines": 50,
    "totalLines": 150
  }
}
```

---

### Signal Actions

#### list_signals

List or enumerate signals with optional pagination and filtering.

**Request:**

```json
{
  "Route": "command",
  "action": "list_signals",
  "query": {
    "toCaptainId": "cpt_abc123",
    "unreadOnly": true
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"list_signals"` |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

---

#### send_signal

Create and send a signal.

**Request:**

```json
{
  "Route": "command",
  "action": "send_signal",
  "data": {
    "ToCaptainId": "cpt_abc123",
    "Type": "Nudge",
    "Payload": "{\"message\": \"Please check the test failures\"}"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"send_signal"` |
| `data` | object | Yes | Signal creation data |

---

### Event Actions

#### list_events

List or enumerate events with optional pagination and filtering.

**Request:**

```json
{
  "Route": "command",
  "action": "list_events",
  "query": {
    "pageSize": 50,
    "eventType": "escalation.triggered"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"list_events"` |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

---

### Dock Actions

#### list_docks

List or enumerate docks (git worktrees) with optional pagination and filtering.

**Request:**

```json
{
  "Route": "command",
  "action": "list_docks",
  "query": {
    "vesselId": "vsl_abc123"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"list_docks"` |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

---

### Merge Queue Actions

#### list_merge_queue

List merge queue entries with optional pagination and filtering.

**Request:**

```json
{
  "Route": "command",
  "action": "list_merge_queue",
  "query": {
    "pageNumber": 1,
    "pageSize": 25
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"list_merge_queue"` |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

---

#### get_merge_entry

Get a merge queue entry by ID.

**Request:**

```json
{
  "Route": "command",
  "action": "get_merge_entry",
  "id": "mrg_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"get_merge_entry"` |
| `id` | string | Yes | Merge entry ID |

---

#### enqueue_merge

Enqueue a branch for merge.

**Request:**

```json
{
  "Route": "command",
  "action": "enqueue_merge",
  "data": {
    "VesselId": "vsl_abc123",
    "BranchName": "feature/my-feature",
    "MissionId": "msn_abc123"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"enqueue_merge"` |
| `data` | object | Yes | Merge entry creation data |

---

#### cancel_merge

Cancel a merge queue entry.

**Request:**

```json
{
  "Route": "command",
  "action": "cancel_merge",
  "id": "mrg_abc123"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"cancel_merge"` |
| `id` | string | Yes | Merge entry ID |

---

#### process_merge_queue

Trigger processing of the merge queue.

**Request:**

```json
{
  "Route": "command",
  "action": "process_merge_queue"
}
```

**Response:**

```json
{
  "type": "command.result",
  "action": "process_merge_queue",
  "data": {
    "success": true
  }
}
```

---

### Backup and Restore Actions

#### backup

Create a verified provider-native backup of the configured database and archive it with settings and a manifest. Behaviour and archive contents match REST `GET /api/v1/backup`. A failure returns `command.error` with a stable reason in `error` and leaves no archive.

**Request:**

```json
{
  "Route": "command",
  "action": "backup",
  "data": {
    "OutputPath": "~/.armada/backups/my-backup.zip"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"backup"` |
| `data.OutputPath` | string | No | File path for the backup ZIP. Defaults to `<dataDirectory>/backups/armada-backup-{timestamp}.zip` |

**Response:**

```json
{
  "type": "command.result",
  "action": "backup",
  "data": {
    "Path": "~/.armada/backups/armada-backup-2026-03-11-120000.zip",
    "TimestampUtc": "2026-03-11T12:00:00.0000000Z",
    "DatabaseType": "Postgresql",
    "SchemaVersion": 9,
    "ServerArtifactPath": "",
    "SizeBytes": 245760,
    "RecordCounts": {
      "fleets": 2,
      "vessels": 5,
      "captains": 3,
      "missions": 42,
      "voyages": 8
    }
  }
}
```

---

#### restore

Restore a SQLite Armada database from a previously created backup ZIP file. Behaviour matches REST `POST /api/v1/restore`. On PostgreSQL, MySQL and SQL Server the command returns `command.error` with `restore_unsupported_for_provider_<type>` and changes nothing. An archive from another provider returns `backup_provider_mismatch`.

**Request:**

```json
{
  "Route": "command",
  "action": "restore",
  "data": {
    "FilePath": "~/.armada/backups/armada-backup-20260311T120000Z.zip"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"restore"` |
| `data.FilePath` | string | Yes | Path to the backup ZIP file to restore from |

**Response:**

```json
{
  "type": "command.result",
  "action": "restore",
  "data": {
    "Status": "restored",
    "SafetyBackupPath": "~/.armada/backups/pre-restore-2026-03-11-120000-1a2b3c4d.zip",
    "SchemaVersion": 9,
    "Message": "Database restored from armada-backup-20260311T120000Z.zip. Restart the server to reload the restored data."
  }
}
```

> **Note:** A verified safety backup is created before the SQLite database is replaced. Restart the server after restoring.

---

### Enumerate

#### enumerate

Generic paginated enumeration of any entity type with filtering and sorting. This is the WebSocket equivalent of the REST `POST /api/v1/{entity}/enumerate` endpoints and the MCP `armada_enumerate` tool.

**Request:**

```json
{
  "Route": "command",
  "action": "enumerate",
  "entityType": "missions",
  "query": {
    "pageNumber": 2,
    "pageSize": 25,
    "status": "InProgress"
  }
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `"enumerate"` |
| `entityType` | string | Yes | Entity type to enumerate (see table below) |
| `query` | object | No | [EnumerationQuery](#enumerationquery) for pagination/filtering |

**Supported entity types:**

| `entityType` value | Supported filters |
|---|---|
| `fleets` | `createdAfter`, `createdBefore` |
| `vessels` | `fleetId`, `createdAfter`, `createdBefore` |
| `captains` | `status` (Idle/Working/Stalled), `createdAfter`, `createdBefore` |
| `missions` | `status`, `vesselId`, `captainId`, `voyageId`, `createdAfter`, `createdBefore` |
| `voyages` | `status` (Active/Complete/Cancelled), `createdAfter`, `createdBefore` |
| `docks` | `vesselId`, `createdAfter`, `createdBefore` |
| `signals` | `signalType`, `captainId`, `toCaptainId`, `unreadOnly`, `createdAfter`, `createdBefore` |
| `events` | `eventType`, `captainId`, `missionId`, `vesselId`, `voyageId`, `createdAfter`, `createdBefore` |
| `merge_queue` | `status` (Queued/Testing/Passed/Failed/Landed/Cancelled), `createdAfter`, `createdBefore` |

Singular forms (e.g., `"fleet"`, `"mission"`) are also accepted.

**Response:**

```json
{
  "type": "command.result",
  "action": "enumerate",
  "data": {
    "objects": [ ... ],
    "totalRecords": 42,
    "pageSize": 25,
    "pageNumber": 2,
    "totalPages": 2,
    "success": true,
    "totalMs": 1.23
  }
}
```

**Error (unknown entity type):**

```json
{
  "type": "command.error",
  "action": "enumerate",
  "error": "Unknown entity type: bananas. Valid types: fleets, vessels, captains, missions, voyages, docks, signals, events, merge_queue"
}
```

---

## Pagination

All `list_*` actions and the `enumerate` action support an optional `query` object for pagination and filtering via [EnumerationQuery](#enumerationquery).

### EnumerationQuery

| Field | Type | Default | Description |
|---|---|---|---|
| `pageNumber` | int | 1 | Page number (1-based) |
| `pageSize` | int | 100 | Results per page (min 1, max 1000) |
| `order` | string | `"CreatedDescending"` | Sort order: `"CreatedAscending"` or `"CreatedDescending"` |
| `createdAfter` | string | null | Filter: only records created after this ISO 8601 datetime |
| `createdBefore` | string | null | Filter: only records created before this ISO 8601 datetime |
| `status` | string | null | Filter by status string (e.g., `"InProgress"`, `"Pending"`) |
| `fleetId` | string | null | Filter by fleet ID |
| `vesselId` | string | null | Filter by vessel ID |
| `captainId` | string | null | Filter by captain ID |
| `voyageId` | string | null | Filter by voyage ID |
| `missionId` | string | null | Filter by mission ID |
| `eventType` | string | null | Filter events by type |
| `signalType` | string | null | Filter signals by [SignalTypeEnum](#signaltypeenum) |
| `toCaptainId` | string | null | Filter signals by recipient captain |
| `unreadOnly` | bool | false | Filter signals to unread only |

### Enumeration Response Format

All `list_*` actions return a paginated response:

```json
{
  "type": "command.result",
  "action": "list_missions",
  "data": {
    "success": true,
    "pageNumber": 1,
    "pageSize": 100,
    "totalPages": 3,
    "totalRecords": 245,
    "objects": [ "..." ],
    "totalMs": 2.45
  }
}
```

| Field | Type | Description |
|---|---|---|
| `success` | bool | Whether the query succeeded |
| `pageNumber` | int | Current page number |
| `pageSize` | int | Results per page |
| `totalPages` | int | Total number of pages |
| `totalRecords` | int | Total matching records |
| `objects` | array | Array of result objects |
| `totalMs` | float | Query execution time in milliseconds |

---

## Mission Status Transitions

Not all status transitions are valid. The following table documents the allowed transitions:

| From | Allowed To |
|---|---|
| `Pending` | `Assigned`, `Cancelled` |
| `Assigned` | `InProgress`, `Cancelled` |
| `InProgress` | `WorkProduced`, `Testing`, `Review`, `Complete`, `Failed`, `Cancelled` |
| `WorkProduced` | `PullRequestOpen`, `Complete`, `LandingFailed`, `Cancelled` |
| `PullRequestOpen` | `Complete`, `LandingFailed`, `Cancelled` |
| `Testing` | `Review`, `InProgress`, `Complete`, `Failed`, `Cancelled` |
| `Review` | `Complete`, `InProgress`, `Failed`, `Cancelled` |
| `LandingFailed` | `WorkProduced`, `Failed`, `Cancelled` |

Invalid transitions will return a `command.error` response.

---

## Error Handling

### Command Errors

When a command fails, the server returns a `command.error` message:

```json
{
  "type": "command.error",
  "action": "get_fleet",
  "error": "Fleet not found"
}
```

If the command body cannot be parsed or an exception occurs:

```json
{
  "type": "command.error",
  "error": "Unexpected character encountered while parsing value"
}
```

### Unknown Actions

Sending an unrecognized `action` value returns:

```json
{
  "type": "command.error",
  "action": "bad_action",
  "error": "Unknown action: bad_action",
  "code": "unknown_command"
}
```

### Authorization Refusals

A refused command returns `command.error` with a `code` and writes nothing:

| Code | Meaning |
|---|---|
| `unknown_command` | The action is not declared. |
| `authentication_required` | The command has no authenticated caller. |
| `global_administrator_required` | The command needs a global administrator. |
| `not_found` | The record does not exist or the caller may not read it. The two read the same. |
| `forbidden` | The caller may read the record but not change it. |

```json
{
  "type": "command.error",
  "action": "update_persona",
  "error": "update_persona requires a global administrator",
  "code": "global_administrator_required"
}
```

### Unknown Routes

Sending a message to a route other than `subscribe` or `command` returns:

```json
{
  "type": "error",
  "message": "Unknown route: bad_route"
}
```

### No Route Specified

If a message is sent without a route:

```json
{
  "type": "error",
  "message": "Send a message with route 'subscribe' or 'command'"
}
```

---

## Data Types

### Models

#### ArmadaStatus

| Field | Type | Description |
|---|---|---|
| `totalCaptains` | int | Total registered captains |
| `idleCaptains` | int | Captains in Idle state |
| `workingCaptains` | int | Captains in Working state |
| `stalledCaptains` | int | Captains in Stalled state |
| `activeVoyages` | int | Number of active (non-complete) voyages |
| `missionsByStatus` | object | Map of status string to count (e.g., `{"Pending": 3, "InProgress": 2}`) |
| `voyages` | array | List of [VoyageProgress](#voyageprogress) objects |
| `recentSignals` | array | List of recent [Signal](#signal) objects |
| `remoteTunnel` | [RemoteTunnelStatus](#remotetunnelstatus) | Current outbound remote tunnel status |
| `timestampUtc` | string | ISO 8601 UTC timestamp of the snapshot |

#### RemoteTunnelStatus

| Field | Type | Description |
|---|---|---|
| `enabled` | bool | Whether the remote tunnel feature is enabled |
| `state` | string | Tunnel state (`Disabled`, `Disconnected`, `Connecting`, `Connected`, `Error`, `Stopping`) |
| `tunnelUrl` | string \| null | Configured or normalized websocket endpoint |
| `instanceId` | string \| null | Stable instance identifier advertised during handshake |
| `lastConnectAttemptUtc` | string \| null | Last connection attempt timestamp |
| `connectedUtc` | string \| null | Last successful connection timestamp |
| `lastHeartbeatUtc` | string \| null | Last heartbeat or inbound tunnel activity timestamp |
| `lastDisconnectUtc` | string \| null | Last disconnect timestamp |
| `lastError` | string \| null | Last recorded tunnel error |
| `reconnectAttempts` | int | Consecutive reconnect attempts since the last successful connection |
| `latencyMs` | int \| null | Last successful ping/pong latency in milliseconds |
| `capabilityManifest` | object | Current handshake capability manifest |

#### VoyageProgress

| Field | Type | Description |
|---|---|---|
| `voyage` | object | [Voyage](#voyage) object |
| `totalMissions` | int | Total missions in this voyage |
| `completedMissions` | int | Missions with status Complete |
| `failedMissions` | int | Missions with status Failed |
| `inProgressMissions` | int | Missions currently in progress |

#### Voyage

| Field | Type | Description |
|---|---|---|
| `id` | string | Voyage ID (prefix `vyg_`) |
| `title` | string | Voyage title |
| `description` | string \| null | Voyage description |
| `status` | string | [VoyageStatusEnum](#voyagestatusenum) value |
| `createdUtc` | string | ISO 8601 creation timestamp |
| `completedUtc` | string \| null | ISO 8601 completion timestamp |
| `lastUpdateUtc` | string | ISO 8601 last update timestamp |
| `autoPush` | bool \| null | Override global auto-push setting |
| `autoCreatePullRequests` | bool \| null | Override global auto-create PR setting |
| `autoMergePullRequests` | bool \| null | Override global auto-merge PR setting |
| `landingMode` | string \| null | [LandingModeEnum](#landingmodeenum) Ã¢â‚¬â€ per-voyage landing policy override |

#### Vessel

| Field | Type | Description |
|---|---|---|
| `id` | string | Vessel ID (prefix `vsl_`) |
| `fleetId` | string \| null | Parent fleet ID |
| `name` | string | Vessel name |
| `repoUrl` | string \| null | Remote repository URL |
| `localPath` | string \| null | Local path to the bare repository clone |
| `workingDirectory` | string \| null | Local working directory for merge on completion |
| `defaultBranch` | string | Default branch name (default `"main"`) |
| `projectContext` | string \| null | Project context describing architecture, key files, and dependencies |
| `styleGuide` | string \| null | Style guide describing naming conventions, patterns, and library preferences |
| `landingMode` | string \| null | [LandingModeEnum](#landingmodeenum) Ã¢â‚¬â€ per-vessel landing policy override |
| `branchCleanupPolicy` | string \| null | [BranchCleanupPolicyEnum](#branchcleanuppolicyenum) Ã¢â‚¬â€ per-vessel branch cleanup override |
| `active` | bool | Whether the vessel is active |
| `createdUtc` | string | ISO 8601 creation timestamp |
| `lastUpdateUtc` | string | ISO 8601 last update timestamp |

#### Mission

| Field | Type | Description |
|---|---|---|
| `id` | string | Mission ID (prefix `msn_`) |
| `voyageId` | string \| null | Parent voyage ID |
| `vesselId` | string \| null | Target vessel ID |
| `captainId` | string \| null | Assigned captain ID |
| `title` | string | Mission title |
| `description` | string \| null | Mission description |
| `status` | string | [MissionStatusEnum](#missionstatusenum) value |
| `priority` | int | Priority (lower = higher priority, default 100) |
| `parentMissionId` | string \| null | Parent mission ID for sub-tasks |
| `branchName` | string \| null | Git branch created for this mission |
| `dockId` | string \| null | Assigned dock ID for this mission's worktree |
| `processId` | int \| null | OS process ID for the agent working this mission |
| `prUrl` | string \| null | Pull request URL |
| `commitHash` | string \| null | Git commit hash (HEAD) captured at mission completion |
| `diffSnapshot` | string \| null | Saved git diff snapshot captured at mission completion |
| `createdUtc` | string | ISO 8601 creation timestamp |
| `startedUtc` | string \| null | ISO 8601 start timestamp |
| `completedUtc` | string \| null | ISO 8601 completion timestamp |
| `lastUpdateUtc` | string | ISO 8601 last update timestamp |

#### Captain

| Field | Type | Description |
|---|---|---|
| `id` | string | Captain ID (prefix `cpt_`) |
| `name` | string | Display name |
| `runtime` | string | [AgentRuntimeEnum](#agentruntimeenum) value |
| `state` | string | [CaptainStateEnum](#captainstateenum) value |
| `currentMissionId` | string \| null | Currently assigned mission |
| `currentDockId` | string \| null | Currently assigned dock (worktree) |
| `processId` | int \| null | OS process ID |
| `recoveryAttempts` | int | Number of recovery attempts |
| `lastHeartbeatUtc` | string \| null | ISO 8601 last heartbeat timestamp |
| `createdUtc` | string | ISO 8601 creation timestamp |
| `lastUpdateUtc` | string | ISO 8601 last update timestamp |

#### Signal

| Field | Type | Description |
|---|---|---|
| `id` | string | Signal ID (prefix `sig_`) |
| `fromCaptainId` | string \| null | Sender captain ID (null = Admiral) |
| `toCaptainId` | string \| null | Recipient captain ID (null = Admiral) |
| `type` | string | [SignalTypeEnum](#signaltypeenum) value |
| `payload` | string \| null | JSON payload string |
| `read` | bool | Whether the signal has been read |
| `createdUtc` | string | ISO 8601 creation timestamp |

#### ArmadaEvent

| Field | Type | Description |
|---|---|---|
| `id` | string | Event ID |
| `type` | string | Event type string |
| `message` | string | Human-readable description |
| `data` | string \| null | JSON data payload |
| `createdUtc` | string | ISO 8601 creation timestamp |

#### Dock

| Field | Type | Description |
|---|---|---|
| `id` | string | Dock ID (prefix `dck_`) |
| `vesselId` | string | Parent vessel ID |
| `captainId` | string \| null | Assigned captain ID |
| `worktreePath` | string \| null | Filesystem path to the worktree |
| `branchName` | string \| null | Current branch name |
| `active` | bool | Whether the dock is active and usable |
| `createdUtc` | string | ISO 8601 creation timestamp |
| `lastUpdateUtc` | string | ISO 8601 last update timestamp |

#### MergeEntry

| Field | Type | Description |
|---|---|---|
| `id` | string | Merge entry ID |
| `vesselId` | string | Target vessel ID |
| `missionId` | string \| null | Associated mission ID |
| `branchName` | string | Branch to merge |
| `status` | string | [MergeStatusEnum](#mergestatusenum) value |
| `createdUtc` | string | ISO 8601 creation timestamp |
| `completedUtc` | string \| null | ISO 8601 completion timestamp |

---

### Enumerations

#### MissionStatusEnum

| Value | Description |
|---|---|
| `Pending` | Not yet assigned to a captain |
| `Assigned` | Assigned to a captain, not yet started |
| `InProgress` | Captain actively working |
| `WorkProduced` | Agent exited successfully; work ready for landing |
| `PullRequestOpen` | Pull request created, awaiting merge confirmation |
| `Testing` | Work complete, under automated testing |
| `Review` | Awaiting human review |
| `Complete` | Successfully completed (code landed) |
| `Failed` | Mission failed |
| `LandingFailed` | Landing (merge/PR) failed; may be retried |
| `Cancelled` | Mission was cancelled |

#### LandingModeEnum

| Value | Description |
|---|---|
| `LocalMerge` | Merge branch into default branch locally and push |
| `PullRequest` | Create a pull request and poll for merge confirmation |
| `MergeQueue` | Enqueue the branch into Armada's merge queue |
| `None` | No automated landing; leave work on the branch |

#### BranchCleanupPolicyEnum

| Value | Description |
|---|---|
| `LocalOnly` | Delete the local branch after landing |
| `LocalAndRemote` | Delete both local and remote branches after landing |
| `None` | Do not delete branches after landing |

#### VoyageStatusEnum

| Value | Description |
|---|---|
| `Open` | Voyage created, missions being set up |
| `InProgress` | Has active missions in progress |
| `Complete` | All missions completed |
| `Cancelled` | Voyage was cancelled |

#### CaptainStateEnum

| Value | Description |
|---|---|
| `Idle` | Available for assignment |
| `Working` | Actively working on a mission |
| `Stalled` | Process appears stalled (no heartbeat) |
| `Stopping` | In process of stopping |

#### SignalTypeEnum

| Value | Description |
|---|---|
| `Assignment` | Mission assignment notification |
| `Progress` | Progress update from captain |
| `Completion` | Mission completion notification |
| `Error` | Error notification |
| `Heartbeat` | Heartbeat signal |
| `Nudge` | Ephemeral nudge message |
| `Mail` | Persistent mail message |

#### AgentRuntimeEnum

| Value | Description |
|---|---|
| `ClaudeCode` | Anthropic Claude Code CLI |
| `Codex` | OpenAI Codex CLI |
| `Gemini` | Google Gemini CLI |
| `Cursor` | Cursor agent CLI |
| `Mux` | Mux CLI |
| `Custom` | Custom agent runtime |

#### MergeStatusEnum

| Value | Description |
|---|---|
| `Pending` | Waiting in the queue |
| `InProgress` | Currently being merged |
| `Complete` | Successfully merged |
| `Failed` | Merge failed |
| `Cancelled` | Merge was cancelled |

#### EnumerationOrderEnum

| Value | Description |
|---|---|
| `CreatedAscending` | Sort by creation time, oldest first |
| `CreatedDescending` | Sort by creation time, newest first |

---

## Client Examples

### JavaScript

```javascript
const ws = new WebSocket("ws://localhost:7890/ws");

ws.onopen = () => {
  // Subscribe to receive real-time broadcasts
  ws.send(JSON.stringify({ Route: "subscribe" }));
};

ws.onmessage = (event) => {
  const msg = JSON.parse(event.data);

  switch (msg.type) {
    case "status.snapshot":
      console.log("Initial status:", msg.data);
      break;
    case "mission.changed":
      console.log(`Mission ${msg.missionId}: ${msg.status}`);
      break;
    case "captain.changed":
      console.log(`Captain ${msg.captainId}: ${msg.state}`);
      break;
    case "command.result":
      console.log(`Command '${msg.action}' result:`, msg.data);
      break;
    case "command.error":
      console.error(`Command '${msg.action}' error:`, msg.error);
      break;
  }
};

// List fleets with pagination
ws.send(JSON.stringify({
  Route: "command",
  action: "list_fleets",
  query: { pageNumber: 1, pageSize: 25 }
}));

// Get a specific fleet
ws.send(JSON.stringify({
  Route: "command",
  action: "get_fleet",
  id: "flt_abc123def456ghi789jk"
}));

// Create a new voyage with missions
ws.send(JSON.stringify({
  Route: "command",
  action: "create_voyage",
  data: {
    Title: "Feature batch 1",
    VesselId: "vsl_abc123def456ghi789jk",
    Missions: [
      { Title: "Add login page", Description: "Create the login form" },
      { Title: "Add signup page", Description: "Create the signup form" }
    ]
  }
}));

// Transition a mission status
ws.send(JSON.stringify({
  Route: "command",
  action: "transition_mission_status",
  id: "msn_abc123def456ghi789jk",
  status: "Complete"
}));

// Update a captain
ws.send(JSON.stringify({
  Route: "command",
  action: "update_captain",
  id: "cpt_abc123def456ghi789jk",
  data: { Name: "captain-primary" }
}));

// Delete a vessel
ws.send(JSON.stringify({
  Route: "command",
  action: "delete_vessel",
  id: "vsl_abc123def456ghi789jk"
}));

// Stop a specific captain
ws.send(JSON.stringify({
  Route: "command",
  action: "stop_captain",
  captainId: "cpt_abc123def456ghi789jk"
}));
```

### C# / .NET

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

ClientWebSocket ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri("ws://localhost:7890/ws"), CancellationToken.None);

// Subscribe to broadcasts
string subscribe = JsonSerializer.Serialize(new { Route = "subscribe" });
byte[] subscribeBytes = Encoding.UTF8.GetBytes(subscribe);
await ws.SendAsync(subscribeBytes, WebSocketMessageType.Text, true, CancellationToken.None);

// Send a command: list missions filtered by voyage
string listMissions = JsonSerializer.Serialize(new
{
    Route = "command",
    action = "list_missions",
    query = new
    {
        pageNumber = 1,
        pageSize = 50,
        voyageId = "vyg_abc123def456ghi789jk",
        status = "InProgress"
    }
});
byte[] listBytes = Encoding.UTF8.GetBytes(listMissions);
await ws.SendAsync(listBytes, WebSocketMessageType.Text, true, CancellationToken.None);

// Send a command: create a fleet
string createFleet = JsonSerializer.Serialize(new
{
    Route = "command",
    action = "create_fleet",
    data = new { Name = "my-fleet" }
});
byte[] createBytes = Encoding.UTF8.GetBytes(createFleet);
await ws.SendAsync(createBytes, WebSocketMessageType.Text, true, CancellationToken.None);

// Send a command: transition mission status
string transitionMission = JsonSerializer.Serialize(new
{
    Route = "command",
    action = "transition_mission_status",
    id = "msn_abc123def456ghi789jk",
    status = "Complete"
});
byte[] transitionBytes = Encoding.UTF8.GetBytes(transitionMission);
await ws.SendAsync(transitionBytes, WebSocketMessageType.Text, true, CancellationToken.None);

// Receive messages
byte[] buffer = new byte[8192];
while (ws.State == WebSocketState.Open)
{
    WebSocketReceiveResult result = await ws.ReceiveAsync(buffer, CancellationToken.None);
    string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
    JsonDocument doc = JsonDocument.Parse(json);
    string type = doc.RootElement.GetProperty("type").GetString() ?? "";

    switch (type)
    {
        case "status.snapshot":
            Console.WriteLine($"Status snapshot received");
            break;
        case "mission.changed":
            string missionId = doc.RootElement.GetProperty("missionId").GetString() ?? "";
            string missionStatus = doc.RootElement.GetProperty("status").GetString() ?? "";
            Console.WriteLine($"Mission {missionId}: {missionStatus}");
            break;
        case "captain.changed":
            string captainId = doc.RootElement.GetProperty("captainId").GetString() ?? "";
            string state = doc.RootElement.GetProperty("state").GetString() ?? "";
            Console.WriteLine($"Captain {captainId}: {state}");
            break;
        case "command.result":
            string action = doc.RootElement.GetProperty("action").GetString() ?? "";
            Console.WriteLine($"Command '{action}' succeeded");
            break;
        case "command.error":
            string error = doc.RootElement.GetProperty("error").GetString() ?? "";
            Console.WriteLine($"Command error: {error}");
            break;
    }
}
```

### Python

```python
import asyncio
import json
import websockets

async def main():
    async with websockets.connect("ws://localhost:7890/ws") as ws:
        # Subscribe to broadcasts
        await ws.send(json.dumps({"Route": "subscribe"}))

        # List fleets with pagination
        await ws.send(json.dumps({
            "Route": "command",
            "action": "list_fleets",
            "query": {"pageNumber": 1, "pageSize": 25}
        }))

        # Get a specific voyage
        await ws.send(json.dumps({
            "Route": "command",
            "action": "get_voyage",
            "id": "vyg_abc123def456ghi789jk"
        }))

        # Create a mission
        await ws.send(json.dumps({
            "Route": "command",
            "action": "create_mission",
            "data": {
                "Title": "Implement feature X",
                "Description": "Add feature X to the system",
                "VesselId": "vsl_abc123def456ghi789jk",
                "VoyageId": "vyg_abc123def456ghi789jk"
            }
        }))

        # Transition mission status
        await ws.send(json.dumps({
            "Route": "command",
            "action": "transition_mission_status",
            "id": "msn_abc123def456ghi789jk",
            "status": "Review"
        }))

        # Update a captain
        await ws.send(json.dumps({
            "Route": "command",
            "action": "update_captain",
            "id": "cpt_abc123def456ghi789jk",
            "data": {"Name": "captain-primary"}
        }))

        # Delete a fleet
        await ws.send(json.dumps({
            "Route": "command",
            "action": "delete_fleet",
            "id": "flt_abc123def456ghi789jk"
        }))

        # Receive events
        async for message in ws:
            event = json.loads(message)
            event_type = event.get("type")

            if event_type == "status.snapshot":
                print(f"Status: {event['data']}")
            elif event_type == "mission.changed":
                print(f"Mission {event['missionId']}: {event['status']}")
            elif event_type == "captain.changed":
                print(f"Captain {event['captainId']}: {event['state']}")
            elif event_type == "command.result":
                print(f"Command '{event['action']}' result: {event['data']}")
            elif event_type == "command.error":
                print(f"Command error: {event.get('error')}")

asyncio.run(main())
```
