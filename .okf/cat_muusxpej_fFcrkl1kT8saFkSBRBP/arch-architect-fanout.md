---
type: "Project"
title: "Architect fan-out: one plan becomes N worker chains"
description: "When an Architect stage completes with a parsed plan, the next Worker mission takes definition 1, N-1 extra Worker missions are created (each cloning the post-Worker stages), dependencies are applied, worker branches are fresh, and normal handoff is skipped."
timestamp: "2026-10-05T05:40:31.7538160Z"
created: "2026-10-05T05:40:30.5687990Z"
slug: "arch-architect-fanout"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["rt-architect-plan-output", "arch-pipeline-stage-handoff", "term-pipeline"]
version: "1"
salience: "0.5"
---
Code: MissionService.TryHandoffToNextStageAsync (architect branch), CloneDependentChainAsync, RetitleDependentChainAsync, ApplyArchitectMissionDependenciesAsync, CleanupArchitectBranchAsync, ProjectArchitectMissionsToLogAsync; parser [[rt-architect-plan-output]].

Steps: parse the Architect output (armada-plan JSON preferred, legacy [ARMADA:MISSION] fallback); update the existing next-stage Worker mission with the first definition; for each remaining definition create an additional Worker mission depending on the Architect and clone the post-Worker stages (e.g. Usability Engineer, Test Engineer, Linter, Judge, Recorder) chained to it; apply plan dependsOn ordering and waitForOtherMissions (WaitForVoyageWorkers); fan-out workers inherit the persona's preferred captain; start them on fresh branches (stage-branch hardening is not used here); clean up the architect's own branch; assign the first worker; skip normal context handoff. If the Architect produced no valid definitions the mission fails ("Architect produced no valid [ARMADA:MISSION] definitions in output"). Result for 2 definitions in FullPipeline: [Architect] -> Worker1 -> ... -> Judge1 and Worker2 -> ... -> Judge2 in the same voyage.
