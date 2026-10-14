---
type: "Reference"
title: "[ARMADA:VERDICT]: Judge verdict protocol and contract"
description: "Only one standalone `[ARMADA:VERDICT] PASS|FAIL|NEEDS_REVISION` line outside fenced code counts; missing or conflicting verdicts = no verdict = landing blocked; a PASS must be substantiated and a block must cite a concrete affected case."
timestamp: "2026-10-05T05:36:40.2637800Z"
created: "2026-10-05T05:36:39.9302390Z"
slug: "rt-verdict-line"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-armada-protocol-lines", "arch-mission-completion-pipeline", "decision-structured-judge-and-failure-kind"]
version: "1"
salience: "0.5"
---
Parser: src/Armada.Core/Services/JudgeVerdictParser.cs -> JudgeVerdictEnum (None, Pass, Fail, NeedsRevision). Regex ^\[ARMADA:VERDICT\][ \t]+(PASS|FAIL|NEEDS_REVISION)[ \t]*$ per trimmed line; lines inside ``` or ~~~ fences are skipped; prose ("Verdict: PASS", "### Verdict: **PASS**"), bare PASS/FAIL, inline mentions, and quoted examples never count. Repeated identical lines are fine; conflicting lines -> None (so an echoed line cannot override the Judge).

Contract: src/Armada.Core/Services/JudgeContract.cs appends to Judge prompts: end with a "## Verdict" section and exactly one standalone verdict line; a PASS must be substantiated (three review lenses plus real narrative); to block, the Judge must exhibit a concrete affected case. In MissionService.HandleCompletionCoreAsync a None verdict blocks landing; a rejected (unsubstantiated) PASS is downgraded to blocking and the mission fails terminally with an explicit reason rather than a quiet re-dispatch; an unsubstantiated block still fails but says so. Judge missions do not file papercuts. Decision record: [[decision-structured-judge-and-failure-kind]].
