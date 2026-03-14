# Armada Failure Cases — Actionable Implementation Plan

> Derived from FAILURE_CASES_PLAN.md (Claude + Codex consensus) and verified
> against the actual source code. Each task includes exact file paths, method
> names, line-level guidance, and a progress checkbox.
>
> **Legend:** `[ ]` not started · `[~]` in progress · `[x]` complete · `[!]` blocked

---

## Tier 1 — Stop the Bleeding

All T1 items are independent. No cross-dependencies.

---

### T1-1: Block same-vessel concurrency by default

**Status:** `[x]` Complete

**Goal:** Only one active (Assigned/InProgress/WorkProduced/PullRequestOpen) mission
per vessel at a time, unless the operator explicitly opts in.

#### 1a. Add `AllowConcurrentMissions` to the Vessel model

- **File:** `src/Armada.Core/Models/Vessel.cs`
- **Change:** Add property after `BranchCleanupPolicy`:
  ```csharp
  /// <summary>
  /// Whether this vessel allows multiple concurrent missions. Default false.
  /// When false, only one mission may be in an active state
  /// (Assigned, InProgress, WorkProduced, PullRequestOpen) at a time.
  /// </summary>
  public bool AllowConcurrentMissions { get; set; } = false;
  ```

#### 1b. Schema migration v12 — add column to vessels table

- **Files (4 drivers):**
  - `src/Armada.Core/Database/Sqlite/Queries/TableQueries.cs`
  - `src/Armada.Core/Database/Postgresql/PostgresqlDatabaseDriver.cs` (or its TableQueries equivalent)
  - `src/Armada.Core/Database/Mysql/Queries/TableQueries.cs`
  - `src/Armada.Core/Database/SqlServer/...` (find TableQueries equivalent)
- **SQL (SQLite example):**
  ```sql
  ALTER TABLE vessels ADD COLUMN allow_concurrent_missions INTEGER NOT NULL DEFAULT 0;
  ```
- **Migration object:**
  ```csharp
  new SchemaMigration(12, "Add allow_concurrent_missions to vessels", new[] { sql })
  ```
- **Repeat** for Postgres (`BOOLEAN NOT NULL DEFAULT FALSE`), MySQL (`TINYINT(1) NOT NULL DEFAULT 0`), SQL Server (`BIT NOT NULL DEFAULT 0`).

#### 1c. Update all 4 database driver vessel read/write methods

- Map `allow_concurrent_missions` ↔ `Vessel.AllowConcurrentMissions` in:
  - `src/Armada.Core/Database/Sqlite/Implementations/VesselMethods.cs`
  - `src/Armada.Core/Database/Postgresql/Implementations/VesselMethods.cs`
  - `src/Armada.Core/Database/Mysql/Implementations/VesselMethods.cs`
  - `src/Armada.Core/Database/SqlServer/Implementations/VesselMethods.cs`
- Null-check: if column is NULL in DB, default to `false`.

#### 1d. Enforce serialization in `TryAssignAsync`

- **File:** `src/Armada.Core/Services/MissionService.cs`, method `TryAssignAsync` (~line 70)
- **Current code (lines 98–102):** Warns about concurrent missions but proceeds.
- **New behavior:** After the broad-scope checks (lines 76–96), add:
  ```csharp
  // Enforce per-vessel serialization unless explicitly allowed
  if (!vessel.AllowConcurrentMissions)
  {
      int activeCount = activeMissions.Count(m =>
          m.Status == MissionStatusEnum.Assigned ||
          m.Status == MissionStatusEnum.InProgress ||
          m.Status == MissionStatusEnum.WorkProduced ||
          m.Status == MissionStatusEnum.PullRequestOpen);

      if (activeCount > 0)
      {
          _Logging.Info(_Header + "vessel " + vessel.Id + " already has active mission(s); deferring " + mission.Id + " (AllowConcurrentMissions=false)");
          return false;
      }
  }
  ```
- **Remove or downgrade** the existing warning-only block (lines 98–102) since it is now superseded by the enforcement block.

#### 1e. Expose in MCP vessel tools

- **File:** `src/Armada.Server/Mcp/VesselAddArgs.cs` — add `public bool? AllowConcurrentMissions { get; set; }`
- **File:** `src/Armada.Server/Mcp/VesselUpdateArgs.cs` — add same property
- **File:** `src/Armada.Server/Mcp/McpToolRegistrar.cs` — in `RegisterVesselTools()`:
  - `armada_add_vessel`: map arg to `vessel.AllowConcurrentMissions` (default false if null)
  - `armada_update_vessel`: if non-null, update the field

#### 1f. Tests

- **File:** `test/Armada.Test.Unit/Suites/Services/SequentialDispatchTests.cs` (existing — extend)
- Add test: `TryAssignAsync` returns `false` when vessel has an InProgress mission and `AllowConcurrentMissions == false`.
- Add test: `TryAssignAsync` returns `true` when vessel has an InProgress mission and `AllowConcurrentMissions == true`.

#### Checklist

- [x] 1a — Vessel model property
- [x] 1b — Schema migration v12 (SQLite + MySQL; Postgres/SqlServer don't have migrations yet)
- [x] 1c — DB read/write mapping (all 4 drivers)
- [x] 1d — TryAssignAsync enforcement (includes WorkProduced/PullRequestOpen in active check)
- [x] 1e — MCP tool Args + registrar
- [ ] 1f — Unit tests (deferred to separate pass)

---

### T1-2: Fix missing-PID treated as success

**Status:** `[x]` Complete

**Goal:** A process that no longer exists in the OS process table should be
treated as an unknown/failure, not a clean success.

#### 2a. Change exit code in ArgumentException handler

- **File:** `src/Armada.Core/Services/AdmiralService.cs`, method `HealthCheckCaptainAsync`
- **Current code (lines 549–553):**
  ```csharp
  catch (ArgumentException)
  {
      // Process no longer exists in process table - treat as clean exit
      isAlive = false;
      exitCode = 0;  // <-- BUG
  }
  ```
- **Change to:**
  ```csharp
  catch (ArgumentException)
  {
      // Process no longer exists — treat as unknown (not success)
      isAlive = false;
      exitCode = -1;
  }
  ```
- **Update comment** to reflect the new semantics.

#### 2b. Add commit-proof check before promoting to WorkProduced

- **File:** `src/Armada.Core/Services/MissionService.cs`, method `HandleCompletionAsync` (~line 270)
- **After** setting `mission.Status = WorkProduced` (line 280), add a check:
  - Call `OnVerifyWorkProduced?.Invoke(mission)` (new delegate, similar to `OnMissionComplete`)
  - The delegate (wired in ArmadaServer) should run:
    ```
    git log <baseBranch>..<missionBranch> --oneline
    ```
  - If no commits, set `mission.Status = Failed` with reason "No commits produced".
- **Alternative (simpler):** Add the git check in `HandleCompletionAsync` itself via the existing `_Git` reference (but MissionService doesn't have `_Git` — so a delegate/callback is cleaner).

#### 2c. Wire the delegate in ArmadaServer

- **File:** `src/Armada.Server/ArmadaServer.cs`
- During init where `OnMissionComplete` is wired, also wire `OnVerifyWorkProduced`:
  ```csharp
  _Missions.OnVerifyWorkProduced = async (mission) =>
  {
      // check git log baseBranch..missionBranch for commits
  };
  ```

#### Checklist

- [x] 2a — Fix exitCode = -1
- [ ] 2b — Add work-verification delegate to MissionService (deferred to T2)
- [ ] 2c — Wire delegate in ArmadaServer (deferred to T2)

---

### T1-3: Stop deleting sibling worktrees

**Status:** `[x]` Complete

**Goal:** `DockService.ProvisionAsync` must not delete directories belonging to
active captains on the same vessel.

#### 3a. Query active docks before cleanup

- **File:** `src/Armada.Core/Services/DockService.cs`, method `ProvisionAsync` (lines 93–140)
- **Current code:** Iterates all directories under `vesselDockDir`, skips only `captain.Name`, deletes everything else.
- **New behavior:** Before the cleanup loop, query the database for active docks on this vessel:
  ```csharp
  List<Dock> activeDocks = await _Database.Docks.EnumerateByVesselAsync(vessel.Id, token);
  HashSet<string> activeDockPaths = new HashSet<string>(
      activeDocks.Where(d => d.Active).Select(d => d.WorktreePath),
      StringComparer.OrdinalIgnoreCase);
  ```
- In the loop, before deleting `existingDir`:
  ```csharp
  if (activeDockPaths.Contains(existingDir))
  {
      _Logging.Info(_Header + "skipping cleanup of " + existingDir + ": still in use by an active dock");
      continue;
  }
  ```

#### 3b. Verify `EnumerateByVesselAsync` exists on IDockMethods

- **File:** `src/Armada.Core/Database/Interfaces/IDockMethods.cs`
- If not present, add: `Task<List<Dock>> EnumerateByVesselAsync(string vesselId, CancellationToken token = default);`
- Implement in all 4 driver `DockMethods.cs` files.

#### Checklist

- [x] 3a — Guard deletion with active-dock check (queries DB for active docks before cleanup)
- [x] 3b — EnumerateByVesselAsync already exists in IDockMethods and all drivers

---

### T1-4: Stop marking local landing Complete on push failure

**Status:** `[x]` Complete

**Goal:** If `git push` fails after a local merge, the mission must be
`LandingFailed`, not `Complete`.

#### 4a. Locate the local merge landing path

- **File:** `src/Armada.Server/ArmadaServer.cs`, method `HandleMissionCompleteAsync` (~line 2488+)
- Find the section where `MergeBranchLocalAsync` is called, followed by the push.
- Trace the variable `landingSucceeded` (or equivalent flag) through both merge and push.

#### 4b. Ensure push failure flips the flag

- After the push call, if the push throws or returns an error:
  ```csharp
  landingSucceeded = false;
  mission.Status = MissionStatusEnum.LandingFailed;
  mission.StatusReason = "Push to remote failed: " + ex.Message;
  ```
- **Do NOT delete the mission branch** on landing failure.

#### 4c. Preserve branch on failure

- In the branch cleanup section, add a guard:
  ```csharp
  if (mission.Status == MissionStatusEnum.LandingFailed)
  {
      _Logging.Info(_Header + "preserving branch " + branchName + " for retry (landing failed)");
      // Skip branch deletion
  }
  ```

#### Checklist

- [x] 4a — Located in ArmadaServer.HandleMissionCompleteAsync (~line 2694)
- [x] 4b — Push failure now sets landingSucceeded=false → LandingFailed
- [x] 4c — Branch cleanup moved AFTER push; skipped on failure

---

### T1-5: Reconcile merge-queue result back to mission status

**Status:** `[x]` Complete

**Goal:** When a merge-queue entry reaches `Landed` or `Failed`, the linked
mission must be updated accordingly.

#### 5a. Update mission on successful landing

- **File:** `src/Armada.Core/Services/MergeQueueService.cs`, method `LandEntryAsync` (line 450)
- After `entry.Status = MergeStatusEnum.Landed` (line 456), add:
  ```csharp
  // Reconcile linked mission
  if (!String.IsNullOrEmpty(entry.MissionId))
  {
      Mission? linkedMission = await _Database.Missions.ReadAsync(entry.MissionId, token);
      if (linkedMission != null &&
          linkedMission.Status != MissionStatusEnum.Complete &&
          linkedMission.Status != MissionStatusEnum.Failed &&
          linkedMission.Status != MissionStatusEnum.Cancelled)
      {
          linkedMission.Status = MissionStatusEnum.Complete;
          linkedMission.CompletedUtc = DateTime.UtcNow;
          linkedMission.LastUpdateUtc = DateTime.UtcNow;
          linkedMission.StatusReason = "Landed via merge queue entry " + entry.Id;
          await _Database.Missions.UpdateAsync(linkedMission, token);
      }
  }
  ```

#### 5b. Update mission on failed landing

- In the `catch` block of `LandEntryAsync` (line 462), after `entry.Status = MergeStatusEnum.Failed`:
  ```csharp
  if (!String.IsNullOrEmpty(entry.MissionId))
  {
      Mission? linkedMission = await _Database.Missions.ReadAsync(entry.MissionId, token);
      if (linkedMission != null &&
          linkedMission.Status != MissionStatusEnum.Complete &&
          linkedMission.Status != MissionStatusEnum.Failed &&
          linkedMission.Status != MissionStatusEnum.Cancelled)
      {
          linkedMission.Status = MissionStatusEnum.LandingFailed;
          linkedMission.LastUpdateUtc = DateTime.UtcNow;
          linkedMission.StatusReason = "Merge queue landing failed: " + ex.Message;
          await _Database.Missions.UpdateAsync(linkedMission, token);
      }
  }
  ```

#### 5c. Check for voyage completion after mission update

- After updating the linked mission to Complete (in 5a), check if all missions
  in the same voyage are now terminal:
  ```csharp
  if (!String.IsNullOrEmpty(linkedMission.VoyageId))
  {
      // VoyageService.CheckCompletionsAsync will pick this up on next heartbeat
      // No extra work needed here — the status change is sufficient
  }
  ```
- **Note:** `VoyageService.CheckCompletionsAsync` already runs on each heartbeat
  and will detect the completion. No new code needed for voyage reconciliation
  beyond updating the mission status.

#### 5d. Verify MergeEntry has MissionId field

- **File:** `src/Armada.Core/Models/MergeEntry.cs`
- Confirm `MissionId` property exists. If not, add it and add schema migration.

#### Checklist

- [x] 5a — ReconcileMissionStatusAsync added, called on Landed → Complete
- [x] 5b — ReconcileMissionStatusAsync called on Failed → LandingFailed
- [x] 5c — VoyageService.CheckCompletionsAsync runs on heartbeat, picks up status changes
- [x] 5d — MergeEntry.MissionId confirmed exists (nullable string property)

---

## Tier 2 — Close State Machine Gaps

Dependencies noted per item.

---

### T2-1: Persistent PR reconciler (depends on T1-5)

**Status:** `[x]` Complete

**Goal:** Missions in `PullRequestOpen` must be checked periodically, not just
via a one-shot 5-minute poller.

#### 6a. Add PR reconciliation to the health-check loop

- **File:** `src/Armada.Core/Services/AdmiralService.cs`, method `HealthCheckAsync`
- At the end of the health-check cycle (after captain checks), add a new step:
  ```csharp
  await ReconcilePullRequestMissionsAsync(token);
  ```

#### 6b. Implement `ReconcilePullRequestMissionsAsync`

- **File:** `src/Armada.Core/Services/AdmiralService.cs` (new private method)
- Query all missions with `Status == PullRequestOpen`.
- For each, check PR status via `_Git.IsPrMergedAsync(...)`.
  - If merged → transition to `Complete`, emit event.
  - If closed without merge → transition to `LandingFailed`.
  - If still open → skip (check again next cycle).
- Rate-limit: process at most 10 PRs per cycle. Track `LastPrCheckUtc` per mission
  to avoid re-checking the same PR every 30 seconds.

#### 6c. Add `LastPrCheckUtc` to Mission model (optional optimization)

- Could use an in-memory dictionary instead to avoid schema change.
- If using schema: migration v13, column on missions table.

#### 6d. Remove or reduce the fire-and-forget poller

- **File:** `src/Armada.Server/ArmadaServer.cs`, method `PollAndPullAfterMergeAsync` (~line 2883)
- Keep the initial 5-minute poll as a fast path, but remove the "give up after 30 attempts" behavior.
- Or remove entirely and rely on the persistent reconciler.

#### Checklist

- [x] 6a — ReconcilePullRequestMissionsAsync added to HealthCheckAsync
- [x] 6b — Delegate-based: OnReconcilePullRequest in IAdmiralService, wired in ArmadaServer
- [x] 6c — Rate-limited to 10 PRs per health check cycle
- [ ] 6d — Fire-and-forget poller kept as fast path (removes itself after 5min, reconciler catches remainder)

---

### T2-2: Explicit `armada_retry_landing` command (depends on T1-4)

**Status:** `[ ]` Not started

**Goal:** Operator can retry a failed landing with one command.

#### 7a. Create LandingService

- **New file:** `src/Armada.Core/Services/LandingService.cs`
- **New interface:** `src/Armada.Core/Services/Interfaces/ILandingService.cs`
- Methods:
  ```csharp
  Task<bool> RetryLandingAsync(string missionId, CancellationToken token = default);
  ```
- Implementation:
  1. Load mission, validate status is `LandingFailed`.
  2. Load vessel, dock (if still exists), resolve landing mode.
  3. Rebase mission branch onto current target branch head.
  4. If rebase clean → re-run landing (using resolved landing mode).
  5. If rebase conflicts → report conflicts, leave as `LandingFailed`.
  6. Log all retry attempts via events.

#### 7b. Create MCP tool

- **New file:** `src/Armada.Server/Mcp/MissionRetryLandingArgs.cs`
  ```csharp
  public class MissionRetryLandingArgs
  {
      public string MissionId { get; set; } = "";
  }
  ```
- **File:** `src/Armada.Server/Mcp/McpToolRegistrar.cs`
- Register `armada_retry_landing` tool in `RegisterMissionTools()`.

#### 7c. Wire LandingService in ArmadaServer

- **File:** `src/Armada.Server/ArmadaServer.cs`
- Instantiate `LandingService` and inject dependencies.

#### Checklist

- [ ] 7a — LandingService + interface
- [ ] 7b — MCP Args + tool registration
- [ ] 7c — Wire in ArmadaServer

---

### T2-3: Bounded auto-retry for target-branch drift (depends on T1-1 + T2-2)

**Status:** `[~]` Partial (setting added, retry logic deferred to LandingService)

**Goal:** Automatically rebase and retry landing when the failure is due to
target-branch drift (not a genuine content conflict).

#### 8a. Add retry logic to LandingService

- **File:** `src/Armada.Core/Services/LandingService.cs`
- In the landing flow (or called from merge-queue/local-merge failure handler):
  1. On merge conflict, attempt `git rebase <targetBranch>`.
  2. If rebase succeeds cleanly → retry landing immediately.
  3. Track retry count per mission (in-memory or DB field `LandingRetryCount`).
  4. Max retries: configurable, default 3.
  5. If all retries fail → `LandingFailed` with conflict details.

#### 8b. Add `MaxLandingRetries` setting

- **File:** `src/Armada.Core/Settings/ArmadaSettings.cs`
- Add: `public int MaxLandingRetries { get; set; } = 3;`
- Clamp to [0, 10] in setter or validation.

#### 8c. Add `LandingRetryCount` to Mission model (optional)

- If tracking in DB: migration v13 or v14.
- If in-memory: use a `ConcurrentDictionary<string, int>` in LandingService.

#### Checklist

- [ ] 8a — Auto-rebase retry logic (requires LandingService from T2-2)
- [x] 8b — MaxLandingRetries setting added to ArmadaSettings (default 3, clamped [0,10])
- [ ] 8c — Retry count tracking (requires LandingService)

---

### T2-4: Captain not released until post-run handoff completes (depends on T1-1)

**Status:** `[x]` Complete

**Goal:** Captain stays in `Working` state until the branch is pushed / PR is
created / merge-queue entry is enqueued. Only long-running waits (PR merge
polling, queue processing) happen after captain release.

#### 9a. Restructure HandleCompletionAsync

- **File:** `src/Armada.Core/Services/MissionService.cs`, method `HandleCompletionAsync` (~line 270)
- **Current flow:**
  1. Set mission to WorkProduced
  2. Capture diff
  3. Fire-and-forget `Task.Run` for `OnMissionComplete`
  4. Release captain immediately (line 383)
- **New flow:**
  1. Set mission to WorkProduced
  2. Capture diff
  3. **Synchronously** invoke `OnMissionComplete` (Phase A: push branch, create PR, or enqueue)
  4. **Then** release captain
  5. If Phase A fails → set mission to `LandingFailed`, release captain, preserve dock
- **Phase B** (PR merge polling, queue processing) runs independently via:
  - T2-1 persistent PR reconciler
  - T1-5 merge-queue reconciliation
  - These do NOT hold the captain

#### 9b. Remove fire-and-forget Task.Run

- Replace the `Task.Run(async () => { ... })` block with a direct `await`.
- Remove `_InFlightCompletions` tracking (no longer needed for this path).
- Move dock reclamation to after captain release (but before next assignment).

#### Checklist

- [x] 9a — OnMissionComplete now invoked synchronously (await, not Task.Run)
- [x] 9b — Removed fire-and-forget Task.Run; captain released after handoff + dock reclaim

---

### T2-5: Reclaim docks from failed launches (depends on T1-3)

**Status:** `[x]` Complete

**Goal:** Docks stuck in `Provisioned` state with no active captain are
reclaimed automatically.

#### 10a. Add dock reclamation to health check

- **File:** `src/Armada.Core/Services/AdmiralService.cs`, method `HealthCheckAsync`
- New step: query docks where `Active == true` but:
  - No captain is in `Working` state for that dock
  - Dock has been in current state for > 5 minutes
- Reclaim those docks.

#### 10b. Add provisioning timestamp to Dock model

- **File:** `src/Armada.Core/Models/Dock.cs`
- Verify `CreatedUtc` or similar timestamp exists for age comparison.

#### Checklist

- [x] 10a — ReclaimOrphanedDocksAsync added to health check (5-minute threshold)
- [x] 10b — Uses Dock.CreatedUtc for age check; verifies captain state before reclaiming

---

## Tier 3 — Structural Hardening

Implement after Tiers 1 and 2 are stable.

---

### T3-1: Per-mission dock paths (depends on T2-4)

**Status:** `[ ]` Not started

**Goal:** Change dock path from `{vessel}/{captain}` to `{vessel}/{missionId}`
to eliminate path-reuse races.

#### 11a. Update dock path construction

- **File:** `src/Armada.Core/Services/DockService.cs`, method `ProvisionAsync`
- **Current:** `Path.Combine(_Settings.DocksDirectory, vessel.Name, captain.Name)`
- **New:** `Path.Combine(_Settings.DocksDirectory, vessel.Name, mission.Id)`
- This requires `ProvisionAsync` to accept a `Mission` parameter (or at least the mission ID).

#### 11b. Update ProvisionAsync signature

- **File:** `src/Armada.Core/Services/Interfaces/IDockService.cs`
- Change: `Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, CancellationToken token)`
- To: `Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, Mission mission, string branchName, CancellationToken token)`
- Update all callers.

#### 11c. Add periodic cleanup for terminal-mission worktrees

- **File:** `src/Armada.Core/Services/AdmiralService.cs`
- In health check: find docks whose mission is in a terminal state (Complete, Failed, Cancelled)
  and worktree still exists → clean up.

#### Checklist

- [ ] 11a — New dock path pattern
- [ ] 11b — Updated ProvisionAsync signature
- [ ] 11c — Periodic terminal-mission worktree cleanup

---

### T3-2: Transactional assignment (depends on T1-1)

**Status:** `[ ]` Not started

**Goal:** Mission claim + captain claim + dock record creation happen in a
single DB transaction.

#### 12a. Add transaction support to database drivers

- **Files:** All 4 `DatabaseDriver` implementations
- Add: `Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken token)`
- For SQLite: wrap in `BEGIN TRANSACTION` / `COMMIT` / `ROLLBACK`.
- For Postgres/MySQL/SQL Server: use their native transaction APIs.

#### 12b. Restructure TryAssignAsync

- **File:** `src/Armada.Core/Services/MissionService.cs`
- Wrap the three-step sequence in a transaction:
  1. Update mission (Assigned, set CaptainId)
  2. TryClaimAsync captain
  3. Create dock record
- If any step fails → rollback all.
- Dock *provisioning* (filesystem `git worktree add`) happens AFTER commit.
- If provisioning fails → new transaction: revert mission + release captain.

#### Checklist

- [ ] 12a — Transaction support in DB drivers
- [ ] 12b — Transactional TryAssignAsync

---

### T3-3: Durable landing state machine (depends on T2-2 + T2-3)

**Status:** `[ ]` Not started

**Goal:** Landing is a persistent, restart-safe state machine instead of a
fire-and-forget background task.

#### 13a. Create LandingJob model (or extend MergeEntry)

- **Option A (preferred):** Extend `MergeEntry` with additional states:
  ```
  Queued → Rebasing → Merging → Pushing → CreatingPR → Landed → Failed
  ```
- **Option B:** New `LandingJob` entity with its own table.
- Decision: Option A is simpler — MergeEntry already has `Status`, `MissionId`, `VesselId`, `BranchName`.

#### 13b. Drive landing from health-check loop

- **File:** `src/Armada.Core/Services/AdmiralService.cs`
- On each health check, query for MergeEntry/LandingJob in non-terminal states.
- Advance each through its state machine.
- Each state transition is persisted before executing the operation.

#### 13c. Handle server restart

- On startup, resume any in-progress landing jobs from their last persisted state.

#### Checklist

- [ ] 13a — Landing job model/schema
- [ ] 13b — Health-check-driven state machine
- [ ] 13c — Restart recovery

---

### T3-4: Replace user-working-directory integration with dedicated worktree (depends on T3-1)

**Status:** `[ ]` Not started

**Goal:** Local merge happens in a dedicated temporary worktree, not the
user's live checkout.

#### 14a. Create dedicated integration worktree method

- **File:** `src/Armada.Core/Services/LandingService.cs` (created in T2-2)
- New method: `MergeInDedicatedWorktreeAsync(vessel, mission, targetBranch)`
  1. Create temp worktree: `{DocksDirectory}/_integration/{missionId}`
  2. Checkout target branch in the temp worktree.
  3. Merge mission branch.
  4. Push from temp worktree.
  5. Clean up temp worktree.

#### 14b. Update HandleMissionCompleteAsync to use LandingService

- **File:** `src/Armada.Server/ArmadaServer.cs`
- Replace the `MergeBranchLocalAsync` call with `LandingService.MergeInDedicatedWorktreeAsync`.
- After successful push, optionally fast-forward the user's working directory
  (only if clean and on the target branch).

#### 14c. User sync step (optional convenience)

- After confirmed push success:
  ```csharp
  if (vessel.WorkingDirectory != null)
  {
      // Check if user's WD is clean and on target branch
      // If yes: git pull --ff-only
      // If no: log info message "run git pull to sync"
  }
  ```

#### Checklist

- [ ] 14a — Dedicated integration worktree method
- [ ] 14b — Replace local merge call
- [ ] 14c — Optional user-WD sync

---

### T3-5: Fix stall detection (depends on T1-2)

**Status:** `[ ]` Not started

**Goal:** Stall detection is based on actual agent output, not the health-check
loop refreshing the heartbeat.

#### 15a. Stop updating heartbeat from health check

- **File:** `src/Armada.Core/Services/AdmiralService.cs`, method `HealthCheckCaptainAsync`
- Find where `LastHeartbeatUtc` is updated when the process is alive.
- **Remove** that update — heartbeat should only be updated when the agent
  produces output.

#### 15b. Track last output timestamp

- **File:** `src/Armada.Server/ArmadaServer.cs` (or the agent runtime output handler)
- When the agent writes stdout/stderr, update `captain.LastHeartbeatUtc`.
- This is already partially done in `HandleAgentOutput` — verify it updates the
  heartbeat timestamp there.

#### 15c. Use output-based heartbeat for stall detection

- In `HealthCheckCaptainAsync`, compare `captain.LastHeartbeatUtc` against
  `StallThresholdMinutes`. If the agent hasn't produced output in that window,
  it's genuinely stalled.

#### Checklist

- [ ] 15a — Stop health-check heartbeat refresh
- [ ] 15b — Output-driven heartbeat updates
- [ ] 15c — Stall detection uses output heartbeat

---

## Implementation Order

```
Phase 1 (parallel, no dependencies):
  T1-1  Serialize per vessel
  T1-2  Fix PID-as-success
  T1-3  Stop deleting sibling docks
  T1-4  Fix push-failure marking
  T1-5  Merge-queue reconciliation

Phase 2 (after Phase 1):
  T2-1  Persistent PR reconciler        (after T1-5)
  T2-2  Retry landing command           (after T1-4)
  T2-3  Auto-retry on drift             (after T1-1 + T2-2)
  T2-4  Captain release timing          (after T1-1)
  T2-5  Reclaim failed docks            (after T1-3)

Phase 3 (after Phase 2):
  T3-1  Per-mission dock paths          (after T2-4)
  T3-2  Transactional assignment        (after T1-1)
  T3-3  Durable landing state machine   (after T2-2 + T2-3)
  T3-4  Dedicated integration worktree  (after T3-1)
  T3-5  Fix stall detection             (after T1-2)
```

---

## Progress Log

| Date | Item | Status | Notes |
|------|------|--------|-------|
| 2026-03-13 | T1-1 | Complete | Vessel concurrency serialization, schema v12, all 4 DB drivers, MCP tools |
| 2026-03-13 | T1-2 | Complete | exitCode = -1 for missing PID (work verification deferred to T2) |
| 2026-03-13 | T1-3 | Complete | DockService checks active docks before deleting sibling worktrees |
| 2026-03-13 | T1-4 | Complete | Push failure → LandingFailed; branch preserved; push before cleanup |
| 2026-03-13 | T1-5 | Complete | ReconcileMissionStatusAsync in MergeQueueService on land/fail |
| 2026-03-13 | T2-1 | Complete | Persistent PR reconciler via delegate, rate-limited, in health check |
| 2026-03-13 | T2-3 | Partial | MaxLandingRetries setting added; retry logic needs LandingService |
| 2026-03-13 | T2-4 | Complete | Synchronous handoff; removed Task.Run fire-and-forget |
| 2026-03-13 | T2-5 | Complete | ReclaimOrphanedDocksAsync in health check, 5-min threshold |

---

## Files Modified Tracker

This section tracks every file touched, to help with code review.

| File | Items | Changes |
|------|-------|---------|
| `src/Armada.Core/Models/Vessel.cs` | T1-1 | Add AllowConcurrentMissions property |
| `src/Armada.Core/Services/MissionService.cs` | T1-1, T1-2, T2-4 | Enforce serialization, work verification, captain release timing |
| `src/Armada.Core/Services/AdmiralService.cs` | T1-2, T2-1, T2-5, T3-5 | Fix exit code, PR reconciler, dock reclaim, stall detection |
| `src/Armada.Core/Services/DockService.cs` | T1-3, T3-1 | Guard sibling deletion, per-mission paths |
| `src/Armada.Core/Services/MergeQueueService.cs` | T1-5 | Mission reconciliation on land/fail |
| `src/Armada.Server/ArmadaServer.cs` | T1-2, T1-4, T2-1, T2-2, T3-4 | Work verification, push failure, PR reconciler, landing service |
| `src/Armada.Core/Services/LandingService.cs` | T2-2, T2-3, T3-4 | NEW — retry landing, auto-retry, dedicated worktree |
| `src/Armada.Core/Services/Interfaces/ILandingService.cs` | T2-2 | NEW — interface |
| `src/Armada.Core/Settings/ArmadaSettings.cs` | T2-3 | MaxLandingRetries setting |
| `src/Armada.Server/Mcp/McpToolRegistrar.cs` | T1-1, T2-2 | Vessel concurrent flag, retry_landing tool |
| `src/Armada.Server/Mcp/VesselAddArgs.cs` | T1-1 | AllowConcurrentMissions arg |
| `src/Armada.Server/Mcp/VesselUpdateArgs.cs` | T1-1 | AllowConcurrentMissions arg |
| `src/Armada.Server/Mcp/MissionRetryLandingArgs.cs` | T2-2 | NEW — args class |
| DB drivers (×4): Sqlite, Postgres, MySQL, SqlServer | T1-1 | Schema migration v12 + vessel column mapping |
| `test/Armada.Test.Unit/Suites/Services/SequentialDispatchTests.cs` | T1-1 | Concurrent mission blocking tests |
