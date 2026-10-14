---
type: "Reference"
title: "Event (ArmadaEvent, evt_): the system event log"
description: "ArmadaEvent (evt_) rows record what happened (EventType like mission.failed, captain.quarantined, papercut) with entity and related ids; many are also pushed over WebSocket."
timestamp: "2026-10-05T05:29:37.2587170Z"
created: "2026-10-05T05:29:36.8455760Z"
slug: "term-armada-event"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-papercut", "api-websocket-events"]
version: "1"
salience: "0.5"
---
Term: Event (src/Armada.Core/Models/ArmadaEvent.cs, prefix evt_). Append-only audit/timeline record. Fields: EventType (dotted string), EntityType, EntityId, CaptainId, MissionId, VesselId, VoyageId, Message (human text, not stable), Payload (JSON text), TenantId/UserId, CreatedUtc.

Emitted by ArmadaServer.EmitEventAsync, AdmiralService.EmitEventAsync, AgentLifecycleHandler, MissionLandingHandler, etc. Examples: captain.launched, captain.completed, captain.quarantined, captain.quarantine_lifted, mission.failed, mission.completed, mission.pull_request_open, mission.landing_failed, merge_queue.enqueued, mission.redispatched, papercut (captain-reported friction, see [[term-papercut]]), objective.snapshot, audit.command (commands run via workspace exec, fleet actions, check runs). The server resolves the event's tenant (ResolveEventTenantAsync) so WebSocket delivery is tenant-scoped. Events written only to the log (not WebSocket event types) are informational and outside the 1.0 compatibility promise. Old events pruned by dataRetentionDays (SQLite only). REST /api/v1/events (filters eventType/type, captainId, missionId, vesselId, voyageId); MCP enumerate includePayload flag.
