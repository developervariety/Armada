---
type: "Project"
title: "Mission assignment: MissionService.TryAssignAsync gates and steps"
description: "TryAssignAsync checks dependency/handoff readiness, vessel serialization (AllowConcurrentMissions), resolves preferred captain and tier, applies the RequireHarborForLaunch gate, provisions the dock, atomically claims the captain, writes instructions, launches; every failure rolls back to Pending/Idle."
timestamp: "2026-10-05T05:39:08.1865760Z"
created: "2026-10-05T05:39:07.5982950Z"
slug: "arch-mission-assignment"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-captain-routing-persona-tier", "arch-mission-scheduling", "arch-docks-and-bare-repos", "arch-prompt-and-instructions-file"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/MissionService.cs TryAssignAsync (called inline at dispatch and by DispatchPendingMissionsAsync each tick):
1. Dependency gate: if DependsOnMissionId is set, the dependency must be Complete or WorkProduced AND the downstream handoff must already have handed this mission the same branch (prevents a stage starting with the raw dispatch prompt).
2. Architect sequencing deferral (ShouldDeferArchitectSequencedMissionAsync / WaitForVoyageWorkers).
3. Vessel lock: broad-scope missions block new assignments; unless Vessel.AllowConcurrentMissions, only one truly active mission (Assigned/InProgress/Testing/Review; WorkProduced and PullRequestOpen do not count) per vessel.
4. ResolvePreferredCaptainAsync then FindAvailableCaptainAsync ([[arch-captain-routing-persona-tier]]).
5. CanAssignMissionAsync hook: with RequireHarborForLaunch, defer until an eligible Harbor owned by the mission's user is connected.
6. Branch: reuse an existing BranchName (downstream stages, review rework, resumed missions) else armada/<captain>/<missionId>; DockService.ProvisionAsync (per-repo semaphore, bare clone ensure, worktree add).
7. Atomic captain claim (TryClaim; only succeeds if still Idle) else reclaim the dock and give up; Assignment signal; write instructions file (CLAUDE.md/CODEX.md/...); OnLaunchAgent.
8. On launch failure: release captain to Idle, mission back to Pending. A fast in-process captain may already have moved the mission on, so records are re-read before writing InProgress. Priority/FIFO ordering is described in [[arch-mission-scheduling]].
