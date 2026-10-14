---
type: "Project"
title: "MergeQueueService: serialized test-and-land with a durable lease"
description: "ProcessQueueAsync takes an in-process flag plus a durable CoordinationLease \"merge-queue:process\" (15 min), groups Queued entries by vessel+target branch (parallel groups, sequential within), merges --no-ff in a temp worktree, runs the test command (10 min timeout), pushes, marks Landed/Failed."
timestamp: "2026-10-05T05:39:59.4034870Z"
created: "2026-10-05T05:39:58.9584670Z"
slug: "arch-merge-queue-service"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-merge-entry", "arch-coordination-leases", "term-landing-mode"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/MergeQueueService.cs; docs/MERGING.md. Driven every health tick (ArmadaServer health loop) and on demand (MCP process_merge_queue / process_merge_entry, REST POST /api/v1/merge-queue/process).

Concurrency: _ProcessLock/_Processing in-process guard (a second call while running is a silent no-op) plus cross-instance lease CoordinationLeases.TryAcquireAsync("merge-queue:process", instanceId, 15 min); released in finally. Entries: all Queued, ordered by Priority then creation; grouped by VesselId+TargetBranch; groups run in parallel via Task.WhenAll with ProcessGroupSafeAsync isolation; entries within a group strictly sequential so each merge sees the previous landing (no batch cascade failures).
Per entry: mark Testing; git fetch; temp worktree under DocksDirectory/_merge-queue/ named mq_<entryId>_<timestamp> from the target branch; git merge --no-ff; conflict -> Failed; run TestCommand (entry, else settings MergeQueueTestCommand) -> nonzero -> Failed with exit code and truncated output; push integration:target; Landed; cleanup worktree. Hard subprocess timeout 10 min for git and tests (killed, TimeoutException). Missing vessel fails the whole group. Repo path: vessel.LocalPath else ReposDirectory. Metric armada_mergequeue_processed_total.
