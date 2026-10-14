---
type: "Project"
title: "End-to-end flow: dispatch to landing"
description: "dispatch -> AdmiralService.DispatchVoyageAsync (resolve pipeline, create voyage + mission chains) -> TryAssignAsync (captain, dock, CLAUDE.md, launch) -> agent runs, output parsed -> process exit -> HandleCompletionAsync (diff, gates, handoff, review) -> MissionLandingHandler (LocalMerge/PR/MergeQueue/None) -> reclaim dock, release captain."
timestamp: "2026-10-05T05:38:59.1493460Z"
created: "2026-10-05T05:38:58.3433740Z"
slug: "arch-dispatch-to-landing-flow"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-mission-assignment", "arch-mission-completion-pipeline", "arch-landing-handler", "arch-pipeline-resolution", "arch-agent-lifecycle-handler"]
version: "1"
salience: "0.5"
---
Entry points: CLI `armada go`/`voyage create`, REST POST /api/v1/voyages or /missions, MCP dispatch/create_mission, planning-session dispatch, Ask proposals, fleet Mission actions.
1. AdmiralService.ValidateDispatchAsync, then DispatchVoyageAsync: verify vessel, ResolvePipelineAsync (explicit > vessel > fleet > WorkerOnly). Single Worker stage -> standard path; multi-stage -> per description a chain of stage missions "<title> [<Persona>]" with DependsOnMissionId; persist voyage captain overrides first; persist playbook selections; only the first stage is assigned immediately; voyage -> InProgress if anything was assigned.
2. MissionService.TryAssignAsync ([[arch-mission-assignment]]): dependency gate, vessel serialization, preferred captain/tier, Harbor policy, provision dock (git worktree on armada/<captain>/<mission>), atomic captain claim, Assignment signal, write the runtime instructions file, OnLaunchAgent.
3. AgentLifecycleHandler.HandleLaunchAgentAsync: choose local vs Harbor executor, build prompt (MissionPromptBuilder + commit instructions), runtime.StartAsync, map PID -> captain/mission, liveness heartbeat, captain.launched event, WebSocket broadcast.
4. While running: output -> heartbeat, progress/papercut signals, STATUS toggles; health loop watches stall/runtime.
5. Exit: AdmiralService.HandleProcessExitAsync -> clean exit -> MissionService.HandleCompletionAsync ([[arch-mission-completion-pipeline]]): WorkProduced, diff capture, gates (no-op, boundary scan, judge verdict, DoD, auto-land), pipeline handoff or review hold.
6. OnMissionComplete -> MissionLandingHandler.HandleMissionCompleteAsync ([[arch-landing-handler]]) -> Complete / PullRequestOpen / LandingFailed / stays WorkProduced (MergeQueue, None).
7. Reclaim dock, release captain to Idle (only if still on this mission), dispatch next pending. Voyage completion detected in the health loop.
