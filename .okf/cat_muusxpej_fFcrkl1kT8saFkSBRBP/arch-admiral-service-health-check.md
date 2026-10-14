---
type: "Project"
title: "AdmiralService.HealthCheckAsync: per-tick orchestration steps"
description: "Each tick: lift expired quarantines, check every Working captain in parallel, recover orphaned InProgress missions, re-drive dangling handoffs, detect voyage completion, reconcile PRs, release overdue reviews, reclaim orphan docks, dispatch pending missions, maintain captain pool, evaluate escalations."
timestamp: "2026-10-05T05:38:48.2835700Z"
created: "2026-10-05T05:38:47.5433860Z"
slug: "arch-admiral-service-health-check"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-admiral-health-check-loop", "rt-stall-detection", "arch-mission-assignment", "arch-review-gates"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/AdmiralService.cs HealthCheckAsync, in order:
1. LiftExpiredQuarantinesAsync (Quarantined -> Idle when QuarantineUntilUtc passed).
2. HealthCheckCaptainAsync for every Working captain, in parallel Task.Run with per-captain exception isolation (process exit, max runtime, stall; see [[rt-stall-detection]]).
3. RecoverOrphanedMissionsAsync: InProgress missions whose captain moved on.
4. MissionService.RecoverDanglingHandoffsAsync: WorkProduced missions whose downstream Pending stage was never prepared (metric HandoffsRedriven).
5. VoyageService.CheckCompletionsAsync -> OnVoyageComplete (MissionLandingHandler.HandleVoyageCompleteAsync).
6. ReconcilePullRequestMissionsAsync: PullRequestOpen missions -> OnReconcilePullRequest checks gh for merged PRs -> Complete.
7. RecoverOverdueReviewsAsync: missions in Review past ReviewDeadlineUtc (ReviewTimeoutMinutes, default 1440) release their captain; mission stays in Review, dock preserved; fires once.
8. ReclaimOrphanedDocksAsync: active docks older than 5 min with no working captain.
9. DispatchPendingMissionsAsync: priority order, ResourceAdmission memory floor (MinAvailableMemoryBytesForLaunch, default 0 = off), MaxConcurrentMissions ceiling (0 = unlimited), MissionService.TryAssignAsync.
10. MaintainCaptainPoolAsync when MinIdleCaptains > 0.
11. EscalationService.EvaluateAsync (EscalationRule: Trigger CaptainStalled/MissionOverdue/MissionFailed/RecoveryExhausted/PoolExhausted, ThresholdMinutes, Action Log/Webhook, CooldownMinutes).
