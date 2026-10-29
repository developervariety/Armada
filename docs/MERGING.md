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

### One active entry per branch

A branch has at most one unfinished entry (any status other than `Landed`, `Failed` or `Cancelled`) per vessel and target branch. Every stage of a pipeline commits to the same branch, and an entry merges the branch tip, so a second entry could only land the same work again or be cancelled as already landed. An enqueue for a branch that already has an active entry, from any entry point (the landing handler, the landing drain, `armada_enqueue_merge`, REST or WebSocket), attaches to that entry and returns it instead of creating a row. While the entry is still `Queued` and the attaching mission is a later stage of the entry's mission (it reaches it through `DependsOnMissionId`), the entry is retargeted to the attaching mission, whose work is the branch tip. Each attach writes a `merge_queue.enqueue_attached` event on the entry that names the attaching mission, the entry's mission and the previous one. The landing drain skips an earlier stage whose branch entry already belongs to a later stage.

When an entry lands, or is cancelled because its work already landed, the entry's mission and each earlier stage on the same branch that still reads `WorkProduced` become `Complete`, so the landing drain does not enqueue that work again.

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
- **Cancelled** -- removed from the queue. The entry's test output names the reason: `Cancelled by operator`, the work already landed (the branch or its mission commit is already in the target), or a recovery action (`recovery_rebased`, `recovery_redispatched`).

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
   4. Merge the entry's branch into the worktree (`git merge`). A branch that strictly
      descends from the target lands as a fast-forward, with no merge commit.
   5. If the merge stops on `CHANGELOG.md` alone (all three index stages present), keep
      both sides with `git merge-file --union`, stage the result, and complete the merge
      commit. Otherwise, if the merge conflicts, read the conflicted files (`git diff --name-only -z
      --diff-filter=U`, before `git merge --abort` clears them), classify the failure
      with those files and the diff size, mark the entry `Failed`, and move on.
   6. Read the landing evidence: the vessel record, every changed path
      (`git diff --name-only --no-renames -z`, so a rename reports both names and no
      name arrives C-quoted), and the unified diff. If any read fails, mark the entry
      `Failed` with a `landing_evidence_unavailable: <part>: <error>` reason; nothing
      is tested, landed or pushed. An empty change is a verified empty read, never a
      substitute for a failed one. Direct landing reads its evidence through the same
      collector.
   7. Run the dock-boundary scan (protected paths, secrets, private identifiers) on
      that evidence. A finding marks the entry `Failed`.
   8. Validate the integration candidate (see [Integration Validation](#integration-validation)).
      A refused candidate marks the entry `Failed` with the named reason.
   9. Run the configured merge-queue test command, if one is set. It is an extra
      gate after the integration validation, not a substitute for it. If tests fail,
      mark `Failed`.
   10. Before publishing, confirm that the latest evidence for the entry allows
       publication and names exactly the integration commit, and that neither
       `origin/<target>` nor the local target holds work the commit does not contain.
       Otherwise the entry goes back to `Rebasing`: the candidate is rebuilt on the
       current target and validated again, at most `MaxLandingRetries` times, after
       which the entry fails with `target_branch_drift_retry_exhausted`.
   11. Push the integration branch to update the target (`git push origin integration:target`).
       The push is never forced, so git refuses it when the target moved after step 10.
   12. Mark the entry `Landed`.
   13. Clean up the temporary worktree.

Because each entry is landed immediately, the next entry in the same group always merges against the up-to-date target branch. This eliminates the cascade failures that occur with batch-style merge queues.

## Integration Validation

A branch's own Checks measure the branch. Two branches that each pass alone can
still break the target once both are merged. Both landing paths therefore
measure the merged commit itself before the target moves: the direct landing in
its integration worktree, and the merge queue in its integration branch. They
use one shared validator.

1. The validator builds the candidate: the source branch tip, the target commit
   the merge was built on, and the merged (integration) commit.
2. It resolves the vessel's workflow profile. The required checks are the
   profile's `BuildCommand` and `UnitTestCommand`.
3. For each required check, it reuses a full native Armada Check only at exactly
   the integration commit, with exit code zero, the current profile identity,
   and an exact trimmed command. A versioned `check.execution_context` receipt
   binds that Check to its captured profile, declared environment, resolved
   execution budget, repository source and native runtime. The receipt stores
   an opaque SHA-256 fingerprint, never environment values. Missing, malformed
   or mismatched receipts require a fresh Check. Imported, deployment, gate-
   satisfied and Harbor-owned results do not supply native reuse proof.
   Harbor checks run through their configured route; native history cannot
   substitute for a current Harbor binding.
   Otherwise the normal Check service runs in an isolated checkout of the
   integration commit under the host-wide command slot. A fast-forward of a
   tip with matching independent execution evidence runs no second gate.
4. It records a `landing.integration_validation` event for the mission (direct
   landing) or the merge entry (queue). The payload names the outcome, the
   reason, the source, target-base and integration commits, and the linked
   Check IDs. The Checks link no mission and no voyage, so they never decide a
   branch-review gate.

| Outcome | Publishes | Meaning |
|---|---|---|
| `Passed` | yes | Every required check passed at the integration commit. |
| `NotRequired` | yes | The vessel is listed in `integrationValidation.noChecksRequiredVesselIds`. |
| `Disabled` | yes | `integrationValidation.enabled` is `false`. |
| `CommandMissing` | no | The profile has no `BuildCommand` and no `UnitTestCommand`, or no profile resolves. Reason `integration_validation_command_missing`. |
| `Failed` | no | A required check did not pass. Reason `integration_validation_failed`. |
| `Unavailable` | no | The profile or a check could not run, or a check was interrupted by a signal on both the run and its re-run. Reason `integration_validation_unavailable` or `integration_validation_interrupted`. |

A required check that the OS terminates by a signal (`128 + signal`; `143` is
SIGTERM) with no failing test named did not finish on its own -- a stray kill
from another process sharing the host, not the merged tree. The validator
re-runs that check once; a genuine break fails again, an interruption clears.
When it is still interrupted, the candidate is `Unavailable` with the reason
`integration_validation_interrupted`, not `Failed`: an interruption is an
environment fault, so publication is refused without a false code-failure
rescue.

A refusal leaves the target branch, its remote and the source branch unchanged.
A direct landing records the reason as the mission's failure reason. The queue
marks the entry `Failed`; only a `Failed` outcome is a code failure that starts
recovery.

When the target moves while a direct landing validates its candidate, the
candidate is stale. It is not published. The landing treats the move as
target-branch drift: the bounded retry rebuilds the merge on the new tip and
validates the new candidate. The compare-and-swap that advances the target
refuses a move that happens after that check. Evidence always names one
integration commit, so evidence for one candidate never decides another, also
after a restart.

### Stale integration worktree recovery (direct landing)

The direct landing's integration worktree lives at a deterministic per-mission
path. An attempt that does not run its own cleanup -- for example a Check that
is still running when the admiral restarts and is canceled with no verdict --
leaves that worktree on disk. Left in place it makes the next `git worktree add`
refuse with an `already exists` path and fails the landing.

Before it creates the integration worktree, the landing recovers a leftover it
owns: it prunes worktree registrations whose directories are gone, and when the
path is still a registered worktree of the landing repository it removes it and
rebuilds. The integration path is exclusive to one mission's landing and that
landing is serialized, so a registered worktree there is a finished or
interrupted attempt of the same landing and is safe to rebuild. The first
landing after a restart therefore recovers and lands; it does not fail the
voyage and wait for a second attempt to clear the path.

The recovery removes only a path the landing owns. A path at the integration
location that is NOT a registered worktree of the landing repository is foreign:
it is preserved, the landing refuses and names it in the failure reason, and the
target does not move. Cleanup after an attempt likewise removes only a worktree
that attempt created, so a preserved path is never deleted on the way out.

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
| **Merge conflict** | Entry marked `Failed` with message. The conflicted files, read before the merge is aborted, are stored on the entry and passed to the failure classifier. Worktree cleaned up. Next entry in the same group continues. |
| **Test failure** | Entry marked `Failed` with exit code and truncated output. Worktree cleaned up. Next entry continues. A test failure after the merge is surfaced to the operator (`test_failure_before_merge`); it is never routed to a rebase or redispatch captain, because the recorded test context carries no git exit code. When the definition-of-done failure classifier reads the test output as host trouble (a restore error, a missing SDK or command, a dead container runtime, a crashed test host), the entry is classified `InfraTestFailure` and surfaced to the operator (`infra_test_failure`) instead of routed to a recovery captain, because a captain on the same host would fail the same way. |
| **Landing evidence unavailable** | Entry marked `Failed` with `landing_evidence_unavailable: vessel_unreadable`, `vessel_not_found`, `changed_files_unreadable`, or `diff_unreadable` and the underlying error. Nothing is pushed. |
| **Test timeout** | The test command and its whole process tree are stopped after `MergeQueueTestTimeoutSeconds`; the entry is marked `Failed` with `merge_queue_test_timeout`. Both output streams drain concurrently, so a command that fills one stream while holding the other open cannot hold the host test lock. The command owns its process group, so the stop also reaches a background child it left behind. Each stream keeps 1 MiB, its beginning and its end, with a marker naming what was dropped. |
| **Push failure** | Entry marked `Failed` with error message, and the linked mission becomes `LandingFailed`. A landing that did not reach origin also records a `merge_queue.origin_push_failed` event and opens one High incident per vessel and target branch. Both name the branch, the git error, and the bare target, origin target, and integration heads. The usual cause is a bare target and origin that have diverged; see "Reconcile a bare target that diverged from origin" below. |
| **Failure after the push advanced the target** | The queue rolls the target back to its pre-land head with `git push --force-with-lease=refs/heads/<target>:<inspected-head>`. If another writer moved the target after the rollback inspected it, the push is refused and that writer's commit stays. The `merge_queue.failed_target_advanced` event records `rolled_back`, `partial_rollback: ...`, or `rollback_failed: ...`. |
| **Vessel not found** | All entries in the group are marked `Failed` with a message indicating the vessel could not be resolved. |
| **Integration setup failure** | An error while preparing the integration worktree (fetch, worktree creation) marks the entry `Failed` with `integration_setup_failed: <error>`, the mission becomes `LandingFailed`, and recovery runs. |
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
- **Reconcile a bare target that diverged from origin.** The merge queue cuts
  its integration branch from the bare target, so when origin holds commits
  the bare lacks (for example after an operator landed something directly),
  every push is rejected as non-fast-forward. Compare the two in the vessel
  bare repo: `git fetch origin`, then `git rev-list --left-right --count
  refs/heads/<target>...refs/remotes/origin/<target>`. When origin only is
  ahead, fast-forward the bare (`git update-ref refs/heads/<target>
  refs/remotes/origin/<target>`). When the bare only is ahead, push the landed
  commits to origin with a fast-forward. When both are ahead, merge origin into
  the bare target in a scratch worktree and push the merge. Never reset the
  bare target to origin while it holds landed commits: those commits are then
  reachable only from mission and preserved refs. Retry the failed landing
  afterwards.
- **A direct landing pushes before it moves the bare target.** The merged
  commit is validated first (see [Integration Validation](#integration-validation)).
  Outside `LocalMerge`, the merged commit is then pushed to origin and the bare target
  advances only after the push succeeds, so a refused push leaves the bare
  unchanged. When target-branch drift exhausts the landing retries, the
  landing records `landing.origin_push_failed` and opens one High incident per
  vessel and target with the bare and origin heads.
- **A landing retry does not need the mission's dock.** A failed landing's
  dock is reclaimed. `retry_landing` reads the change from a temporary
  detached worktree at the mission branch tip and removes it afterwards.
- **A voyage is not Complete while a retried landing of its work runs.** The
  retry holds its mission in `WorkProduced` while it lands, and every completion
  writer keeps the voyage open for that time (reason `landing_retry_in_flight`).
  The terminal-voyage reconciler and the landing-drain sweep leave the mission
  alone too. When the retry ends it decides the voyage: a landed retry completes
  it, and a failed retry returns the mission to `LandingFailed` and the voyage
  ends `Failed`.
- **A voyage is not given a terminal status while a merge entry for one of its
  missions is still landing.** A Judge PASS puts its work on the merge queue,
  which builds and verifies an integration candidate after the release returns,
  so the work is still being landed while its entry is non-terminal. Every
  completion writer keeps the voyage open for that time (reason
  `merge_in_flight`), so a voyage is never terminalized mid-landing. This matters
  most for a reopened voyage whose earlier stages were reconciled `Failed` by a
  prior terminal pass: without the hold the completion rule would re-fail the
  voyage on those stages and cascade an unlanded outcome onto the stage that is
  in fact landing. When the entry reaches a terminal status the landing decides
  the voyage.
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
| `LocalMerge` | Merge the branch into the vessel's configured working directory without pushing. The vessel must have both `WorkingDirectory` and `LocalPath` configured. Mission transitions to `Complete` on success or `LandingFailed` on failure. If those vessel paths are not configured, the mission remains at `WorkProduced`. A mission with an empty diff snapshot or a branch that is missing from the bare repository lands as a no-op only when no commit is on record past its start commit, or the recorded commit is already on the target branch. Otherwise the landing fails as `landing_source_unavailable`, naming the recorded and start commits, and the branch is kept for retry. |
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

A branch counts as landed only when its tip is contained in the bare repository's target branch and, when the bare tracks `origin/<target>`, in that too. A merge only the bare holds, such as a rejected push or a `LocalMerge` landing not yet pushed, keeps its branches; the sweep summary reports them as `unpushed`.

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
- **`IntegrationValidation`** (in `ArmadaSettings`) -- policy for the shared integration validation. `enabled` (default `true`) runs it on every landing; `false` turns it off and records the `Disabled` outcome. `noChecksRequiredVesselIds` (default empty) lists vessels that require no integration checks; they record `NotRequired` and land. Any other vessel without a `BuildCommand` or `UnitTestCommand` in its workflow profile is refused with `integration_validation_command_missing`.
- **`MergeQueueTestCommand`** (in `ArmadaSettings`) -- extra test command the queue runs in the integration worktree after the integration validation, for entries that don't specify their own. Can be overridden per entry via the `testCommand` parameter on `armada_enqueue_merge`. When neither is set, the integration validation is the only test gate.
- **`MergeQueueTestTimeoutSeconds`** (in `ArmadaSettings`, default `3600`, clamped to 1-86400) -- longest time a merge-queue test command may run before the queue stops it with its process tree and fails the entry.
- **`DocksDirectory`** -- parent directory for temporary merge worktrees. Worktrees are created under `_merge-queue/` within this directory.
- **`ReposDirectory`** -- fallback repository path when a vessel's `LocalPath` is not set.
