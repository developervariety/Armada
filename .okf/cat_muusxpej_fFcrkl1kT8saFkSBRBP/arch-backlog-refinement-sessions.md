---
type: "Project"
title: "ObjectiveRefinementCoordinator: backlog refinement chat without a dock"
description: "ObjectiveRefinementCoordinator reserves the selected captain (Refining), runs refinement turns without provisioning a dock, broadcasts objective-refinement-session.* events, summarizes into ObjectiveRefinementSummaryDocument, applies the summary onto the objective, and recovers/maintains sessions."
timestamp: "2026-10-05T05:42:29.6880730Z"
created: "2026-10-05T05:42:28.8129810Z"
slug: "arch-backlog-refinement-sessions"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-refinement-session", "term-objective-backlog-item", "arch-planning-sessions"]
version: "1"
salience: "0.5"
---
src/Armada.Server/ObjectiveRefinementCoordinator.cs (ArmadaServer wires it with DB, settings, AgentRuntimeFactory, EmitEventAsync, WebSocket hub); summary model ObjectiveRefinementSummaryDocument.cs. Flow: create session for a backlog item with an explicit captain (captain -> CaptainStateEnum.Refining; vessel optional; no dock/worktree by default) -> send messages (captain runtime turn, transcript persisted as orm_ rows; WebSocket objective-refinement-session.message.created/updated) -> summarize (objective-refinement-session.summary.created) -> apply back onto the Objective (acceptance criteria, scope fields; objective-refinement-session.applied) -> stop/delete (releases captain). RecoverSessionsAsync and MaintainSessionsAsync run at startup and every 10 health cycles. Unlike planning, refinement persistence is implemented for all providers (tables from migration 43). Routes: /api/v1/backlog/{id}/refinement-sessions, /api/v1/objective-refinement-sessions/{id}/...; MCP *_backlog_refinement_* tools. Mutations need tenant admin.
