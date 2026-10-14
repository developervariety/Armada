---
type: "Reference"
title: "Merge queue entry (mrg_) and its statuses"
description: "MergeEntry (mrg_) = one branch queued to land on a target branch with an optional test command; statuses Queued, Testing, Passed, Failed, Landed, Cancelled."
timestamp: "2026-10-05T05:30:22.7937210Z"
created: "2026-10-05T05:30:22.1466330Z"
slug: "term-merge-entry"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-merge-queue-service", "term-landing-mode"]
version: "1"
salience: "0.5"
---
Term: Merge queue entry (src/Armada.Core/Models/MergeEntry.cs, prefix mrg_). Fields: MissionId, VesselId, BranchName, TargetBranch, Status (MergeStatusEnum: Queued, Testing, Passed, Failed, Landed, Cancelled; terminal Landed/Failed/Cancelled), Priority (lower first), BatchId, TestCommand (falls back to settings MergeQueueTestCommand), TestOutput, TestExitCode, RetryCount, LeaseExpiresUtc, TestStartedUtc, CompletedUtc.

Created automatically when a mission lands with LandingMode MergeQueue, or manually via MCP enqueue_merge / REST POST /api/v1/merge-queue. Processed by MergeQueueService ([[arch-merge-queue-service]]). Tools: get_merge_entry, cancel_merge, process_merge_entry, process_merge_queue, delete_merge (terminal only, cleans branches), purge_merge_queue / purge_merge_entry / purge_merge_entries. MCP enumerate (entityType merge_queue) hides TestOutput unless includeTestOutput=true. Failed merges surface in the operator inbox (kind merge_failed).
