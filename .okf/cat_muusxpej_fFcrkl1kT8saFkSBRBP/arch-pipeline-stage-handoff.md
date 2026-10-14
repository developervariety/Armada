---
type: "Project"
title: "Pipeline stage handoff, branch hardening, and dangling-handoff re-drive"
description: "When a stage reaches WorkProduced, TryHandoffToNextStageAsync injects a \"Prior Stage Output\" section (persona, title, id, branch, diff) into each Pending dependent, gives it the same branch (force-advanced to the prior HEAD), and assigns it; failures cancel downstream stages; the health loop re-drives handoffs that dangled."
timestamp: "2026-10-05T05:40:23.6685350Z"
created: "2026-10-05T05:40:23.2776530Z"
slug: "arch-pipeline-stage-handoff"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-architect-fanout", "arch-review-gates", "term-pipeline", "arch-docks-and-bare-repos"]
version: "1"
salience: "0.5"
---
Code: MissionService.TryHandoffToNextStageAsync, HardenStageBranchAsync, RecoverDanglingHandoffsAsync, CancelDependentPipelineStagesAsync, UpdateVoyageTerminalStatusAsync, ApplyConditionalFeedbackToNextStagesAsync; docs/PIPELINES.md sections 6-8.

Chain: for each mission description, stage missions linked by DependsOnMissionId; only the first is assignable; TryAssignAsync refuses a dependent until its dependency is Complete/WorkProduced AND the handoff has set its branch.
Handoff: find Pending dependents in the same voyage; append markdown "## Prior Stage Output" (prior persona, title, mission id, branch, diff snapshot as a diff block) to each description; set the same BranchName; HardenStageBranchAsync resolves the completed dock's live HEAD (GetHeadCommitHashAsync) and lifts it onto the branch ref with ForceAdvanceBranchAsync (git update-ref, tolerant of detached/checked-out worktrees; best effort); then assign. A conditional review approval folds the reviewer's comment into the next stage(s). Failure of a stage cancels dependent stages (DependencyFailed) and updates the voyage's terminal status. RecoverDanglingHandoffsAsync (every tick) re-runs handoffs for WorkProduced missions whose downstream Pending stage was never prepared (v0.9 fix). Architect stages fan out instead ([[arch-architect-fanout]]).
