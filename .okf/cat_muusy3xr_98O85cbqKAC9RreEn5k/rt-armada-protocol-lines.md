---
type: "Reference"
title: "[ARMADA:*] protocol lines: the captain-to-Admiral output protocol"
description: "Captains report via standalone output lines `[ARMADA:TYPE] value` parsed by ProgressParser: PROGRESS, STATUS, MESSAGE, RESULT, VERDICT, PAPERCUT, TOKENS, plus Architect MISSION blocks; a marker counts only when it starts a physical line."
timestamp: "2026-10-05T05:36:32.8507060Z"
created: "2026-10-05T05:36:32.3831740Z"
slug: "rt-armada-protocol-lines"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-status-line", "rt-result-line", "rt-verdict-line", "rt-papercut-line", "rt-tokens-line", "rt-architect-plan-output"]
version: "1"
salience: "0.5"
---
Parser: src/Armada.Core/Services/ProgressParser.cs, regex ^\s*\[ARMADA:(\w+)\]\s+(.+?)\s*$ (case-insensitive), applied per physical line of each runtime output record (a Codex agent_message or Claude text block can hold many lines). Embedded mid-line markers are not signals (so docs that describe the format do not trigger). When one record holds several markers, a PAPERCUT wins (it must reach the store); result/status are re-detected from the mission log at exit.

Types: [ARMADA:PROGRESS] 75 (percentage 0-100, clamped); [ARMADA:STATUS] Testing (see [[rt-status-line]]); [ARMADA:MESSAGE] text (informational); [ARMADA:RESULT] COMPLETE (read-only modes, [[rt-result-line]]); [ARMADA:VERDICT] PASS|FAIL|NEEDS_REVISION (Judge, [[rt-verdict-line]]); [ARMADA:PAPERCUT] {json} ([[rt-papercut-line]]); [ARMADA:TOKENS] input=N output=N cached=N ([[rt-tokens-line]]); [ARMADA:MISSION] title ... [/ARMADA:MISSION] (legacy Architect fan-out, [[rt-architect-plan-output]]). AgentLifecycleHandler.HandleAgentOutput processes progress/papercut signals; HandleAgentStdout handles STATUS. Contracts asking for these lines are appended in code (JudgeContract, MissionModeContract, ArchitectPlanParser.FormatInstructions) so prompt-template edits cannot break parsing.
