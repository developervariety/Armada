---
type: "Project"
title: "Mission state machine: the single transition table"
description: "MissionStateMachine.IsValidTransition is the one authoritative table: Pending->Assigned/Cancelled; Assigned->InProgress/Cancelled; InProgress->WorkProduced/Testing/Review/Complete/Failed/Cancelled; WorkProduced->PullRequestOpen/Complete/LandingFailed/Cancelled; LandingFailed->WorkProduced/Failed/Cancelled."
timestamp: "2026-10-05T05:39:31.3187990Z"
created: "2026-10-05T05:39:31.0749150Z"
slug: "arch-mission-state-machine"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-mission", "rt-status-line"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/MissionStateMachine.cs (static, centralized in v0.9). Transitions:
- Pending -> Assigned, Cancelled
- Assigned -> InProgress, Cancelled
- InProgress -> WorkProduced, Testing, Review, Complete, Failed, Cancelled
- WorkProduced -> PullRequestOpen, Complete, LandingFailed, Cancelled
- PullRequestOpen -> Complete, LandingFailed, Cancelled
- Testing -> Review, InProgress, Complete, Failed
- Review -> Complete, InProgress, Failed
- LandingFailed -> WorkProduced (retry landing), Failed, Cancelled
- Complete, Failed, Cancelled: terminal.
Helpers: IsAgentReportableTransition (only InProgress<->Testing from [ARMADA:STATUS]); IsTerminal; IsTerminalOrPostWork (terminal + WorkProduced, PullRequestOpen, LandingFailed: agent finished; used to release captains); IsActive (Assigned, InProgress, Review, Testing). Manual transitions go through REST PUT /api/v1/missions/{id}/status or MCP transition_mission_status, validated against this table. Internal flows (review deny back to Pending, recovery resets) write statuses directly where the business rule requires.
