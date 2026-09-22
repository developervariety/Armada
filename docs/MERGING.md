---
topic: "Merge Queue And Landing"
summary: "How Armada serializes merges, lands mission work, and recovers a diverged checkout."
read_when: "Landing work, choosing a landing mode, or diagnosing a merge or sync failure."
applies_to: orchestrator
tier: leaf
---
# Merge Queue

## Overview

Armada includes a built-in merge queue that serializes branch merges into a target branch, running optional tests before landing each one. Entries targeting the same vessel and target branch are processed **sequentially** to avoid conflicts, while different vessel+target-branch groups are processed **in parallel** for throughput. This design ensures correctness within a group (each merge sees the result of the previous one) while maximizing overall processing speed across independent repositories and branches.

The merge queue is managed through MCP tools (`armada_enqueue_merge`, `armada_process_merge_queue`, `armada_enumerate` with entityType 'merge_queue', etc.) and operates on the bare repository clones that Armada maintains for each vessel.

---

## Status State Machine

```
Queued --> Testing --> Landed
  |          |
  v          v
Cancelled  Failed
```

- **Queued** -- waiting to be picked up by a processing run.
- **Testing** -- merged into a temporary integration branch; tests are running.
- **Landed** -- tests passed and the merge was pushed to the target branch.
- **Failed** -- merge conflict or test failure.
- **Cancelled** -- manually removed from the queue.

Terminal states: `Landed`, `Failed`, `Cancelled`.

---

## Processing Flow

1. **Acquire global lock** -- only one queue processing run can happen at a time across *all* vessels and target branches. There is a single lock on the entire `MergeQueueService` instance, not one lock per vessel. If already processing, the call returns immediately (no-op). This means that if you call `armada_process_merge_queue` while a previous run is still working through entries, the second call is silently dropped. Within a single processing run, however, independent vessel+target-branch groups are processed in parallel (see step 3).

2. **Fetch queued entries** -- all entries with status `Queued` are loaded, ordered by priority (lower number = higher priority) then by creation time.

3. **Group by vessel + target branch** -- entries targeting the same vessel and branch form a group. Groups are processed **in parallel** using `Task.WhenAll`. Each group is wrapped in error isolation (`ProcessGroupSafeAsync`) so that one group's failure does not affect other groups. Entries *within* each group remain strictly sequential: each entry is merged, tested, and landed before the next entry in the same group begins. Each entry gets its own temporary worktree path, so the integration worktrees do not collide. Note, however, that different groups may still resolve to the same underlying bare repository when they target different branches on the same vessel.

4. **For each entry in a group** (sequential within the group):
   1. Mark the entry as `Testing`.
   2. Fetch latest refs from the remote (`git fetch`).
   3. Create a temporary worktree from the current target branch.
   4. Merge the entry's branch into the worktree (`git merge --no-ff`).
   5. If the merge conflicts, mark the entry `Failed` and move on.
   6. Read the landing evidence: the vessel record, every changed path
      (`git diff --name-only --no-renames -z`, so a rename reports both names and no
      name arrives C-quoted), and the unified diff. If any read fails, mark the entry
      `Failed` with a `landing_evidence_unavailable: <part>: <error>` reason; nothing
      is tested, landed or pushed. An empty change is a verified empty read, never a
      substitute for a failed one. Direct landing reads its evidence through the same
      collector.
   7. Run the dock-boundary scan (protected paths, secrets, private identifiers) on
      that evidence. A finding marks the entry `Failed`.
   8. Run the configured test command (if any). If tests fail, mark `Failed`.
   9. Push the integration branch to update the target (`git push origin integration:target`).
   10. Mark the entry `Landed`.
   11. Clean up the temporary worktree.

Because each entry is landed immediately, the next entry in the same group always merges against the up-to-date target branch. This eliminates the cascade failures that occur with batch-style merge queues.

---

## Thread Safety

- A single `_ProcessLock` object gate-keeps entry to `ProcessQueueAsync`. The lock is checked-and-set inside a `lock` block. If `_Processing` is already `true`, the call returns immediately.
- Within a processing run, each vessel+target-branch group runs as an independent `Task`. Groups execute in parallel via `Task.WhenAll`. Each group task is wrapped in a `try-catch` (`ProcessGroupSafeAsync`) for error isolation, so a failure in one group does not cancel or affect other groups. Worktree paths under `_merge-queue/` are unique per entry, and groups update different merge-entry rows. Different groups can still point at the same bare repository if they target different branches on the same vessel.
- Within a single group, entries are processed strictly one at a time. There is no concurrency within a group.
- The lock is released in a `finally` block, so even if `Task.WhenAll` throws, the next call to `ProcessQueueAsync` will be able to proceed.

---

## Failure Scenarios

| Scenario | Behavior |
|---|---|
| **Merge conflict** | Entry marked `Failed` with message. Worktree cleaned up. Next entry in the same group continues. |
| **Test failure** | Entry marked `Failed` with exit code and truncated output. Worktree cleaned up. Next entry continues. |
| **Landing evidence unavailable** | Entry marked `Failed` with `landing_evidence_unavailable: vessel_unreadable`, `vessel_not_found`, `changed_files_unreadable`, or `diff_unreadable` and the underlying error. Nothing is pushed. |
| **Test timeout** | The test command and its whole process tree are stopped after `MergeQueueTestTimeoutSeconds`; the entry is marked `Failed` with `merge_queue_test_timeout`. Both output streams drain concurrently, so a command that fills one stream while holding the other open cannot hold the host test lock. |
| **Push failure** | Entry marked `Failed` with error message. Typically means the remote rejected the push (force-push protection, etc.). |
| **Failure after the push advanced the target** | The queue rolls the target back to its pre-land head with `git push --force-with-lease=refs/heads/<target>:<inspected-head>`. If another writer moved the target after the rollback inspected it, the push is refused and that writer's commit stays. The `merge_queue.failed_target_advanced` event records `rolled_back`, `partial_rollback: ...`, or `rollback_failed: ...`. |
| **Vessel not found** | All entries in the group are marked `Failed` with a message indicating the vessel could not be resolved. |
| **Unexpected exception** | Entry marked `Failed` with error message. Best-effort worktree cleanup. Processing continues to the next entry. Group-level exceptions are caught by `ProcessGroupSafeAsync` and logged as warnings. |

### Known Operator Notes

These are observed behaviors that the entry status alone does not make obvious.

- **`enqueue_merge` consumes the source branch into a
  `refs/heads/armada/merge-queue/<id>` ref.** A later
  `process_merge_entry` that reports
  `Branch ... was not found ... as a local branch or as origin/...` may mean
  the landing actually succeeded (only the original name resolution failed)
  OR that it genuinely failed and the branch is gone. Verify the target
  branch contains the commit (`git merge-base --is-ancestor <sha> <target>`
  in the vessel bare repo) before re-enqueueing; restore the branch from the
  mission commit hash if it is gone.
- **Processing the same vessel+target concurrently fails.** Two parallel
  `process_merge_entry` calls against one target collide on the push and
  both can report a non-fast-forward rejection even though either alone
  would succeed. Operators landing multiple branches must process them one
  at a time and wait for a terminal state before the next.
- **A landing can drop sibling commits from the target.** When enqueued
  branches are based on an older base, landing can rebuild the target from
  that base lineage and lose commits that sat at the previous target tip.
  After a batch of landings, diff the pre-batch target tip against the
  post-batch history and cherry-pick back any dropped commit.
- **`retry_check_run` / `run_check` build the bare repo's target ref, not
  the working checkout.** After a direct push to `origin`, sync the bare
  repo (`git fetch origin` then `git update-ref refs/heads/<target>
  refs/remotes/origin/<target>`) or Checks keep testing stale code.

---

## Best Practices

- **One branch per entry.** Each merge queue entry corresponds to a single feature branch being merged into a target branch.
- **Keep test commands fast.** Tests run synchronously per entry, blocking subsequent entries in the same group. Long tests slow down the entire group's queue throughput.
- **Use priorities.** Lower priority numbers are processed first within a group. Use this to land critical fixes ahead of routine changes.
- **Monitor terminal entries.** Use `armada_enumerate` with entityType 'merge_queue' and status 'Failed' to check for entries that may need attention.
- **Clean up regularly.** Use `armada_delete_merge` or `armada_purge_merge_queue` to remove terminal entries and their associated git branches.

---

## Commands Reference

| Tool | Description |
|---|---|
| `armada_enqueue_merge` | Add an entry to the merge queue. |
| `armada_process_merge_queue` | Trigger a processing run (no-op if already running). |
| `armada_process_merge_entry` | Process a single entry by ID. |
| `armada_get_merge_entry` | Get a single entry by ID. |
| `armada_cancel_merge` | Cancel a queued entry. |
| `armada_delete_merge` | Delete a terminal entry and clean up its branches. |
| `armada_purge_merge_queue` | Bulk delete all terminal entries, with optional vessel/status filters. |

---

## Landing Mode

When a mission's agent exits successfully, Armada sets the mission to `WorkProduced` and then applies the **landing mode** to determine how to integrate the work. The landing mode is resolved in priority order:

1. **Voyage-level** `LandingMode` (if the mission belongs to a voyage with a non-null `LandingMode`)
2. **Vessel-level** `LandingMode` (on the target vessel)
3. **Global** `LandingMode` (in `ArmadaSettings`)
4. **Legacy booleans** (`AutoPush`, `AutoCreatePullRequests`, `AutoMergePullRequests`) if all of the above are null

| Landing Mode | Behavior |
|---|---|
| `LocalMerge` | Merge the branch into the vessel's configured working directory without pushing. The vessel must have both `WorkingDirectory` and `LocalPath` configured. Mission transitions to `Complete` on success or `LandingFailed` on failure. If those vessel paths are not configured, the mission remains at `WorkProduced`. |
| `PullRequest` | Create a pull request. Mission transitions to `PullRequestOpen`. Armada polls for merge confirmation; once merged, transitions to `Complete`. |
| `MergeQueue` | Enqueue the branch into Armada's merge queue for serialized testing and landing. |
| `None` | No automated landing. The mission stays at `WorkProduced` for manual handling. |

**Working-directory sync (`LocalMerge`).** After a `LocalMerge` lands on the bare
repository, Armada fast-forwards the vessel's configured working checkout to the
landed tip. The pre-sync check guards on **tracked** changes only
(`IGitService.HasUncommittedTrackedChangesAsync`): untracked files, such as a
captain scratch directory left in the checkout, do not block the sync, because a
fast-forward preserves them and `MergeBranchLocalAsync` (which the sync calls)
tolerates them too. A checkout with uncommitted **tracked** changes, or on the
wrong branch, is left untouched and the mission records
`working_directory_sync_failed` for the operator to reconcile by hand — the work
is still on the target branch.

**Diverged working checkout (`LocalMerge`).** The working checkout and the
landing repository are separate clones. When the checkout holds commits that
the landing repository's target branch does not, the fast-forward cannot run
and those commits exist in one place only. Armada never resets the checkout and
never pushes to a remote. Instead it:

1. Counts the checkout-only commits (`git rev-list --count <target-tip>..HEAD`
   in the checkout).
2. Pushes the checkout `HEAD` to `recover/working-checkout-<12-char-sha>` in the
   landing repository (the vessel `LocalPath`), without force.
3. Emits one `landing.working_checkout_diverged` event and opens one incident
   for the vessel. The incident names the recover branch, the full SHA, the
   commit count and the operator steps. A later landing that finds the same
   recover branch with the incident still open adds neither again.

The mission reason names the recover branch. To repair: review
`git log <target>..recover/working-checkout-<sha>` in the landing repository,
land that branch through the merge queue, fast-forward the checkout
(`git fetch <LocalPath> <target>` then `git merge --ff-only FETCH_HEAD`),
confirm both `rev-list --count` directions read 0, then close the incident and
delete the recover branch. If the checkout cannot be read, counted or pushed,
nothing is pushed and the incident reason says which step failed.

---

## Branch Cleanup Policy

After a mission's work has landed, Armada can automatically clean up the mission branch. The policy is resolved from the vessel level (falling back to global settings):

| Policy | Behavior |
|---|---|
| `LocalOnly` | Delete the local branch only (default) |
| `LocalAndRemote` | Delete both local and remote branches |
| `None` | Leave branches in place |


New bare clones enable `core.logAllRefUpdates`. Platform deletion records
`git.ref_deleted`, naming the ref, repository, remote (when used), and caller.
Cleanup accepts only `armada/`, `armada-landing/`, `refs/armada-preserved/`,
`refs/armada/docks/`, and `refs/armada/missions/`; user and `recover/` refs
are refused as `ref_delete_unmanaged`. Existing reachability, age, active-owner
and expected-SHA checks still apply before cleanup.

A `reference-transaction` hook also records committed deletions made through
raw Git. It writes an atomic record under the common repository's
`armada-ref-audit` directory. Health maintenance imports up to 200 records per
repository per cycle into `git.ref_deleted` events, with a stable event ID so a
restart cannot duplicate an imported record. The payload names the source,
previous SHA, ref, working directory, OS user ID and Git process ID. Provisioned
worktrees carry mission and captain context outside tracked files; the collector
links it only when the stored mission belongs to that vessel and captain.
Commands without verified mission context retain OS attribution explicitly.
API deletion and Git transaction events are separate witnesses of the same
operation; consumers can distinguish the transaction source in the payload.
Git can report an unknown old tip, so the hook captures it during preparation
and publishes the record only after commit; an aborted transaction is discarded.
If the logical ref is still present at that point, as when packing removes loose
storage or a concurrent writer recreates the ref, the event is
`git.ref_removal_observed` instead of a confirmed deletion. Both retain caller
and prior-tip evidence. A preparation-time journal failure refuses the Git
transaction rather than allowing an unrecorded deletion.

Installation honors `core.hooksPath` and preserves an existing operator-owned
`reference-transaction` hook. A conflict or collection failure is logged as
`ref_audit_unavailable`; it must be resolved before claiming audit coverage.
Malformed records are retained with a `.rejected` suffix and a named warning.
The hook needs Git's shell and standard shell utilities. It is observability,
not an isolation boundary: disabling or removing it bypasses capture, and direct
filesystem edits are outside Git transactions. Reflogs alone are incomplete
because Git can remove a deleted ref's log. Preserve a recovery bundle and full
commit SHA before an operator retires a parked ref.

---

## Configuration

- **`LandingMode`** (in `ArmadaSettings`) -- global landing policy. Can be overridden per-vessel (`Vessel.LandingMode`) or per-voyage (`Voyage.LandingMode`).
- **`BranchCleanupPolicy`** (in `ArmadaSettings`) -- global branch cleanup policy. Can be overridden per-vessel (`Vessel.BranchCleanupPolicy`).
- **`MergeQueueTestCommand`** (in `ArmadaSettings`) -- default test command to run for entries that don't specify their own. Can be overridden per entry via the `testCommand` parameter on `armada_enqueue_merge`.
- **`MergeQueueTestTimeoutSeconds`** (in `ArmadaSettings`, default `3600`, clamped to 1-86400) -- longest time a merge-queue test command may run before the queue stops it with its process tree and fails the entry.
- **`DocksDirectory`** -- parent directory for temporary merge worktrees. Worktrees are created under `_merge-queue/` within this directory.
- **`ReposDirectory`** -- fallback repository path when a vessel's `LocalPath` is not set.
