---
type: "Reference"
title: "Mission mode: Implementation vs read-only Audit/Research"
description: "MissionModeEnum Implementation (expects a commit) vs Audit and Research (read-only: write a report, end with [ARMADA:RESULT] COMPLETE; an empty diff is success and nothing lands)."
timestamp: "2026-10-05T05:33:11.5523770Z"
created: "2026-10-05T05:33:11.2811940Z"
slug: "term-mission-mode"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-mission", "rt-result-line", "arch-mission-completion-pipeline"]
version: "1"
salience: "0.5"
---
Term: Mission mode (Mission.Mode, MissionModeEnum: Implementation, Audit, Research; helper src/Armada.Core/Services/MissionModeContract.cs).

Implementation (default) expects committed changes; an empty diff with a fast exit and trivial output is treated as a no-op false-complete and re-dispatched (MaxNoOpRedispatchAttempts, default 1) then failed (FailureKind NoOp).
Audit and Research are read-only (MissionModeContract.IsReadOnly): the brief tells the captain not to modify the repository and to deliver a written report ending with a standalone `[ARMADA:RESULT] COMPLETE` line. In HandleCompletionCoreAsync they skip no-op rejection and never attempt landing; unless held for review or feeding a pipeline stage they complete directly. Use for investigations, reviews, and reports that should not produce a branch.
