---
type: "Reference"
title: "Architect output: armada-plan JSON block, legacy [ARMADA:MISSION] markers"
description: "Architects should emit one ```armada-plan fenced JSON {\"missions\":[{title, description, dependsOn, waitForOtherMissions}]}; the last valid block wins; fallback is legacy `[ARMADA:MISSION] title ... [/ARMADA:MISSION]` blocks; no RESULT or VERDICT lines."
timestamp: "2026-10-05T05:36:55.1786910Z"
created: "2026-10-05T05:36:52.9150070Z"
slug: "rt-architect-plan-output"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["arch-architect-fanout", "rt-armada-protocol-lines"]
version: "1"
salience: "0.5"
---
Parser: src/Armada.Core/Services/ArchitectPlanParser.cs (FenceInfo = "armada-plan", models ArchitectPlan / ArchitectPlanMission). TryParse deserializes each fenced armada-plan block (comments and trailing commas allowed); the last well-formed plan wins; missions without a title, with a placeholder like "<title>", or with repeated titles are dropped; a dependsOn that does not name an earlier kept mission (1-based number) is cleared; waitForOtherMissions maps to Mission.WaitForVoyageWorkers (defer until other voyage workers settle). FormatInstructions() is the one-sentence contract appended to Architect prompts.

Fallback: MissionService parses legacy [ARMADA:MISSION] <title> segments (closing tag [/ARMADA:MISSION], optional "Depends on:" line) from the architect's output. If neither yields missions, the Architect fails with "Architect produced no valid [ARMADA:MISSION] definitions in output" (or "no ... markers"). Architect missions must not emit [ARMADA:RESULT] or [ARMADA:VERDICT]. Parsed missions are also projected into the mission log. Fan-out behavior: [[arch-architect-fanout]].
