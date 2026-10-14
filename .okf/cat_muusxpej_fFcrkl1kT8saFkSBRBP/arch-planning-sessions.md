---
type: "Project"
title: "PlanningSessionCoordinator: reservation, turns, draft, dispatch"
description: "PlanningSessionCoordinator reserves captain (Planning) + dock for a vessel, runs each turn by relaunching the runtime against the stored transcript, streams/broadcasts replies, summarizes a reply into a dispatch draft, dispatches a voyage linked back to the session, and recovers/expires sessions."
timestamp: "2026-10-05T05:42:22.2351240Z"
created: "2026-10-05T05:42:21.4268190Z"
slug: "arch-planning-sessions"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-planning-session", "data-sqlite-only-features", "arch-backlog-refinement-sessions"]
version: "1"
salience: "0.5"
---
src/Armada.Server/PlanningSessionCoordinator.cs (constructed in ArmadaServer with DockService, AdmiralService, AgentRuntimeFactory, EmitEventAsync, WebSocket hub).
- CreateAsync: validates captain/vessel (404 when missing), requires a runtime with SupportsPlanningSessions (CLI runtimes; ApiEndpoint/Custom/Harbor-hosted rejected -> 501), reserves the captain (CaptainStateEnum.Planning) and provisions a dock for the vessel, persists PlanningSession (psn_) and messages (psm_).
- Turns: transcript-backed; each user message relaunches the runtime in the dock with the preserved transcript plus repo context; options ShowThinking and Stream (incremental vs once at end); Claude Code uses stream-json for token deltas; Stop cancels; per-turn metrics (time to first token, tokens/sec, counts; ChatTurnMetricsBuilder) and token usage recorded.
- Summarize a selected reply into a server-owned dispatch draft (PlanningSummaryDraftDocument) (planning-session.summary.created); open in Dispatch; or dispatch directly (planning-session.dispatch.created) creating a voyage with SourcePlanningSessionId/SourcePlanningMessageId; backlog items link the session.
- Lifecycle: RecoverSessionsAsync at startup; MaintainSessionsAsync at startup and every 10 health cycles (inactivity timeout PlanningSessionInactivityTimeoutMinutes, abandonment timeout 240 min for sessions with no running process, retention PlanningSessionRetentionDays); stopping releases captain and dock.
Persistence is SQLite-only ([[data-sqlite-only-features]]). REST /api/v1/planning-sessions; MCP create/get/dispatch_backlog_planning_session.
