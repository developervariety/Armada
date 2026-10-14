---
type: "Reference"
title: "Planning session (psn_): repo-aware chat with a captain before dispatch"
description: "PlanningSession (psn_) reserves a captain (state Planning) and a dock on a vessel; transcript-backed turns; reply can be summarized into a dispatch draft or dispatched directly; SQLite only."
timestamp: "2026-10-05T05:30:54.6586730Z"
created: "2026-10-05T05:30:54.1367550Z"
slug: "term-planning-session"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-planning-sessions", "term-refinement-session", "data-sqlite-only-features"]
version: "1"
salience: "0.5"
---
Term: Planning session (src/Armada.Core/Models/PlanningSession.cs prefix psn_; messages psm_; PlanningSessionStatusEnum Created, Active, Responding, Stopping, Stopped, Failed). A persistent planning conversation between a user and a captain on a vessel, run by Armada.Server/PlanningSessionCoordinator.

Behavior: reserves the chosen captain (CaptainStateEnum.Planning) and a dock/worktree for the vessel while active; the captain can read and modify the repo (tool-capable, not read-only). Each turn relaunches the runtime against the preserved transcript (no persistent stdin session). Supported runtimes: CLI runtimes (ClaudeCode, Codex, Gemini, Cursor, Mux, OpenCode); ApiEndpoint, Custom, and Harbor-hosted runtimes report SupportsPlanningSessions=false. A selected reply can be summarized into a server-owned dispatch draft (PlanningSummaryDraftDocument), opened in Dispatch, or dispatched directly (voyage records SourcePlanningSessionId/MessageId). Persistence exists only for SQLite; PostgreSQL/MySQL/SQL Server throw NotSupportedException (HTTP 501). Cleanup: PlanningSessionInactivityTimeoutMinutes (code default 60), PlanningSessionAbandonmentTimeoutMinutes (240), PlanningSessionRetentionDays (0 = keep). REST /api/v1/planning-sessions; MCP backlog planning tools. See [[arch-planning-sessions]].
