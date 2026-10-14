---
type: "Reference"
title: "Papercut: captain-reported friction stored as an event"
description: "A papercut is friction a captain reports via an [ARMADA:PAPERCUT] JSON line (category, severity, title, detail, path); stored as a papercut event with Admiral-supplied context and read back grouped by papercut_summary."
timestamp: "2026-10-05T05:33:06.4248660Z"
created: "2026-10-05T05:33:05.9823290Z"
slug: "term-papercut"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["rt-papercut-line", "term-armada-event"]
version: "1"
salience: "0.5"
---
Term: Papercut (src/Armada.Core/Models/Papercut.cs, PapercutPayload, PapercutGroup; PapercutParser, PapercutService). Friction a captain met instead of silently working around it: stale doc, dead link, self-contradicting brief, missing sibling repo, flaky test.

Payload from the captain: category (PapercutCategoryEnum BriefContradiction, ToolFailure, MissingDoc, BrokenLink, RepoFriction, TestFlake, EnvSetup, PlatformBug, Other), severity (PapercutSeverityEnum Low/Medium/High), title, detail, path. Context (mission, captain, vessel, voyage, runtime, timestamp) is added by the Admiral, never trusted from the captain. Stored as an ArmadaEvent of type "papercut" (AgentLifecycleHandler.HandlePapercutSignal; malformed lines are dropped without disturbing the mission).
MCP papercut_summary (e.g. sinceHours: 168) collapses reports into groups by vessel+category+problem with count and distinct-captain count. Routing guidance (README): MissingDoc/BrokenLink/RepoFriction/TestFlake -> backlog item on the vessel; EnvSetup -> dock/workflow-profile fix; BriefContradiction/PlatformBug -> Armada objective; promotion stays manual. Judge missions do not file papercuts. See [[rt-papercut-line]].
