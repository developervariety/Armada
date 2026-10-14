---
type: "Reference"
title: "Captain quarantine: usage limits, auth failures, crash loops"
description: "After a terminal exit with UsageLimit or AuthFailure, or 3 Crash exits within 15 min, AdmiralService puts the captain in Quarantined for CaptainQuarantineMinutes (15) or until the provider's reset time; the health check lifts it."
timestamp: "2026-10-05T05:36:07.4369220Z"
created: "2026-10-05T05:36:07.1314020Z"
slug: "rt-quarantine-and-crash-loop"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-runtime-exit-info", "term-captain", "rt-stall-detection"]
version: "1"
salience: "0.5"
---
Code: AdmiralService.HandleProcessExitAsync, QuarantineCaptainAsync, RecordCrashAndCheckLoop, LiftExpiredQuarantinesAsync (src/Armada.Core/Services/AdmiralService.cs).

Rules: after failing the mission (HandleTerminalProcessExitFailureAsync), if exitInfo.FailureKind is UsageLimit or AuthFailure the captain is quarantined immediately (reason "provider usage limit" / "provider auth failure"); if Crash, an in-memory per-captain list of crash times is kept and when CaptainCrashLoopThreshold (default 3; 0 disables) crashes fall within CaptainCrashLoopWindowMinutes (default 15) it is quarantined with reason "crash loop" (counter resets on detection and on Admiral restart). Window: QuarantineUntilUtc = now + CaptainQuarantineMinutes (default 15) unless ProviderResetParser finds a valid provider reset time or Retry-After in the structured provider error (reason suffix "(provider reset time)"). Quarantine clears CurrentMissionId/DockId/ProcessId and emits captain.quarantined. Each health check first runs LiftExpiredQuarantinesAsync (-> Idle, event captain.quarantine_lifted). Manual: POST /api/v1/captains/{id}/unquarantine (Armada.Client UnquarantineReply). Quarantined captains are skipped by assignment and tier selection.
