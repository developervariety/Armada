---
type: "Reference"
title: "Negative exit codes are interruptions and get re-dispatched"
description: "An agent exit code < 0 (stop/shutdown cancellation maps to -1; a vanished PID reports -1) re-dispatches the mission (bounded by MaxNoOpRedispatchAttempts, default 1) instead of failing the voyage; positive codes are genuine failures."
timestamp: "2026-10-05T05:36:23.7838620Z"
created: "2026-10-05T05:36:23.5336650Z"
slug: "rt-interrupted-exit-redispatch"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-runtime-exit-info", "rt-stall-detection"]
version: "1"
salience: "0.5"
---
Code: AdmiralService.HandleProcessExitAsync and HealthCheckCaptainAsync -> HandleInterruptedProcessExitAsync.

Rule: the runtime maps OperationCanceledException from a stop or shutdown to exit code -1, and the health monitor uses -1 for a PID that no longer exists. Those are interruptions (server or Harbor restart, link reconnect), not agent failures. If the mission is not Cancelled and Mission.RedispatchAttempts < settings.MaxNoOpRedispatchAttempts (default 1), the mission is re-dispatched (redispatch counter increments, event mission.redispatched) and pending missions are dispatched again; once the budget is exhausted the failure is terminal. Real agent/model/config failures exit with a positive code and go through RuntimeExitInfo classification. A fast in-process captain (ApiEndpoint) can exit before the launch recorded InProgress; the handler waits briefly for the launch record to avoid leaving a mission stuck. Also, if the PID is gone but OnIsProcessExitHandled says the exit callback already fired, the health check skips to avoid racing the completion handler.
