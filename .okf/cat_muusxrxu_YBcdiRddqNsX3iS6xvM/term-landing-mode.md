---
type: "Reference"
title: "Landing modes and branch cleanup policy"
description: "LandingModeEnum LocalMerge, PullRequest, MergeQueue, None decides how WorkProduced work integrates; resolved voyage > vessel > global > legacy AutoPush/AutoCreatePullRequests booleans."
timestamp: "2026-10-05T05:30:12.2901990Z"
created: "2026-10-05T05:30:11.6815420Z"
slug: "term-landing-mode"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-landing-handler", "arch-merge-queue-service", "term-merge-entry"]
version: "1"
salience: "0.5"
---
Term: Landing mode (LandingModeEnum). After a mission reaches WorkProduced, MissionLandingHandler applies:
- LocalMerge: fetch the captain branch from the bare repo and merge into the vessel WorkingDirectory (needs WorkingDirectory and LocalPath), push, then Complete or LandingFailed. A mission with no changes completes without merge.
- PullRequest: push and create a PR (gh), mission -> PullRequestOpen; optional auto-merge; health check reconciles merged PRs -> Complete.
- MergeQueue: auto-enqueue a MergeEntry; mission stays WorkProduced until the queue lands it.
- None: no automation; mission stays WorkProduced for manual handling.
Resolution: Voyage.LandingMode > Vessel.LandingMode > ArmadaSettings.LandingMode > legacy booleans (AutoPush default true, AutoCreatePullRequests false, AutoMergePullRequests false). An explicit None or MergeQueue never merges into the user's working directory.
BranchCleanupPolicyEnum (vessel > global, default LocalOnly): LocalOnly, LocalAndRemote, None; branches are deleted only after confirmed success. Retry a failed landing with POST /api/v1/missions/{id}/retry-landing or MCP retry_landing (MaxLandingRetries default 3). See [[arch-landing-handler]], [[arch-merge-queue-service]].
