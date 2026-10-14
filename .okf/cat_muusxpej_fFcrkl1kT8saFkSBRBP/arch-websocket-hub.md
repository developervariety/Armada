---
type: "Project"
title: "ArmadaWebSocketHub: authenticated, tenant-scoped real-time delivery"
description: "The /ws hub on the REST port authenticates every upgrade in PreRouting (headers, ?token=, or armada-token.<b64> subprotocol), keeps per-socket identity, broadcasts entity events to the entity's tenant (admins may opt into all tenants), sends ask.* only to the owner, and authorizes commands (global admin only)."
timestamp: "2026-10-05T05:43:15.9523370Z"
created: "2026-10-05T05:43:15.5034730Z"
slug: "arch-websocket-hub"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["api-websocket-connection", "api-websocket-events", "api-websocket-commands", "arch-ask-armada-flow"]
version: "1"
salience: "0.5"
---
Files: src/Armada.Server/WebSocket/ArmadaWebSocketHub.cs (AuthorizeUpgradeAsync, HandleWebSocketAsync, BroadcastToTenant, SendToUser, Broadcast*Change helpers, EntityChanged event), WebSocketCommandHandler.cs (IsAuthorized; command actions), WebSocketClientState.cs, WebSocketCredentials.cs, WebSocketSurface.cs (declared contract used by the API surface generator and E2E.ApiContract).

Auth: ArmadaServer PreRouting intercepts /ws upgrades before Watson completes the handshake and returns 401 if unauthenticated (thread-scoped Ask tokens refused). Delivery: entity changes (mission/voyage/captain/check-run/objective/deployment/incident/runbook-execution/approval-needed/planning and refinement events/generic events) go to sockets of the entity's tenant; events whose tenant cannot be resolved go to global admins only; global admins can subscribe with AllTenants: true; ask.* go only to the thread owner's sockets. Producers: services expose callbacks wired at startup (MissionService.OnReviewRequested -> BroadcastApprovalNeeded, CheckRunService.OnCheckRunChanged, ObjectiveService, DeploymentService, IncidentService, RunbookService, AgentLifecycleHandler, MissionLandingHandler, AskThreadService.OnUserEvent). AskWorkTracker subscribes to EntityChanged. Commands operate across tenants by id and therefore require a global administrator. The hub was hardened in Ask Phase 0 (previously unauthenticated broadcast-to-all). Through Armada.Proxy the query string and subprotocol header are forwarded to the local upgrade.
