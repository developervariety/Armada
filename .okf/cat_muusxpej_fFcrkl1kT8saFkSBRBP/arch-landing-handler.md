---
type: "Project"
title: "MissionLandingHandler: diff capture, merge/PR/enqueue, PR reconciliation"
description: "src/Armada.Server/MissionLandingHandler.cs resolves landing mode, takes a per-vessel merge lock, then LocalMerge (fetch from bare repo, merge into WorkingDirectory, push, cleanup), PullRequest (push, gh PR, PullRequestOpen, optional auto-merge poll) or MergeQueue (enqueue); broadcasts by resulting status."
timestamp: "2026-10-05T05:39:52.0241160Z"
created: "2026-10-05T05:39:50.8848440Z"
slug: "arch-landing-handler"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-landing-mode", "arch-merge-queue-service", "term-message-templates", "arch-mission-completion-pipeline"]
version: "1"
salience: "0.5"
---
Wired in ArmadaServer: Admiral.OnCaptureDiff = HandleCaptureDiffAsync; OnMissionComplete = HandleMissionCompleteAsync (also LandingService.OnPerformLanding for retries); OnVoyageComplete = HandleVoyageCompleteAsync; OnReconcilePullRequest = HandleReconcilePullRequestAsync.

HandleCaptureDiffAsync: persists the diff to the mission row (and a file for compatibility) and the HEAD commit hash before the worktree is reclaimed.
HandleMissionCompleteAsync: load vessel/voyage; landing mode voyage > vessel > global > legacy booleans; branch cleanup vessel > global; per-vessel SemaphoreSlim so merges on one repo never overlap.
- PullRequest path: push, create PR (PrDescriptionTemplate appended), mission -> PullRequestOpen, event mission.pull_request_open; with AutoMergePullRequests enable auto-merge and PollAndPullAfterMergeAsync (30 x 10 s using the vessel WorkingDirectory as gh context) -> Complete + branch cleanup.
- LocalMerge/legacy: if no changes (empty DiffSnapshot and no branch in bare repo) -> Complete and delete the leftover branch; else fetch the captain branch from the bare repo into WorkingDirectory, merge with MergeCommitTemplate, push BEFORE cleanup; on failure keep the branch for retry -> LandingFailed. Explicit None/MergeQueue never merge into the working directory.
- MergeQueue: enqueue MergeEntry (event merge_queue.enqueued); mission stays WorkProduced.
Broadcast mission change derived from final status. Dock reclaim/captain release are owned by MissionService, not here. HandleReconcilePullRequestAsync (health loop) detects merged PRs and pulls latest into the working directory. Retries: LandingService (MaxLandingRetries 3); LandingPreviewService predicts blockers (protected branches, required checks, release/hotfix prefixes).
