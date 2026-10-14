---
type: "Reference"
title: "Backlog refinement session (ors_): captain chat that shapes a backlog item"
description: "ObjectiveRefinementSession (ors_, messages orm_) = captain-backed chat on a backlog item without a dock; explicit captain selection; summary can be applied back onto the objective."
timestamp: "2026-10-05T05:31:02.1165020Z"
created: "2026-10-05T05:31:00.6912490Z"
slug: "term-refinement-session"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-objective-backlog-item", "term-planning-session", "arch-backlog-refinement-sessions"]
version: "1"
salience: "0.5"
---
Term: Refinement session (src/Armada.Core/Models/ObjectiveRefinementSession.cs prefix ors_; ObjectiveRefinementMessage orm_; ObjectiveRefinementSessionStatusEnum). Run by Armada.Server/ObjectiveRefinementCoordinator; the captain is reserved in CaptainStateEnum.Refining.

Differs from planning: refinement requires explicit captain selection, does not provision a dock by default, can run without a vessel, and is for shaping intent (acceptance criteria, scope); planning is repository-aware and needs a vessel. Flow: create session -> send messages -> summarize (ObjectiveRefinementSummaryDocument) -> apply summary back to the objective -> stop/delete. Sessions are recovered and maintained at startup and every 10 health cycles. REST /api/v1/backlog/{id}/refinement-sessions and /api/v1/objective-refinement-sessions/{id}/(messages|summarize|apply|stop); MCP create/send/summarize/apply/stop_backlog_refinement_*; WebSocket objective-refinement-session.* events. Mutations require tenant admin.
