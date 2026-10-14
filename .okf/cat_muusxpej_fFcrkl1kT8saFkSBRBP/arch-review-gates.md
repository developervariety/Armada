---
type: "Project"
title: "Human review gates: Review status, approve/deny, overdue watchdog"
description: "A pipeline stage with RequiresReview (or an auto-land hold) parks the mission in Review with a deadline (ReviewTimeoutMinutes 1440); approve (optionally conditional) lets it proceed; deny either fails the pipeline (ReviewDenied) or sends the mission back to Pending with feedback (RetryStage)."
timestamp: "2026-10-05T05:40:47.3398230Z"
created: "2026-10-05T05:40:46.8470190Z"
slug: "arch-review-gates"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-mission-completion-pipeline", "term-inbox-item", "arch-definition-of-done-and-autoland", "rt-verdict-line"]
version: "1"
salience: "0.5"
---
Code: MissionService.HandleCompletionCoreAsync (hold), ApproveReviewAsync, DenyReviewAsync, AdmiralService.RecoverOverdueReviewsAsync; PipelineStage.RequiresReview / ReviewDenyAction (ReviewDenyActionEnum RetryStage | FailPipeline); built-in pipelines set RequiresReview on every stage except Recorder, FailPipeline on Judge.

Hold: when a WorkProduced mission RequiresReview (and did not fail scope validation) it moves to Review with ReviewRequestedUtc and ReviewDeadlineUtc = now + ReviewTimeoutMinutes (0 = none); MissionService.OnReviewRequested -> ArmadaWebSocketHub.BroadcastApprovalNeeded (WebSocket approval-needed); it shows in the inbox (kind review). Auto-land holds use the same Review state with ReviewComment "Auto-land held: <reason>".
Approve: POST /api/v1/missions/{id}/review/approve (comment, conditional). Conditional approval folds the reviewer's guidance into the freshly prepared next stage(s).
Deny: POST .../review/deny with optional action override ("More Work Required" vs "Deny"): FailPipeline -> mission Failed (FailureKind ReviewDenied), dock reclaimed, dependent stages cancelled, voyage status updated; RetryStage -> description gets the feedback, run fields cleared, status Pending, immediate TryAssignAsync on the same branch.
Overdue: RecoverOverdueReviewsAsync releases the captain once the deadline passes (metric armada_reviews_overdue_total); the mission stays in Review and its dock is preserved for the reviewer. This human gate is separate from the Judge persona's [ARMADA:VERDICT].
