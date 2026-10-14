---
type: "Reference"
title: "WebSocket event types (65) and who receives them"
description: "Tenant-scoped: mission/voyage/captain/check-run/objective/deployment/incident/runbook-execution .changed, deployment.progress, environment.health, approval-needed, planning-session.* and objective-refinement-session.*, generic delete/purge/landing events; user-scoped ask.*; connection-scoped status.snapshot."
timestamp: "2026-10-05T05:50:03.8780540Z"
created: "2026-10-05T05:50:02.7941750Z"
slug: "api-websocket-events"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["api-websocket-connection", "arch-websocket-hub", "term-armada-event"]
version: "1"
salience: "0.5"
---
Frozen list in docs/API_SURFACE_1.0.md "Events" (65 types), declared in WebSocketSurface.
Dedicated payloads: status.snapshot (ArmadaStatus + allTenants; on subscribe); mission.changed {id, title, status, voyageId}; voyage.changed {id, title, status}; captain.changed {id, name, state}; check-run.changed (CheckRun); objective.changed (Objective); deployment.changed (Deployment); deployment.progress {id, title, status, verificationStatus, environmentId/Name, timestamps}; environment.health {...monitoring summary}; incident.changed; runbook-execution.changed; approval-needed {entityType, entityId, missionId, title, status, vesselId, voyageId, reviewRequestedUtc}; planning-session.changed/.message.created/.message.updated/.thinking/.tool/.summary.created {draft}/.dispatch.created {voyageId}/.deleted; objective-refinement-session.changed/.message.created/.updated/.summary.created/.applied/.deleted.
Ask (owner sockets only, all with threadId): ask.turn {turnId, state started|completed|failed|cancelled, messageId, error?}, ask.chunk {delta}, ask.thinking {delta}, ask.tool {phase started|completed, id, name, arguments, ok, elapsedMs, result}, ask.message, ask.proposal, ask.work {trackedWorkId, snapshot, trackedWork}, ask.thread.
Generic events (payload {entityType, entityId, captainId, missionId, vesselId, voyageId} + message): captain.launched, captain.batch_deleted, dock.deleted/purged/repaired/unstuck/batch_deleted, event.deleted/batch_deleted, fleet.batch_deleted, merge.purged/batch_purged, mission.completed/deleted/landing_failed/manual_complete_no_dock/pull_request_open/restarted/review_approved/review_denied/status_changed/work_produced/batch_deleted, planning-session.created/stopped, objective-refinement-session.created/stopped, signal/vessel/voyage batch_deleted, voyage.deleted. Some session events are delivered twice (dedicated + generic; tell apart by sessionId vs entityId). Event-log-only types (e.g. captain.quarantined) are not WebSocket events.
