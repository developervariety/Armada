---
type: "Project"
title: "FleetActionRunner: executing Command runs and pacing Mission runs"
description: "One server-owned FleetActionRunner: Command targets run the rendered shell via stdin (/bin/sh -s or PowerShell -Command -) in each WorkingDirectory, capped by run concurrency and FleetActions.MaxConcurrency (8); Mission targets dispatch one voyage per vessel, paced by run concurrency from the health loop."
timestamp: "2026-10-05T05:42:39.4824860Z"
created: "2026-10-05T05:42:38.6159710Z"
slug: "arch-fleet-actions-runner"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-fleet-action", "arch-harbor-split-mode", "arch-admiral-health-check-loop"]
version: "1"
salience: "0.5"
---
Files: src/Armada.Core/Services/FleetActionRunner.cs, FleetActionService.cs, FleetActionSeedService.cs, FleetActionTemplateRenderer.cs, FleetActionShellCommandBuilder.cs, AdmiralFleetActionMissionDispatcher.cs, FleetActionHealthSummaryBuilder.cs; docs/FLEET_ACTIONS.md.

Startup (ArmadaServer): seed five built-ins into every tenant (deleted built-ins stay deleted via key), then StartAsync with restart recovery (Running Command targets -> Failed reason Interrupted; pending resume). Run creation re-reads every target vessel within the caller's tenant (one foreign vessel rejects the request). Command targets: optional clean-tree check (git status --porcelain -> Skipped DirtyTree); render {{...}} variables (single pass, no escaping; NoBuildCommand skip); execute through the platform shell on stdin (/bin/sh -s; Windows powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -); a vessel with PreferredHarborId runs on that Harbor as a single argument (sh -c / -Command) or Skipped HarborUnavailable; timeout kills the process tree (TimedOut); stdout/stderr tails capped at MaxOutputBytes; audit.command event. Mission targets: AdmiralFleetActionMissionDispatcher dispatches a voyage through the same path as POST /api/v1/voyages (validation failure -> Skipped DispatchRejected); SyncMissionRunsAsync each tick updates targets from voyages (Succeeded/Failed VoyageFailed) and dispatches pending targets while fewer than Concurrency voyages are active. Cancel: pending -> Cancelled, kill running commands, cancel unfinished voyages (409 on finished runs). Run status rolls up; retention FleetActions.RunRetentionDays (30). Metrics armada_fleet_action_targets_total{kind,outcome}, armada_fleet_action_target_duration_seconds{kind}.
