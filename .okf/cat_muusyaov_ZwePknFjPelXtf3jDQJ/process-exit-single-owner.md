---
type: "Project"
title: "Each captain process exit has exactly one owner; mission status writes are conditional"
description: "Health check treats server-tracked processes as alive; exit handlers claim via TryClaimProcessExitAsync; status writes via TryUpdateIfStatusAsync."
timestamp: "2026-10-05T11:44:06.0503250Z"
created: "2026-10-05T11:44:05.7522270Z"
slug: "process-exit-single-owner"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
version: "1"
salience: "0.5"
---
Root cause of a 2026-10-05 install-verify docker failure (mission stuck InProgress): the AdmiralService health check asked the OS about ApiEndpoint captains' synthetic process ids (>= 2,000,000,000), concluded the process was gone (exit -1, treated as interruption and re-dispatched) while the real exit handler also ran completion; a mission-id "completion in flight" guard (30 s) then swallowed attempt 2's completion. Fix (merge ecf812a8): AgentLifecycleHandler.IsProcessTracked wired as AdmiralService.OnIsProcessTracked so tracked processes are alive to the health/orphan checks; every exit handler first calls IMissionMethods.TryClaimProcessExitAsync (clears the recorded process id only if it still matches); completion/interruption/failure/dock reclaim/launch use IMissionMethods.TryUpdateIfStatusAsync on all four providers; the mission-id guard was removed; ApiAgentRuntime returns -1 only for a real stop (timeouts are failures, exit 1). Known follow-ups: a Harbor disconnect does not report exits (HarborConnectionManager.OnDisconnectedAsync), so those missions recover via stall detection; the lifecycle liveness heartbeat still asks the OS (telemetry only).
