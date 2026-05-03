# Armada Code Context Pack

Goal: Remove RemoteTrigger LocalDaemon mode and process-spawn dead code while preserving RemoteFire behavior and tests.

This is repo discovery evidence from Armada's code index. Playbooks, vessel CLAUDE.md, and project CLAUDE.md rules win on conflict.

VesselId: vsl_moh0egsy_iVZPXzPcI6q
Commit: bb32fd425d652abfe059383dae75e6db532c5541
Freshness: Fresh
IndexedAtUtc: 2026-05-03T17:54:17.1794411Z

## Evidence

### archive/FAILURE_CASES_CODEX.md:161-240

- Language: markdown
- Score: 107
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 51158e86d0c6bd8f5936e8ad421f6641c358265a3db8a0ff5f670224ea2b7ddb
- Freshness: Fresh

```markdown
| D12 | Dispatch | No launch handler leaks dock | Same as above when `OnLaunchAgent` is null | Stale dock and stale branch | Same fix as D11 |
| G1 | Docking | Provisioning can delete active sibling worktrees | Vessel dock cleanup removes all other git dirs | Active mission worktree disappears | Only clean inactive, unreferenced docks |
| G2 | Docking | Fetch failure is tolerated | `DockService` continues with stale local state | Old base branch, later merge conflicts | Fail provisioning if fetch fails for active repos, or mark degraded explicitly |
| G3 | Docking | Fallback fetch may still leave stale refs | `GitService.FetchAsync` falls back to `git fetch origin` | Worktree created from stale base branch | Verify target ref freshness before worktree add |
| G4 | Docking | Worktree directory reuse creates races | Path is `{DocksDirectory}/{vessel}/{captain}` | New mission collides with previous mission cleanup | Use unique per-mission dock paths |
| G5 | Docking | Old branch evidence can be deleted before diagnosis | Provisioning deletes stale branch if it exists | Harder recovery and forensics | Preserve failed branches until explicitly purged |
| G6 | Docking | Partial cleanup f
...
```

### CURSOR.md:1-80

- Language: markdown
- Score: 101
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 03b3d5c159b1baf8a1df36a66da56d068a07b21aa042982eadc52da23491b03a
- Freshness: Fresh

```markdown
## Model Context
The following context was accumulated by AI agents during previous missions on this repository. Use this information to work more effectively.

## Test Framework

Armada uses a custom TestSuite framework, NOT xUnit. Key facts:
- Base class: `Armada.Test.Common.TestSuite` (test/Armada.Test.Common/TestSuite.cs)
- Test files live under: `test/Armada.Test.Unit/Suites/<area>/<Name>Tests.cs`
- Test project csproj: `test/Armada.Test.Unit/Test.Unit.csproj` (not `Armada.Test.Unit.csproj`)
- Run tests: `dotnet run --project test/Armada.Test.Unit/Test.Unit.csproj --framework net8.0`
- Register suites in `test/Armada.Test.Unit/Program.cs` via `runner.AddSuite(new MySuite())`
- Pattern: `public class FooTests : TestSuite`, `public override string Name => "Foo";`, `protected override async Task RunTestsAsync()`
- Each test: `await RunTest("MethodName_Condition_Expected", async () => { ... });`
- Assertion helpers: `AssertEqual(expected, actual, msg)`, `AssertNotNull(val, msg)`, `AssertNull(val, msg)`, `AssertContains(substring, container, msg)`, `AssertTrue(condition, msg)`, `AssertFalse(condition, msg)`
- No `Assert.IsType<T>()` -- use `AssertTrue(r is EvaluationResult.Fail, "m
...
```

### README.md:81-160

- Language: markdown
- Score: 100
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: f8de9b90f3f3c9b5245edaf12e56ccd4b62b115389b622bea1b60350d2aa282c
- Freshness: Fresh

```markdown
- **Voyage-to-mission playbook propagation.** Vessel `DefaultPlaybooks` merge into every dispatch automatically; voyage-level `selectedPlaybooks` cascade into each mission. Architect and `armada_create_mission` paths included. (`8143268`, `f52720a0`)
- **Per-stage `PreferredModel` on `PipelineStage`.** A Reviewed pipeline can run Worker stage on Mid-tier and Judge stage on `claude-opus-4-7` independently. Alias-aware dispatch path expands pipeline stages correctly. (`5ae0ce4b`, `fc48ea41`)
- **`ProtectedPaths` per-vessel gate.** Vessels carry a glob-list of paths that captain commits may NOT touch. Captain commits to `**/CLAUDE.md` (or other protected paths) are rejected with a coaching message teaching the `[CLAUDE.MD-PROPOSAL]` block format for proposing rule changes. (`02e52f6`)
- **Cursor-agent prompt via stdin.** Cursor runtime feeds the prompt to `cursor-agent` via stdin instead of inlining as a CLI arg. Bypasses Windows `cmd.exe`'s ~8 KB command-line limit which silently failed cursor-agent on long structured briefs. (`db9439c`)
- **`GitService.IsPrMergedAsync` platform-aware.** PR-merge detection routes to `gh pr view` (GitHub) or `glab mr view` (GitLab) based on URL host.
...
```

### test/Armada.Test.Unit/Suites/Services/RemoteTriggerServiceLocalDaemonModeTests.cs:1-80

- Language: csharp
- Score: 99
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: a327c57341428feb62ee28f3c5b09b86ba618e9d7545f48ef0d70f3bd1ef3e36
- Freshness: Fresh

```csharp
namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    public class RemoteTriggerServiceLocalDaemonModeTests : TestSuite
    {
        public override string Name => "RemoteTrigger Service LocalDaemon Mode";

        protected override async Task RunTestsAsync()
        {
            await RunTest("FireDrainer_LocalDaemonMode_CallsSpawnerNotHttp", async () =>
            {
                RecordingProcessHost processHost = new RecordingProcessHost();
                RecordingHttpClient http = new RecordingHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeLocalDaemonSettings(), http, processHost, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-ld-1", "WorkProduced: mission m1");

                AssertEqual(0, http.CallCount, "LocalDaemon mode should not call HTTP client");
                AssertEqua
...
```

### archive/CODEX_RESPONSE.md:81-160

- Language: markdown
- Score: 87
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 3e3e120b9579dfde7abfa740194b287e1ccd14fbae1e9de9e6fe8a2395a358d2
- Freshness: Fresh

```markdown
### Task 5: DockService.ReclaimAsync Idempotency

**Feedback concern:** Both `MissionService` (background finalizer) and `ArmadaServer` (`HandleMissionCompleteAsync`) can call `ReclaimAsync` for the same dock, leading to duplicate worktree removal attempts and spurious error logs.

**What was done:**

- Added an idempotency guard at the top of `DockService.ReclaimAsync`: if the dock is already inactive (`Active == false`), the method returns immediately with a debug log.
- This makes double-reclaim a safe no-op.

**Why:** Simple, surgical fix for a real race condition. The guard prevents redundant filesystem operations and eliminates confusing warning logs.

---

### Task 6: Integration Tests for the Landing Pipeline

**Feedback concern:** The landing pipeline had no dedicated test coverage. Changes to status transitions, landing modes, or branch cleanup could silently break the pipeline.

**What was done:**

- Created `LandingPipelineTests.cs` with 11 integration-style tests:
  - `WorkProduced` flow from `HandleCompletionAsync`
  - Local merge call sequence verification
  - Merge failure sets `LandingFailed`
  - `LandingMode` persistence (vessel and voyage)
  - Null `LandingMode`
...
```

### test/Armada.Test.Unit/Program.cs:81-150

- Language: csharp
- Score: 87
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: be81d3347d27a154c64dc50b4522109b2af74ace911da5319249c8221c894863
- Freshness: Fresh

```csharp
runner.AddSuite(new ProxyRegistryTests());
            runner.AddSuite(new RemoteControlQueryServiceTests());
            runner.AddSuite(new RemoteControlManagementServiceTests());
            runner.AddSuite(new CaptainServiceTests());
            runner.AddSuite(new AgentLifecycleHandlerTests());
            runner.AddSuite(new PlanningSessionCoordinatorTests());
            runner.AddSuite(new MissionPromptTests());
            runner.AddSuite(new SequentialDispatchTests());
            runner.AddSuite(new MissionStatusTransitionTests());
            runner.AddSuite(new LandingPipelineTests());
            runner.AddSuite(new SessionTokenServiceTests());
            runner.AddSuite(new AuthenticationServiceTests());
            runner.AddSuite(new AuthorizationConfigTests());
            runner.AddSuite(new AuthorizationServiceTests());
            runner.AddSuite(new AuthEndpointTests());
            runner.AddSuite(new PromptTemplateServiceTests());
            runner.AddSuite(new PromptSignalConsistencyTests());
            runner.AddSuite(new PersonaPipelineDbTests());
            runner.AddSuite(new PipelineDispatchTests());
            runner.AddSuite(new PerStagePreferre
...
```

### test/Armada.Test.Unit/Suites/Services/LocalDaemonSettingsTests.cs:1-80

- Language: csharp
- Score: 86
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 7d3003dcad0fe499ec72a952cbfba6293706915770502c63ef17f01df5c22622
- Freshness: Fresh

```csharp
namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    public class LocalDaemonSettingsTests : TestSuite
    {
        public override string Name => "LocalDaemon Settings";

        protected override async Task RunTestsAsync()
        {
            await RunTest("IsLocalDaemonConfigured_NullLocalDaemon_ReturnsFalse", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.LocalDaemon,
                    LocalDaemon = null,
                };
                AssertFalse(s.IsLocalDaemonConfigured(), "null LocalDaemon block should return false");
                return Task.CompletedTask;
            });

            await RunTest("IsLocalDaemonConfigured_EmptyCommand_ReturnsFalse", () =>
            {
                RemoteTriggerSettings s = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.LocalDaemon,
                    LocalDaemon = new LocalDaemonSettings { Command = "" },
...
```

### CHANGELOG.md:1-80

- Language: markdown
- Score: 83
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 745e08257a3aeba50c731467459ffe8efe1d5d1c93ab9f8ea2bd5a689bb0d544
- Freshness: Fresh

```markdown
# Changelog

All notable changes to Armada are documented in this file.

---

## v0.7.0

Focus: remote access.

### Remote Access
- Added an experimental outbound remote-control tunnel foundation in `Armada.Server`
- New `RemoteControl` settings are persisted in `settings.json` and exposed through `GET/PUT /api/v1/settings`
- Health and status responses now expose `RemoteTunnel` telemetry including state, instance ID, latency, and last error
- React dashboard, legacy dashboard, and `armada status` now surface remote tunnel configuration and live state
- Added request/response handling and server event forwarding on the tunnel contract
- Added `Armada.Proxy` with websocket tunnel termination, instance summaries, recent-event inspection, and live `armada.status.snapshot` / `armada.status.health` forwarding
- Added focused tunnel-backed remote inspection routes for recent activity, missions, voyages, captains, logs, and diffs
- Added bounded tunnel-backed management routes for fleets, vessels, voyages, missions, and captain stop
- Added a proxy-hosted remote operations shell at `/` for mobile-first remote triage, fleet and vessel management, voyage dispatch, mission editing, and capta
...
```

### FAST_TRACK_SETUP.md:161-233

- Language: markdown
- Score: 77
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 20f65b56cb704f4d847d25dbe7f0f4085e84d2f4bfc886c129c68fdb704ae76b
- Freshness: Fresh

```markdown
When dispatched to a vessel, follow that vessel's StyleGuide and ProjectContext exactly. Reference fleet-wide playbooks for cross-cutting standards:
- CODE_STYLE — mandatory C# style rules
- REPOSITORY_REQUIREMENTS — required filesystem layout
- AUTHENTICATION — multi-tenant AAA / RBAC reference
- BACKEND_ARCHITECTURE — Watson 7 + provider-neutral DB + typed routes
- BACKEND_TEST_ARCHITECTURE — Touchstone descriptor pattern
- FRONTEND_ARCHITECTURE — React 19 / Vite 6 / fetch-based ApiClient
- I18N — locale registry, formatters, RTL/CJK layout

Compile clean (no errors, no warnings) before reporting work complete. Prefer existing patterns in the codebase over introducing new abstractions. When SQL is hand-written it is deliberate — do not silently rewrite to ORM helpers. Match the vessel's existing conventions (private field naming, region structure, async signatures) before introducing your own.
```

### Per-runtime defaults to consider

- **Claude Code** — model `claude-opus-4-7` for heavy work, `claude-sonnet-4-6` for cheaper passes, `claude-haiku-4-5-20251001` for fast triage. Multiple captains is fine: e.g. one named "Claude Code (Opus)" and another "Claude Code (Sonnet, fast)"
...
```

### test/Armada.Test.Unit/Suites/Services/RemoteTriggerServiceLocalDaemonModeTests.cs:81-157

- Language: csharp
- Score: 75
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: a327c57341428feb62ee28f3c5b09b86ba618e9d7545f48ef0d70f3bd1ef3e36
- Freshness: Fresh

```csharp
RecordingHttpClient http = new RecordingHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeLocalDaemonSettings(), http, processHost, new LoggingModule(), TimeSpan.Zero);

                await service.FireCriticalAsync("audit critical finding");

                AssertEqual(0, http.CallCount, "LocalDaemon critical should not call HTTP client");
                AssertEqual(1, processHost.SpawnCallCount, "LocalDaemon critical should call spawner once");
            });

            await RunTest("FireDrainer_LocalDaemonMode_CoalescingStillApplies", async () =>
            {
                RecordingProcessHost processHost = new RecordingProcessHost();
                RecordingHttpClient http = new RecordingHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeLocalDaemonSettings(), http, processHost, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-coal-1", "first event");
                await service.FireDrainerAsync("vessel-coal-1", "second event within 60s");

                AssertEqual(1, processHost.SpawnCallCount, "second call for same vessel within 60s shoul
...
```

### test/Armada.Test.Unit/Program.cs:1-80

- Language: csharp
- Score: 74
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: be81d3347d27a154c64dc50b4522109b2af74ace911da5319249c8221c894863
- Freshness: Fresh

```csharp
namespace Armada.Test.Unit
{
    using Armada.Test.Common;
    using Armada.Test.Unit.Suites.Database;
    using Armada.Test.Unit.Suites.Models;
    using Armada.Test.Unit.Suites.Recovery;
    using Armada.Test.Unit.Suites.Routes;
    using Armada.Test.Unit.Suites.Services;

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            bool noCleanup = args.Contains("--no-cleanup");

            TestRunner runner = new TestRunner("ARMADA UNIT TEST SUITE");

            // Database tests
            runner.AddSuite(new FleetDatabaseTests());
            runner.AddSuite(new VesselDatabaseTests());
            runner.AddSuite(new VesselTests());
            runner.AddSuite(new CaptainDatabaseTests());
            runner.AddSuite(new CaptainTests());
            runner.AddSuite(new MissionDatabaseTests());
            runner.AddSuite(new VoyageDatabaseTests());
            runner.AddSuite(new DockDatabaseTests());
            runner.AddSuite(new SignalDatabaseTests());
            runner.AddSuite(new EventDatabaseTests());
            runner.AddSuite(new EventTests());
            runner.AddSuite(new EnumerationTests());
            runn
...
```

### archive/CODEX_RESPONSE.md:161-240

- Language: markdown
- Score: 64
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 3e3e120b9579dfde7abfa740194b287e1ccd14fbae1e9de9e6fe8a2395a358d2
- Freshness: Fresh

```markdown
**Our assessment: Codex is correct. This is a legitimate semantic leak.**

The mission's database status is correct (`PullRequestOpen`), so there is no data corruption or state machine bug. But WebSocket listeners and event consumers see both `mission.pull_request_open` and `mission.work_produced` for the same mission in the same completion cycle. That is confusing for dashboards, automation hooks, and anyone building on the event stream.

The fix is straightforward: the final broadcast block should derive the event type from `mission.Status` rather than from the two boolean flags. If the status is already `PullRequestOpen`, the broadcast should emit `mission.pull_request_open` (or skip the broadcast entirely since it was already emitted). This is a small change with clear correctness improvement.

#### Priority 4: Deeper Landing-Flow Tests

**Codex's claim:** The `LandingPipelineTests` are useful but do not exercise the full `ArmadaServer.HandleMissionCompleteAsync` landing logic. The highest-risk code paths (PR open to complete, merge queue behavior, branch cleanup, final event emission) are only partially covered.

**Our assessment: Codex is correct that there is a coverage gap,
...
```

### archive/CODEX_RESPONSE.md:241-320

- Language: markdown
- Score: 63
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 3e3e120b9579dfde7abfa740194b287e1ccd14fbae1e9de9e6fe8a2395a358d2
- Freshness: Fresh

```markdown
**Our assessment: This recommendation would produce worse test quality, not better.**

`ArmadaServer` is the composition root of the entire system. `HandleMissionCompleteAsync` alone orchestrates:

- Database reads and writes (missions, captains, docks, vessels, voyages, events)
- Git operations (diff, merge, push, branch delete, worktree remove)
- GitHub API calls (PR creation, PR status polling)
- WebSocket broadcasts
- HTTP client calls (notifications)
- Merge queue service interactions
- Captain lifecycle management

Testing this method at the unit level requires mocking or stubbing **all** of these dependencies. The result is a test that:

- Is extremely brittle — any refactor to the method's internal structure breaks the test, even if behavior is preserved.
- Tests mock wiring rather than actual behavior — "verify that `MockGitService.MergeLocal` was called with these arguments" does not prove the merge actually works.
- Provides false confidence — a passing test means the mocks were set up correctly, not that the system works correctly.
- Is expensive to maintain — every new dependency or internal restructuring requires updating the mock setup across multiple tests.

This is
...
```

### archive/CODEX_RESPONSE.md:1-80

- Language: markdown
- Score: 61
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 3e3e120b9579dfde7abfa740194b287e1ccd14fbae1e9de9e6fe8a2395a358d2
- Freshness: Fresh

```markdown
# CODEX_FEEDBACK.md Response

This document details what was implemented in response to the original feedback in `CODEX_FEEDBACK.md`, the rationale behind each change, justification for anything that was not done, and a thorough assessment of the second-round feedback.

---

## Part 1: What Was Implemented

Six tasks were implemented sequentially, each committed, pushed, and verified before proceeding to the next.

---

### Task 1: PullRequestOpen Mission Status

**Feedback concern:** When Armada creates a PR for a completed mission, the mission is marked `Complete` immediately — but the code hasn't actually landed yet. If the PR fails to merge, the mission is incorrectly reported as complete.

**What was done:**

- Added `PullRequestOpen` to `MissionStatusEnum` (value inserted between `WorkProduced` and `Testing`).
- Updated the PR creation flow in `ArmadaServer.cs` to set `PullRequestOpen` instead of `Complete` when a PR is created. The mission emits a `mission.pull_request_open` event and broadcasts via WebSocket.
- Updated `PollAndPullAfterMergeAsync` to transition from `PullRequestOpen` to `Complete` only when the PR merge is confirmed.
- Added valid status transitions: `WorkP
...
```

### docs/MERGING.md:1-80

- Language: markdown
- Score: 60
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 80d6f9ba9228c4f82614a57e426dbc7949697a05667140b840b5f47a8eb572a2
- Freshness: Fresh

```markdown
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
- **Cancelled** -- manually remove
...
```

### src/Armada.Server/AgentLifecycleHandler.cs:641-720

- Language: csharp
- Score: 57
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 20706aecafff6f2d155767f319f79951878f2e7eb46937620fea528502cfb375
- Freshness: Fresh

```csharp
_Logging.Info(_Header + "mission " + mission.Id + " transitioned to " + signal.MissionStatus.Value + " via agent signal");
                        }
                    }

                    Signal dbSignal = new Signal(SignalTypeEnum.Progress, "[" + signal.Type + "] " + signal.Value);
                    dbSignal.FromCaptainId = capturedCaptainId;
                    await _Database.Signals.CreateAsync(dbSignal).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error processing progress signal: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Handle agent process exit event.
        /// </summary>
        public void HandleAgentProcessExited(int processId, int? exitCode)
        {
            StopProcessLivenessHeartbeat(processId);

            string? captainId = null;
            string? missionId = null;
            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain.TryGetValue(processId, out captainId);
                _ProcessToMission.TryGetValue(processId, out missionId);
            }

            // The process e
...
```

### test/Armada.Test.Unit/Suites/Services/ProcessHostTests.cs:1-80

- Language: csharp
- Score: 57
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: e41ffa1104a1ed199ae95b01a28207bc4362d0138d478227085c54c78f9ca2ae
- Freshness: Fresh

```csharp
namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using SyslogLogging;

    public class ProcessHostTests : TestSuite
    {
        public override string Name => "Process Host";

        protected override async Task RunTestsAsync()
        {
            await RunTest("SpawnDetachedAsync_ValidCommand_ReturnsPid", async () =>
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ProcessHost host = new ProcessHost(logging);

                // Use cmd /c exit 0 on Windows -- quick exit, no output
                ProcessSpawnRequest req = new ProcessSpawnRequest
                {
                    Command = "cmd",
                    Args = "/c exit 0",
                    StdinPayload = "",
                    TimeoutSeconds = 10,
                };

                ProcessSpawnResult result = await host.SpawnDetachedAsync(req, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.ProcessId > 0
...
```

### test/Armada.Test.Unit/Suites/Services/AutoLandLandingHandlerTests.cs:1-80

- Language: csharp
- Score: 56
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: cfee9de4136ddfb7293e93e17e38714f540f10d079c2b2227986c3e3814f9726
- Freshness: Fresh

```csharp
namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the auto-land predicate evaluation and ProcessEntryByIdAsync fire decision.
    /// Exercises the predicate-evaluation and background-fire branch directly via AutoLandEvaluator
    /// and a hand-rolled IMergeQueueService recording double.
    /// </summary>
    public class AutoLandLandingHandlerTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "AutoLand LandingHandler";

        /// <summary>
        /// Hand-rolled IMergeQueueService double that records ProcessEntryByIdAsync calls.
        /// </summary>
        private sealed class RecordingMergeQueueService : IMergeQueueService
        {
            public List<string> ProcessedEntryIds { get; } = new List<string>();

            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default)
            {
                ProcessedEntryIds
...
```

### src/Armada.Core/Services/ProcessHost.cs:1-80

- Language: csharp
- Score: 55
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 013fc1ff323db891629bceeca88f52f005af3b75a7547e4e0807b98a01d8fd73
- Freshness: Fresh

```csharp
namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Production IProcessHost implementation. Spawns a detached subprocess, writes the
    /// stdin payload, and returns immediately. A background task monitors the process and
    /// disposes it when it exits or the timeout elapses.
    /// </summary>
    public sealed class ProcessHost : IProcessHost
    {
        private readonly LoggingModule _Logging;
        private const string _Header = "[ProcessHost] ";

        /// <summary>Constructs the host with the supplied logging module.</summary>
        public ProcessHost(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc/>
        public Task<ProcessSpawnResult> SpawnDetachedAsync(ProcessSpawnRequest request, CancellationToken token)
        {
            ProcessStartInfo psi = BuildStartInfo(request);
            Process process = Pr
...
```

### src/Armada.Core/Settings/RemoteTriggerSettings.cs:1-80

- Language: csharp
- Score: 55
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: ed7c22b0881cb6967b07e28f16632156609fe276122b03f60875566a2156807c
- Freshness: Fresh

```csharp
namespace Armada.Core.Settings
{
    using System.Collections.Generic;

    /// <summary>Transport mode for RemoteTriggerService.</summary>
    public enum RemoteTriggerMode
    {
        /// <summary>All wake calls are no-ops. Explicit opt-out regardless of Enabled flag.</summary>
        Disabled,

        /// <summary>HTTP POST to Claude Code Routines /fire endpoint. Default for backward compatibility.</summary>
        RemoteFire,

        /// <summary>Spawn a local subprocess (e.g. claude CLI) with event text piped to stdin.</summary>
        LocalDaemon,
    }

    /// <summary>Settings for spawning a local daemon process in <see cref="RemoteTriggerMode.LocalDaemon"/> mode.</summary>
    public sealed class LocalDaemonSettings
    {
        /// <summary>Executable to run (e.g. "claude"). Required for LocalDaemon mode.</summary>
        public string? Command { get; set; }

        /// <summary>Command-line arguments appended after the executable (e.g. "--dangerously-skip-permissions --print").</summary>
        public string Args { get; set; } = "";

        /// <summary>Orchestrator system prompt prepended before the event text. The event text is appended after a blank line.
...
```

### test/Armada.Test.Unit/Suites/Services/AgentLifecycleHandlerTests.cs:1-80

- Language: csharp
- Score: 54
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: e781f64be2995beba9c84d2771f32c98be5a72fdb30e058692f6c1ed38f1a369
- Freshness: Fresh

```csharp
namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for runtime model validation and launch passthrough in AgentLifecycleHandler.
    /// </summary>
    public class AgentLifecycleHandlerTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Agent Lifecycle Handler";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ValidateModelAsync returns null and forwards model to runtime", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorShimS
...
```

### src/Armada.Helm/Program.cs:161-228

- Language: csharp
- Score: 53
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: bbce38cd55246322a81efa946161dfda29712bd3677b8832ac90d5cdf7ac9342
- Freshness: Fresh

```csharp
config.AddBranch("captain", captain =>
                {
                    captain.SetDescription("Manage captains (agents)");
                    captain.AddCommand<CaptainListCommand>("list")
                        .WithDescription("List all captains");
                    captain.AddCommand<CaptainAddCommand>("add")
                        .WithDescription("Recruit a new captain");
                    captain.AddCommand<CaptainUpdateCommand>("update")
                        .WithDescription("Update an existing captain");
                    captain.AddCommand<CaptainStopCommand>("stop")
                        .WithDescription("Recall a captain (accepts name or ID)");
                    captain.AddCommand<CaptainRemoveCommand>("remove")
                        .WithDescription("Remove a captain (accepts name or ID)");
                    captain.AddCommand<CaptainStopAllCommand>("stop-all")
                        .WithDescription("Emergency recall all captains");
                });

                config.AddBranch("fleet", fleet =>
                {
                    fleet.SetDescription("Manage fleets (groups of repos)");
                    fleet.AddCommand<FleetList
...
```

### test/Armada.Test.Unit/Suites/Services/LandingPipelineTests.cs:161-240

- Language: csharp
- Score: 53
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 9ff1f31f49853df1f51ed45d5ba63a23322149d14f50c0399c2f93797a78ee8d
- Freshness: Fresh

```csharp
Mission? wp = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionStatusEnum.WorkProduced, wp!.Status, "Should be WorkProduced before landing attempt");
                }
            });

            // === Vessel Landing Mode Resolution ===

            await RunTest("Vessel LandingMode is persisted and read correctly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Vessel vessel = new Vessel("mode-test", "https://github.com/test/repo.git");
                    vessel.LandingMode = LandingModeEnum.PullRequest;
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalAndRemote;
                    await testDb.Driver.Vessels.CreateAsync(vessel);

                    Vessel? read = await testDb.Driver.Vessels.ReadAsync(vessel.Id);
                    AssertNotNull(read, "Vessel should exist");
                    AssertEqual(LandingModeEnum.PullRequest, read!.LandingMode, "LandingMode should be PullRequest");
                    AssertEqual(BranchCleanupPolicyEnum.LocalAndRemote, read.BranchCleanupPolicy, "BranchCle
...
```

### src/Armada.Server/MissionLandingHandler.cs:481-560

- Language: csharp
- Score: 52
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: ebb73e5d7a4f0e08fd7792cbe91ce6b62e819a8b25cb97a7a4db618a68d92d38
- Freshness: Fresh

```csharp
}
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "error merging locally for mission " + mission.Id + ": " + ex.Message + " -- branch " + dock.BranchName + " is still available in the bare repo");
                        landingSucceeded = false;
                        landingFailureReason = "Error merging locally: " + ex.Message;
                    }
                    } // end hasChanges else
                }
                else if (landingModeIsMergeQueue)
                {
                    // MergeQueue mode: auto-enqueue the branch into the merge queue.
                    // Processing (test-and-land) remains a separate trigger via armada_process_merge_queue.
                    try
                    {
                        string targetBranch = vessel?.DefaultBranch ?? "main";
                        MergeEntry entry = new MergeEntry(dock.BranchName, targetBranch);
                        entry.MissionId = mission.Id;
                        entry.VesselId = mission.VesselId;
                        entry = await _MergeQueue.EnqueueAsync(entry).ConfigureAwait(false);
                        _Logging.I
...
```

### src/Armada.Core/Services/RemoteTriggerService.cs:1-80

- Language: csharp
- Score: 51
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: bb66ed4aa0c556a4259b687790e91bb160d2a18d4600e9c9ba059082ebd0b9c5
- Freshness: Fresh

```csharp
namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Orchestrates admiral-side orchestrator wakes. Supports two transport modes:
    /// RemoteFire (HTTP POST to Claude Code Routines /fire) and LocalDaemon (subprocess spawn).
    /// Per-vessel 60s coalescing, rolling 20/hour throttle, retry-once-on-retriable-failure,
    /// and 3-strike consecutive-failure tracking apply to both modes.
    /// Critical events bypass coalescing and throttle.
    /// </summary>
    public sealed class RemoteTriggerService : IRemoteTriggerService
    {
        private const int CoalesceWindowSeconds = 60;
        private const int ThrottleCapPerHour = 20;
        private const int ConsecutiveFailureCap = 3;
        private const int ThrottleNotificationDebounceMinutes = 10;

        private static readonly TimeSpan _DefaultRetryDelay = TimeSpan.FromSeconds(2);

        private readonly RemoteTriggerSettings _Settings;
        private readonly IRemot
...
```

### src/Armada.Helm/Program.cs:81-160

- Language: csharp
- Score: 51
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: bbce38cd55246322a81efa946161dfda29712bd3677b8832ac90d5cdf7ac9342
- Freshness: Fresh

```csharp
.WithExample("status", "--all");

                config.AddCommand<WatchCommand>("watch")
                    .WithDescription("Live-updating status dashboard")
                    .WithExample("watch")
                    .WithExample("watch", "--interval", "2");

                config.AddCommand<LogCommand>("log")
                    .WithDescription("Tail a captain's output log")
                    .WithExample("log", "captain-1")
                    .WithExample("log", "captain-1", "--follow");

                config.AddCommand<DiffCommand>("diff")
                    .WithDescription("Show diff of a mission's changes")
                    .WithExample("diff", "msn_abc123")
                    .WithExample("diff", "\"Add validation\"");

                config.AddCommand<DoctorCommand>("doctor")
                    .WithDescription("Check system health and report issues");

                config.AddCommand<ResetCommand>("reset")
                    .WithDescription("Destructively reset all Armada data back to zero");

                // --- Entity management ---
                config.AddBranch("mission", mission =>
                {
                    mission.SetDescript
...
```

### test/Armada.Test.Unit/Suites/Services/RemoteTriggerEventHookTests.cs:161-240

- Language: csharp
- Score: 51
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 1733081817eda90a6ee03732ad014e94305b6917db15f09721f5f9bf7852894c
- Freshness: Fresh

```csharp
#endregion

        /// <summary>Run all remote trigger event hook tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MissionLandingHandler_WorkProduced_FiresDrainer", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingRemoteTriggerService recording = new RecordingRemoteTriggerService();
                    StubGitService git = new StubGitService();

                    (MissionLandingHandler handler, Mission mission, Dock dock, Vessel vessel) =
                        await CreateHandlerAsync(testDb, recording, git, LandingModeEnum.MergeQueue).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(recording.DrainerCalls.Count >= 1, "Expected at least one drainer fire for WorkProduced");
                    bool foundWorkProduced = false;
                    foreach ((string vid, string txt) in recording.DrainerCalls)
                    {
                        if (txt.Contains("WorkProduced
...
```

### archive/PLAYBOOKS.md:561-640

- Language: markdown
- Score: 50
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 4a4c273919fc81e0d82e0f63f1b7b9d21e355983b0da28fa950bfada283a4b11
- Freshness: Fresh

```markdown
Recommended v1 syntax:
  - repeated `--playbook <id>:inline`
  - repeated `--playbook <id>:reference`
  - repeated `--playbook <id>:worktree`

- [ ] Decide whether CLI should accept filenames and resolve them server-side or require IDs.
  Recommended for v1: accept IDs; optional future enhancement for filename lookup.

- [ ] Update command help text and examples.

## Phase 8: Tests And Verification

### 8.1 Unit tests

- [ ] Add unit tests for playbook validation.
  Minimum cases:
  - valid `.md` filename
  - invalid extension
  - duplicate filename in tenant
  - empty content

- [ ] Add unit tests for selection ordering and combined markdown rendering.

- [ ] Add unit tests for delivery mode resolution.
  Minimum cases:
  - playbook default mode used when no override is supplied
  - voyage/mission override mode replaces default
  - invalid delivery mode is rejected

- [ ] Add unit tests for mission snapshot creation.

- [ ] Add unit tests for prompt generation to verify all three delivery modes render correctly.

- [ ] Add unit tests for worktree materialization paths and git-safe ignore behavior.

- [ ] Add unit tests for restart/retry semantics and inheritance behavior.

### 8.2
...
```

### test/Armada.Test.Unit/Suites/Services/RemoteTriggerHttpClientTests.cs:1-80

- Language: csharp
- Score: 49
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 4d205d9876c5e6949b207c753b51270e077e3045c284eddb656e96334c2a94eb
- Freshness: Fresh

```csharp
namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using SyslogLogging;

    public class RemoteTriggerHttpClientTests : TestSuite
    {
        public override string Name => "RemoteTrigger HttpClient";

        protected override async Task RunTestsAsync()
        {
            await RunTest("FireAsync_2xxResponse_ReturnsSuccess_WithSessionUrl", async () =>
            {
                string responseJson = "{\"type\":\"routine_fire\",\"claude_code_session_id\":\"sess_123\",\"claude_code_session_url\":\"https://claude.ai/code/sessions/sess_123\"}";
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, responseJson);
                HttpClient http = new HttpClient(handler);
                LoggingModule logging = new LoggingModule();
                RemoteTriggerHttpClient client = new RemoteTriggerHttpClient(logging, http);

                FireRequest req = Make
...
```

### archive/CODEX_FEEDBACK.md:401-480

- Language: markdown
- Score: 48
- Commit: bb32fd425d652abfe059383dae75e6db532c5541
- ContentHash: 50b98fcd259d7d86bbee6f8fd66c37fa6dd4f54bf73c3a96a821e49d71b9e7c2
- Freshness: Fresh

```markdown
This remains from the prior review and is still true.

`DockService.ProvisionAsync` still deletes non-current captain directories based on directory heuristics:

- [`src/Armada.Core/Services/DockService.cs:93`](src/Armada.Core/Services/DockService.cs:93)

It is probably fine in normal operation, and after reading the response I would treat this as a low-priority refinement rather than an actionable near-term concern. I do not see enough evidence in the source to call it a pressing problem.

## Branching / Merging / Cleanup Assessment

### Is Armada doing the right thing with code after a mission completes?

**In the normal local-merge and PR paths, mostly yes now. In merge-queue mode, not fully yet.**

More explicit answer:

- `LandingMode.LocalMerge`: mostly correct
- `LandingMode.PullRequest`: mostly correct in state progression, but still has an event-stream leak
- `LandingMode.MergeQueue`: not complete enough to deserve the name yet
- `LandingMode.None`: behavior is internally consistent

### What is now clearly correct or much improved

- successful agent exit no longer means immediate mission completion
- PRs now sit in `PullRequestOpen` until merge confirmation
- local merge
...
```
