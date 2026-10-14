---
type: "Reference"
title: "Voyage: a batch of related missions (vyg_)"
description: "Voyage (vyg_) groups missions dispatched together; statuses Open, InProgress, Complete, Failed, Cancelled; carries per-voyage landing overrides and captain overrides."
timestamp: "2026-10-05T05:29:08.1574850Z"
created: "2026-10-05T05:29:07.8895590Z"
slug: "term-voyage"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-mission", "term-landing-mode", "arch-pipeline-stage-handoff"]
version: "1"
salience: "0.5"
---
Term: Voyage (src/Armada.Core/Models/Voyage.cs, prefix vyg_). A batch of related missions dispatched together; the dispatch prompt becomes the voyage title. VoyageStatusEnum: Open, InProgress (set once any mission is assigned), Complete, Failed, Cancelled. Completion is detected by VoyageService.CheckCompletionsAsync in the health check, which fires OnVoyageComplete.

Fields: Description, AutoPush/AutoCreatePullRequests/AutoMergePullRequests (legacy per-voyage booleans), LandingMode (highest priority in landing resolution), SourcePlanningSessionId/SourcePlanningMessageId (when dispatched from planning), SelectedPlaybooks, CaptainOverridesJson (per-persona captain dictates from the dispatch captainAssignments array).
Each `--task` of `armada go` becomes its own mission in one voyage; without --task the prompt is one mission. With a multi-stage pipeline every mission description gets its own chain of stage missions in the same voyage. Example: `armada voyage create "Pre-review cleanup" --vessel my-api --mission "..." --mission "..."`; MCP `dispatch`, `voyage_status`, `cancel_voyage`.
