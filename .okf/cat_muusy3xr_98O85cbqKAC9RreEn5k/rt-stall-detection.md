---
type: "Reference"
title: "Stall detection, auto-recovery, and the max-runtime backstop"
description: "A captain is stalled when LastHeartbeatUtc (last agent OUTPUT, not liveness) is older than StallThresholdMinutes (10); recovery kills and relaunches up to MaxRecoveryAttempts (3), then fails the mission StallRecoveryExhausted; MaxMissionRuntimeMinutes (240) force-fails runaways."
timestamp: "2026-10-05T05:36:17.3644610Z"
created: "2026-10-05T05:36:16.6786560Z"
slug: "rt-stall-detection"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["arch-admiral-health-check-loop", "rt-quarantine-and-crash-loop", "rt-interrupted-exit-redispatch"]
version: "1"
salience: "0.5"
---
Code: AdmiralService.HealthCheckCaptainAsync; AgentLifecycleHandler.HandleAgentHeartbeat and StartProcessLivenessHeartbeat.

Two clocks: Captain.LastHeartbeatUtc advances only when the agent produces output (HandleAgentHeartbeat on OnOutputReceived); Captain.LastProcessAliveUtc is refreshed by a periodic liveness loop and never touches LastHeartbeatUtc, so a live-but-silent agent is still caught (v0.9 fix). Per working captain each tick: if the mission is freshly WorkProduced (< 2 min) skip (handoff/landing in progress); terminal/post-work mission or no mission+no process -> release to Idle; dead process -> exit 0 = completion, negative = interruption redispatch, positive = terminal failure via RuntimeFailureClassifier.Decide(exitCode, null); alive -> (1) if MaxMissionRuntimeMinutes (default 240, 0 disables) exceeded since StartedUtc: stop, fail with MaxRuntimeExceeded, reclaim dock, release captain; (2) if now - LastHeartbeatUtc > StallThresholdMinutes (default 10): metric CaptainStalls; if RecoveryAttempts < MaxRecoveryAttempts (default 3) stop the process and CaptainService.TryRecoverAsync (repair worktree, relaunch); else fail mission (StallRecoveryExhausted), stop, reclaim dock, release captain to Idle (not Stalled). Startup: CleanupStaleCaptainsAsync verifies PID identity (a recycled PID started after launch is not ours) and resets dead Working captains, returning their missions to Pending while keeping DockId/BranchName. Escalation rule trigger CaptainStalled can log or call a webhook.
