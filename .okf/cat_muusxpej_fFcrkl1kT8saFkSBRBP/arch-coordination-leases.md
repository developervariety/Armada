---
type: "Project"
title: "Multi-instance safety: CoordinationLease table and in-memory guards"
description: "CoordinationLease rows (migration 44) give a durable compare-and-swap lease with expiry takeover; today MergeQueueService uses lease \"merge-queue:process\"; other guards (process-exit dedup, repo provisioning locks, completion dedup) are still in-memory per Admiral."
timestamp: "2026-10-05T05:44:08.9936850Z"
created: "2026-10-05T05:44:08.3808040Z"
slug: "arch-coordination-leases"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-merge-queue-service", "data-schema-migration-numbering"]
version: "1"
salience: "0.5"
---
Model src/Armada.Core/Models/CoordinationLease.cs (Name, holder, ExpiresUtc); methods ICoordinationLeaseMethods (TryAcquireAsync(name, holderId, duration, ...), ReleaseAsync) on all four providers; table added by startup migration 44 (v0.9.0) together with dock state/lease, captain process-liveness, mission review deadline, and merge-entry retry/lease columns.

Semantics: acquisition is an atomic compare-and-swap keyed by Name; an expired lease can be taken over so a crashed holder never blocks forever. Current user in code: MergeQueueService.ProcessQueueAsync (lease "merge-queue:process", 15 min, per-instance id) so only one Admiral sharing a database processes the queue. The model's doc comment mentions replacing process-exit dedup and per-repo provisioning locks, but in the code those remain in-process (AgentLifecycleHandler exit dedup, DockService per-repo SemaphoreSlim, MissionService completion in-flight guard, MissionLandingHandler per-vessel merge semaphore, AdmiralService crash counters). Treat running two Admirals against one database as only partially supported.
