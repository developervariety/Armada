namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;

    /// <summary>
    /// Integration-style tests for the landing pipeline:
    /// WorkProduced -> local merge -> Complete (success) or LandingFailed (failure).
    /// Uses StubGitService so no real git operations occur, but exercises the full
    /// MissionService -> HandleMissionComplete -> landing -> status transition flow.
    /// </summary>
    public class LandingPipelineTests : TestSuite
    {
        public override string Name => "Landing Pipeline";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private async Task<LandingTestEntitiesResult> CreateTestEntitiesAsync(
            SqliteDatabaseDriver db, LandingModeEnum? landingMode = null, BranchCleanupPolicyEnum? cleanupPolicy = null, string? tenantId = null, string? userId = null, string nameSuffix = "")
        {
            Vessel vessel = new Vessel("test-vessel" + nameSuffix, "https://github.com/test/repo.git");
            vessel.TenantId = tenantId;
            vessel.UserId = userId;
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            vessel.LandingMode = landingMode;
            vessel.BranchCleanupPolicy = cleanupPolicy;
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Captain captain = new Captain("test-captain" + nameSuffix);
            captain.State = CaptainStateEnum.Working;
            await db.Captains.CreateAsync(captain).ConfigureAwait(false);

            Dock dock = new Dock(vessel.Id);
            dock.CaptainId = captain.Id;
            dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
            dock.BranchName = "armada/test-captain/msn_test123";
            dock.Active = true;
            await db.Docks.CreateAsync(dock).ConfigureAwait(false);

            Mission mission = new Mission("Test local merge mission");
            mission.Status = MissionStatusEnum.InProgress;
            mission.CaptainId = captain.Id;
            mission.DockId = dock.Id;
            mission.VesselId = vessel.Id;
            await db.Missions.CreateAsync(mission).ConfigureAwait(false);

            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = dock.Id;
            await db.Captains.UpdateAsync(captain).ConfigureAwait(false);

            return new LandingTestEntitiesResult(captain, mission, dock, vessel);
        }

        protected override async Task RunTestsAsync()
        {
            // === Local Merge Happy Path ===

            await RunTest("Local merge success produces correct git call sequence", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                    IMessageTemplateService templateService = new MessageTemplateService(logging);
                    MissionLandingHandler handler = new MissionLandingHandler(
                        logging,
                        testDb.Driver,
                        settings,
                        git,
                        new StubMergeQueueService(),
                        landingService,
                        new AutoLandEvaluator(),
                        new ConventionChecker(),
                        new CriticalTriggerEvaluator(),
                        templateService,
                        null,
                        dockService,
                        new NoOpRemoteTriggerService(),
                        null);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.LocalMerge);
                    Captain captain = entities.Captain;
                    Mission mission = entities.Mission;
                    Vessel vessel = entities.Vessel;
                    string integrationWorktree = Path.Combine(settings.DocksDirectory, "_integration", mission.Id);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("direct landing voyage")
                    {
                        Status = VoyageStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    mission.VoyageId = voyage.Id;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    LandingTestEntitiesResult sibling = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.LocalMerge, nameSuffix: "-sibling");
                    sibling.Mission.VoyageId = voyage.Id;
                    await testDb.Driver.Missions.UpdateAsync(sibling.Mission).ConfigureAwait(false);
                    TaskCompletionSource<bool> siblingEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    TaskCompletionSource<bool> finishSibling = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int completedVoyages = 0;
                    bool? reentryWrote = null;
                    missionService.OnVoyageComplete = async completed =>
                    {
                        completedVoyages++;
                        VoyageCompletionResult repeated = await VoyageCompletionRule.ApplyAsync(testDb.Driver, completed.Id, null).ConfigureAwait(false);
                        reentryWrote = repeated.Written;
                    };
                    git.ExistingBranches.Add(entities.Dock.BranchName!);
                    missionService.OnMissionComplete = async (produced, dock) =>
                    {
                        AssertEqual(MissionStatusEnum.WorkProduced, produced.Status, "agent exit hands WorkProduced to landing");
                        if (produced.Id == sibling.Mission.Id)
                        {
                            siblingEntered.TrySetResult(true);
                            await finishSibling.Task.ConfigureAwait(false);
                        }
                        produced.DiffSnapshot = "diff --git a/app/routes_ops.py b/app/routes_ops.py";
                        await testDb.Driver.Missions.UpdateAsync(produced).ConfigureAwait(false);
                        await handler.HandleMissionCompleteAsync(produced, dock).ConfigureAwait(false);
                    };

                    // Exercise both real handoffs with one sibling still completing, without a health sweep.
                    Task siblingCompletion = missionService.HandleCompletionAsync(sibling.Captain);
                    try
                    {
                        await siblingEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        await missionService.HandleCompletionAsync(captain).ConfigureAwait(false);
                        AssertEqual(VoyageStatusEnum.InProgress, (await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false))!.Status,
                            "a sibling's in-flight landing keeps the voyage open");
                        AssertEqual(0, completedVoyages);
                    }
                    finally
                    {
                        finishSibling.TrySetResult(true);
                        await siblingCompletion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }
                    Captain? releasedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, releasedCaptain!.State, "Captain should be Idle after completion");
                    AssertEqual(VoyageStatusEnum.Complete, (await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false))!.Status,
                        "direct landing completes the voyage before a periodic health tick");
                    AssertEqual(1, completedVoyages, "the voyage completion callback runs once");
                    AssertEqual(false, reentryWrote, "the external completion hook can safely re-enter without writing");

                    Mission? landed = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, landed!.Status, "A successful local merge completes the mission");
                    AssertEqual(1, git.MergeBranchCalls.Count(c => c == entities.Dock.BranchName + " -> " + integrationWorktree),
                        "The mission branch is merged once, in the integration worktree. Calls: " + String.Join(", ", git.MergeBranchCalls));
                    AssertFalse(git.MergeBranchCalls.Contains(entities.Dock.BranchName + " -> " + vessel.WorkingDirectory),
                        "The mission branch must never be merged into the user working directory");
                    AssertTrue(git.MergeBranchCalls.IndexOf(vessel.DefaultBranch + " -> " + vessel.WorkingDirectory)
                        > git.MergeBranchCalls.IndexOf(entities.Dock.BranchName + " -> " + integrationWorktree),
                        "After the integration merge, the user working directory syncs the landed default branch. Calls: " + String.Join(", ", git.MergeBranchCalls));
                    AssertTrue(git.RemoveWorktreeCalls.Contains(integrationWorktree), "Integration worktree should be removed after landing");
                }
            });

            await RunTest("Capture records branch_unverified for a branch that lags the captured commit and leaves the branch alone", async () =>
            {
                BranchCheckResult result = await CaptureBranchCheckAsync(branchHoldsCommit: false).ConfigureAwait(false);

                AssertEqual(0, result.VerifiedEvents.Count, "A lagging branch records no branch_verified event");
                AssertEqual(1, result.UnverifiedEvents.Count, "A lagging branch records one branch_unverified event");
                AssertContains(result.CapturedCommit, result.UnverifiedEvents[0].Payload ?? "", "The event names the captured commit");
                AssertContains(result.BranchTipBefore, result.UnverifiedEvents[0].Payload ?? "", "The event names the branch tip");
                AssertEqual(result.BranchTipBefore, result.BranchTipAfter, "The check never moves the branch");
                AssertNotEqual(result.BranchTipBefore, result.CapturedCommit, "The scenario leaves the branch behind the captured commit");
            });

            await RunTest("Capture records branch_verified when the mission branch holds the captured commit", async () =>
            {
                BranchCheckResult result = await CaptureBranchCheckAsync(branchHoldsCommit: true).ConfigureAwait(false);

                AssertEqual(1, result.VerifiedEvents.Count, "A branch holding the commit records one branch_verified event");
                AssertEqual(0, result.UnverifiedEvents.Count, "A branch holding the commit records no branch_unverified event");
                AssertEqual(result.CapturedCommit, result.BranchTipAfter, "The branch tip is the captured commit");
            });

            await RunTest("A landing handoff that throws moves the produced mission to LandingFailed with its reason", async () =>
            {
                foreach (bool activeMergeEntry in new[] { false, true })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        StubGitService git = new StubGitService();
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                        MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                        LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue);
                        if (activeMergeEntry)
                        {
                            MergeEntry entry = new MergeEntry(entities.Dock.BranchName!, "main") { MissionId = entities.Mission.Id, VesselId = entities.Vessel.Id };
                            await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
                        }

                        missionService.OnMissionComplete = (produced, dock) => throw new InvalidOperationException("handoff storage unavailable");

                        await missionService.HandleCompletionAsync(entities.Captain).ConfigureAwait(false);

                        Mission? stored = await testDb.Driver.Missions.ReadAsync(entities.Mission.Id).ConfigureAwait(false);
                        if (activeMergeEntry)
                        {
                            AssertEqual(MissionStatusEnum.WorkProduced, stored!.Status, "an active merge entry still lands the work, so the mission is not failed");
                        }
                        else
                        {
                            AssertEqual(MissionStatusEnum.LandingFailed, stored!.Status, "a thrown handoff with no merge entry must not leave the mission WorkProduced");
                            AssertContains("landing_handoff_failed", stored.FailureReason ?? "", "the reason is recorded on the mission");
                            AssertContains("handoff storage unavailable", stored.FailureReason ?? "", "the reason carries the failure");
                            List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(stored.Id, 100).ConfigureAwait(false);
                            AssertTrue(events.Any(item => item.EventType == "mission.landing_failed"), "the failure is visible as a landing_failed event");
                        }
                    }
                }
            });

            await RunTest("Local merge failure sets LandingFailed on merge exception", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.ShouldThrowOnMergeLocal = true;
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                    IMessageTemplateService templateService = new MessageTemplateService(logging);
                    MissionLandingHandler handler = new MissionLandingHandler(
                        logging,
                        testDb.Driver,
                        settings,
                        git,
                        new StubMergeQueueService(),
                        landingService,
                        new AutoLandEvaluator(),
                        new ConventionChecker(),
                        new CriticalTriggerEvaluator(),
                        templateService,
                        null,
                        dockService,
                        new NoOpRemoteTriggerService(),
                        null);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(
                        testDb.Driver,
                        LandingModeEnum.LocalMerge,
                        BranchCleanupPolicyEnum.LocalAndRemote);
                    Captain captain = entities.Captain;
                    Mission mission = entities.Mission;
                    Vessel vessel = entities.Vessel;
                    string integrationWorktree = Path.Combine(settings.DocksDirectory, "_integration", mission.Id);

                    // WorkProduced is set by HandleCompletionAsync
                    await missionService.HandleCompletionAsync(captain);
                    git.ExistingBranches.Add(entities.Dock.BranchName!);
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.DiffSnapshot = "diff --git a/app/routes_ops.py b/app/routes_ops.py";
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                    Mission? wp = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertNotNull(wp, "Mission should still exist after failed landing");
                    AssertEqual(MissionStatusEnum.LandingFailed, wp!.Status, "Failed integration merge should set LandingFailed");
                    AssertContains("integration_merge_failed", wp.FailureReason ?? "", "Failure reason should name the integration merge failure class");
                    AssertContains("Simulated merge failure", wp.FailureReason ?? "", "Failure reason should carry the underlying git error");
                    ProductionFactPage<MissionAttemptFact> facts = await testDb.Driver.MissionAttemptFacts.EnumerateAsync(
                        new ProductionFactQuery { FromUtc = DateTime.UtcNow.AddMinutes(-1), ToUtc = DateTime.UtcNow.AddMinutes(1) }).ConfigureAwait(false);
                    List<MissionAttemptFact> failedFacts = facts.Items.Where(item => item.MissionId == mission.Id
                        && item.FactType == MissionAttemptFactTypeEnum.Failed).ToList();
                    AssertEqual(1, failedFacts.Count, "The landing handler records the terminal failure once");
                    AssertEqual("landing_merge_failed", failedFacts[0].ReasonCode,
                        "A merge exception emits its typed landing cause without using failure prose");
                    AssertTrue(git.MergeBranchCalls.Contains(entities.Dock.BranchName + " -> " + integrationWorktree), "Merge should be attempted in the integration worktree");
                    AssertTrue(git.RemoveWorktreeCalls.Contains(integrationWorktree), "Integration worktree should be removed after failed landing");
                    AssertTrue(git.PruneWorktreeCalls.Contains(vessel.LocalPath!), "Bare repo worktrees should be pruned after failed landing");
                    AssertFalse(git.MergeBranchCalls.Any(c => c.EndsWith(" -> " + vessel.WorkingDirectory, StringComparison.Ordinal)), "User working directory must not be merge target");
                    AssertFalse(git.PushCalls.Contains(vessel.WorkingDirectory!), "User working directory must not be pushed");
                    AssertFalse(git.PullFastForwardOnlyCalls.Contains(vessel.WorkingDirectory!), "Failed landing must not sync the user working directory");
                    AssertFalse(git.OperationCalls.Contains("delete-local-branch:" + entities.Dock.BranchName), "Failed landing should preserve local mission branch for retry");
                    AssertFalse(git.OperationCalls.Contains("delete-remote-branch:" + entities.Dock.BranchName), "Failed landing should preserve remote mission branch for retry");
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
                    AssertEqual(BranchCleanupPolicyEnum.LocalAndRemote, read.BranchCleanupPolicy, "BranchCleanupPolicy should be LocalAndRemote");
                }
            });

            await RunTest("Vessel with null LandingMode reads back as null", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Vessel vessel = new Vessel("null-mode", "https://github.com/test/repo.git");
                    vessel.LandingMode = null;
                    vessel.BranchCleanupPolicy = null;
                    await testDb.Driver.Vessels.CreateAsync(vessel);

                    Vessel? read = await testDb.Driver.Vessels.ReadAsync(vessel.Id);
                    AssertNotNull(read, "Vessel should exist");
                    AssertNull(read!.LandingMode, "LandingMode should be null");
                    AssertNull(read.BranchCleanupPolicy, "BranchCleanupPolicy should be null");
                }
            });

            // === Voyage LandingMode Resolution ===

            await RunTest("Voyage LandingMode is persisted and read correctly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Voyage voyage = new Voyage("mode-test-voyage");
                    voyage.LandingMode = LandingModeEnum.MergeQueue;
                    await testDb.Driver.Voyages.CreateAsync(voyage);

                    Voyage? read = await testDb.Driver.Voyages.ReadAsync(voyage.Id);
                    AssertNotNull(read, "Voyage should exist");
                    AssertEqual(LandingModeEnum.MergeQueue, read!.LandingMode, "LandingMode should be MergeQueue");
                }
            });

            // === PullRequestOpen Does Not Complete Voyage ===

            await RunTest("Voyage with PullRequestOpen mission does not complete", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);

                    Voyage voyage = new Voyage("PR voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await testDb.Driver.Voyages.CreateAsync(voyage);

                    Mission m1 = new Mission("done");
                    m1.VoyageId = voyage.Id;
                    m1.Status = MissionStatusEnum.Complete;
                    await testDb.Driver.Missions.CreateAsync(m1);

                    Mission m2 = new Mission("pr-open");
                    m2.VoyageId = voyage.Id;
                    m2.Status = MissionStatusEnum.PullRequestOpen;
                    await testDb.Driver.Missions.CreateAsync(m2);

                    List<Voyage> completed = await voyageService.CheckCompletionsAsync();
                    AssertEqual(0, completed.Count, "Voyage should NOT complete while a mission is PullRequestOpen");

                    // Now complete the PR mission
                    m2.Status = MissionStatusEnum.Complete;
                    await testDb.Driver.Missions.UpdateAsync(m2);

                    completed = await voyageService.CheckCompletionsAsync();
                    AssertEqual(1, completed.Count, "Voyage should complete when all missions are Complete");
                }
            });

            await RunTest("Voyage completion broadcast uses terminal voyage status", () =>
            {
                string source = ReadRepositoryFile("src", "Armada.Server", "MissionLandingHandler.cs");
                string method = ExtractBetween(
                    source,
                    "public Task HandleVoyageCompleteAsync(Voyage voyage)",
                    "public async Task<bool> HandleReconcilePullRequestAsync(Mission mission)");

                // The record overload reads the status and the owner scope from the persisted voyage.
                AssertContains(
                    "BroadcastVoyageChange(voyage)",
                    method,
                    "Voyage completion broadcast must pass the persisted voyage so its terminal status and owner are used.");
                AssertDoesNotContain(
                    "VoyageStatusEnum.Complete.ToString()",
                    method,
                    "Failed voyages must not be broadcast as Complete.");

                string hub = ReadRepositoryFile("src", "Armada.Server", "WebSocket", "ArmadaWebSocketHub.cs");
                string overload = ExtractBetween(
                    hub,
                    "public void BroadcastVoyageChange(Voyage voyage)",
                    "public void BroadcastCaptainChange(");
                AssertContains(
                    "voyage.Status.ToString()",
                    overload,
                    "The voyage broadcast overload must use the persisted voyage status.");
                return Task.CompletedTask;
            });

            // === Dock Reclaim Idempotency ===

            await RunTest("Double ReclaimAsync is safe (idempotent)", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("reclaim-test", "https://github.com/test/repo.git");
                    await testDb.Driver.Vessels.CreateAsync(vessel);

                    Dock dock = new Dock(vessel.Id);
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_reclaim_" + Guid.NewGuid().ToString("N"));
                    dock.Active = true;
                    await testDb.Driver.Docks.CreateAsync(dock);

                    // First reclaim
                    await dockService.ReclaimAsync(dock.Id);

                    Dock? afterFirst = await testDb.Driver.Docks.ReadAsync(dock.Id);
                    AssertNotNull(afterFirst, "Dock should still exist");
                    Assert(!afterFirst!.Active, "Dock should be inactive after first reclaim");

                    // Second reclaim — should be a no-op
                    await dockService.ReclaimAsync(dock.Id);

                    Dock? afterSecond = await testDb.Driver.Docks.ReadAsync(dock.Id);
                    AssertNotNull(afterSecond, "Dock should still exist after second reclaim");
                    Assert(!afterSecond!.Active, "Dock should still be inactive");
                }
            });

            await RunTest("Reclaim records dock.reclaimed with the caller, the mission and its status, once per reclaim", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    IDockService dockService = new DockService(logging, testDb.Driver, CreateSettings(), git);
                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.LocalMerge);
                    Dock dock = entities.Dock;
                    Mission mission = entities.Mission;
                    mission.Status = MissionStatusEnum.LandingFailed;
                    await testDb.Driver.Missions.UpdateAsync(mission);

                    await dockService.ReclaimAsync(dock.Id);
                    await dockService.ReclaimAsync(dock.Id);

                    List<ArmadaEvent> events = (await testDb.Driver.Events.EnumerateByTypeAsync("dock.reclaimed", 50))
                        .Where(e => e.EntityId == dock.Id).ToList();
                    AssertEqual(1, events.Count, "The first reclaim records one event and the repeated no-op reclaim records none");
                    ArmadaEvent recorded = events[0];
                    AssertEqual(mission.Id, recorded.MissionId, "The event names the mission");
                    AssertContains("LandingPipelineTests", recorded.Payload ?? "", "The payload names the calling type");
                    AssertContains("\"MissionStatus\":\"LandingFailed\"", recorded.Payload ?? "", "The payload names the mission status at reclaim");
                    AssertContains(mission.Id, recorded.Payload ?? "", "The payload names the mission");
                }
            });

            await RunTest("Reclaim anchors a branchless dock commit by identity", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("anchor-test", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel);

                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("anchor-captain"));

                    // An architect fan-out worker is spawned BRANCHLESS by design, so a
                    // branch-keyed preserve skips it and its commit ends up on no ref at all.
                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.BranchName = null;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_anchor_" + Guid.NewGuid().ToString("N"));
                    dock.Active = true;
                    dock = await testDb.Driver.Docks.CreateAsync(dock);

                    Directory.CreateDirectory(dock.WorktreePath!);

                    Mission mission = new Mission("[Worker] fan-out", "work");
                    mission.VesselId = vessel.Id;
                    mission.CaptainId = captain.Id;
                    mission.DockId = dock.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission);

                    try
                    {
                        await dockService.ReclaimAsync(dock.Id);

                        AssertTrue(
                            git.OperationCalls.Contains("copy-ref:HEAD:refs/armada/docks/" + dock.Id),
                            "Reclaim must anchor the dock HEAD under a dock-keyed ref");
                        AssertTrue(
                            git.OperationCalls.Contains("copy-ref:HEAD:refs/armada/missions/" + mission.Id),
                            "Reclaim must anchor the dock HEAD under the owning mission's ref");
                        AssertEqual(0, git.PushCalls.Count, "Anchoring is a local ref update; nothing may be pushed to the vessel remote");
                    }
                    finally
                    {
                        if (Directory.Exists(dock.WorktreePath!)) Directory.Delete(dock.WorktreePath!, true);
                    }
                }
            });

            await RunTest("Successful local landing removes active worktree before deleting branch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                    IMessageTemplateService templateService = new MessageTemplateService(logging);
                    MissionLandingHandler handler = new MissionLandingHandler(
                        logging,
                        testDb.Driver,
                        settings,
                        git,
                        new StubMergeQueueService(),
                        landingService,
                        new AutoLandEvaluator(),
                        new ConventionChecker(),
                        new CriticalTriggerEvaluator(),
                        templateService,
                        null,
                        dockService,
                        new NoOpRemoteTriggerService(),
                        null);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(
                        testDb.Driver,
                        LandingModeEnum.LocalMerge,
                        BranchCleanupPolicyEnum.LocalAndRemote);

                    git.ExistingBranches.Add(entities.Dock.BranchName!);
                    entities.Mission.Status = MissionStatusEnum.WorkProduced;
                    entities.Mission.DiffSnapshot = "diff --git a/app/routes_ops.py b/app/routes_ops.py";
                    await testDb.Driver.Missions.UpdateAsync(entities.Mission).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(entities.Mission, entities.Dock).ConfigureAwait(false);

                    int removeIndex = git.OperationCalls.IndexOf("remove-worktree:" + entities.Dock.WorktreePath);
                    int deleteLocalIndex = git.OperationCalls.IndexOf("delete-local-branch:" + entities.Dock.BranchName);
                    int deleteRemoteIndex = git.OperationCalls.IndexOf("delete-remote-branch:" + entities.Dock.BranchName);

                    AssertTrue(removeIndex >= 0, "Landing cleanup should remove the active worktree");
                    AssertTrue(deleteLocalIndex > removeIndex, "Local branch deletion should happen after the worktree is removed");
                    AssertTrue(deleteRemoteIndex > deleteLocalIndex, "Remote branch deletion should happen after the local branch delete attempt");

                    Mission? updatedMission = await testDb.Driver.Missions.ReadAsync(entities.Mission.Id).ConfigureAwait(false);
                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Complete, updatedMission!.Status, "Mission should be marked Complete after successful landing");
                }
            });

            await RunTest("Built-in protected paths block CLAUDE.md before local landing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                    IMessageTemplateService templateService = new MessageTemplateService(logging);
                    MissionLandingHandler handler = new MissionLandingHandler(
                        logging,
                        testDb.Driver,
                        settings,
                        git,
                        new StubMergeQueueService(),
                        landingService,
                        new AutoLandEvaluator(),
                        new ConventionChecker(),
                        new CriticalTriggerEvaluator(),
                        templateService,
                        null,
                        dockService,
                        new NoOpRemoteTriggerService(),
                        null);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(
                        testDb.Driver,
                        LandingModeEnum.LocalMerge,
                        BranchCleanupPolicyEnum.LocalAndRemote);

                    git.ExistingBranches.Add(entities.Dock.BranchName!);
                    entities.Mission.Status = MissionStatusEnum.WorkProduced;
                    entities.Mission.DiffSnapshot = "diff --git a/CLAUDE.md b/CLAUDE.md\n+++ b/CLAUDE.md\n+generated mission context\n";
                    await testDb.Driver.Missions.UpdateAsync(entities.Mission).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(entities.Mission, entities.Dock).ConfigureAwait(false);

                    Mission? updatedMission = await testDb.Driver.Missions.ReadAsync(entities.Mission.Id).ConfigureAwait(false);
                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "Protected CLAUDE.md changes should fail before landing");
                    AssertContains("CLAUDE.md", updatedMission.FailureReason ?? "", "Failure should name CLAUDE.md");
                    AssertFalse(git.OperationCalls.Contains("merge-local:" + entities.Dock.BranchName), "Protected mission should not merge locally");
                    AssertFalse(git.OperationCalls.Contains("push:" + entities.Vessel.WorkingDirectory), "Protected mission should not push target branch");
                }
            });

            await RunTest("Direct landing refuses without landing when the change diff cannot be read", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.ShouldThrowOnDiff = true;
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(
                        testDb.Driver,
                        LandingModeEnum.LocalMerge,
                        BranchCleanupPolicyEnum.LocalAndRemote);
                    git.ExistingBranches.Add(entities.Dock.BranchName!);
                    entities.Mission.Status = MissionStatusEnum.WorkProduced;
                    entities.Mission.DiffSnapshot = "diff --git a/app/routes_ops.py b/app/routes_ops.py";
                    await testDb.Driver.Missions.UpdateAsync(entities.Mission).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(entities.Mission, entities.Dock).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(entities.Mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.LandingFailed, updated!.Status, "Unreadable evidence refuses the landing");
                    AssertContains(LandingEvidence.RefusalPrefix + ": diff_unreadable", updated.FailureReason ?? "", "The refusal names the unreadable diff");
                    AssertEqual(0, git.MergeBranchCalls.Count, "No merge runs without evidence");
                    AssertEqual(0, git.PushCalls.Count, "Nothing is pushed without evidence");
                }
            });

            await RunTest("Direct landing refuses without landing when the vessel cannot be read", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(
                        testDb.Driver,
                        LandingModeEnum.LocalMerge,
                        BranchCleanupPolicyEnum.LocalAndRemote);
                    entities.Vessel.ProtectedPaths = new List<string> { "app/**" };
                    await testDb.Driver.Vessels.UpdateAsync(entities.Vessel).ConfigureAwait(false);
                    git.ExistingBranches.Add(entities.Dock.BranchName!);
                    git.DiffResult = "diff --git a/app/routes_ops.py b/app/routes_ops.py\n--- a/app/routes_ops.py\n+++ b/app/routes_ops.py\n@@ -1 +1 @@\n-a\n+b\n";
                    entities.Mission.Status = MissionStatusEnum.WorkProduced;
                    await testDb.Driver.Missions.UpdateAsync(entities.Mission).ConfigureAwait(false);

                    FaultingVesselMethods faulting = FaultingVesselMethods.Install(testDb.Driver, typeof(LandingEvidenceCollector));
                    await handler.HandleMissionCompleteAsync(entities.Mission, entities.Dock).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(entities.Mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Mission should still exist");
                    AssertTrue(faulting.FailedReads > 0, "The landing gate read the vessel and the read failed");
                    AssertEqual(MissionStatusEnum.LandingFailed, updated!.Status, "A landing whose vessel rules cannot be read is refused");
                    AssertContains(LandingEvidence.RefusalPrefix + ": vessel_unreadable", updated.FailureReason ?? "", "The refusal names the unreadable vessel");
                    AssertEqual(0, git.MergeBranchCalls.Count, "No merge runs without the vessel's protected paths");
                    AssertEqual(0, git.PushCalls.Count, "Nothing is pushed without the vessel's protected paths");
                }
            });

            await RunTest("Direct landing blocks a Git-quoted file name that matches a protected path", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(
                        testDb.Driver,
                        LandingModeEnum.LocalMerge,
                        BranchCleanupPolicyEnum.LocalAndRemote);
                    entities.Vessel.ProtectedPaths = new List<string> { "docs/résumé.md" };
                    await testDb.Driver.Vessels.UpdateAsync(entities.Vessel).ConfigureAwait(false);
                    git.ExistingBranches.Add(entities.Dock.BranchName!);

                    string quotedDiff =
                        "diff --git \"a/docs/r\\303\\251sum\\303\\251.md\" \"b/docs/r\\303\\251sum\\303\\251.md\"\n" +
                        "new file mode 100644\n" +
                        "--- /dev/null\n" +
                        "+++ \"b/docs/r\\303\\251sum\\303\\251.md\"\n" +
                        "@@ -0,0 +1 @@\n" +
                        "+private\n";
                    git.DiffResult = quotedDiff;
                    entities.Mission.Status = MissionStatusEnum.WorkProduced;
                    entities.Mission.DiffSnapshot = quotedDiff;
                    await testDb.Driver.Missions.UpdateAsync(entities.Mission).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(entities.Mission, entities.Dock).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(entities.Mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Failed, updated!.Status, "A quoted protected file name must be blocked");
                    AssertContains("docs/résumé.md", updated.FailureReason ?? "", "The failure names the decoded path");
                    AssertEqual(0, git.MergeBranchCalls.Count, "A protected mission is not merged");
                    AssertEqual(0, git.PushCalls.Count, "A protected mission is not pushed");
                }
            });

            // === Status Transition Validation ===

            await RunTest("All LandingMode enum values exist", () =>
            {
                string[] expected = new[] { "LocalMerge", "PullRequest", "MergeQueue", "None" };
                string[] actual = Enum.GetNames(typeof(LandingModeEnum));
                AssertEqual(expected.Length, actual.Length, "LandingMode enum value count");

                foreach (string name in expected)
                {
                    Assert(Enum.TryParse<LandingModeEnum>(name, out _), "Missing LandingMode value: " + name);
                }

                return Task.CompletedTask;
            });

            await RunTest("All BranchCleanupPolicy enum values exist", () =>
            {
                string[] expected = new[] { "LocalOnly", "LocalAndRemote", "None" };
                string[] actual = Enum.GetNames(typeof(BranchCleanupPolicyEnum));
                AssertEqual(expected.Length, actual.Length, "BranchCleanupPolicy enum value count");

                foreach (string name in expected)
                {
                    Assert(Enum.TryParse<BranchCleanupPolicyEnum>(name, out _), "Missing BranchCleanupPolicy value: " + name);
                }

                return Task.CompletedTask;
            });

            // === MergeQueue Zero-Commit Auto-Rescue Guard ===

            await RunTest("MergeQueueLanding_AutoRescueZeroCommits_FailsWithRescueNoCommitsReason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                    IMessageTemplateService templateService = new MessageTemplateService(logging);
                    MissionLandingHandler handler = new MissionLandingHandler(
                        logging,
                        testDb.Driver,
                        settings,
                        git,
                        new StubMergeQueueService(),
                        landingService,
                        new AutoLandEvaluator(),
                        new ConventionChecker(),
                        new CriticalTriggerEvaluator(),
                        templateService,
                        null,
                        dockService,
                        new NoOpRemoteTriggerService(),
                        null);

                    string repoPath = Path.Combine(Path.GetTempPath(), "armada_noop_rescue_" + Guid.NewGuid().ToString("N"));
                    string captainBranch = "armada/rescue-captain/msn_rescue123";
                    try
                    {
                        string head = await InitNoOpRepoAsync(repoPath, captainBranch);
                        long headCommitUnixSeconds = Int64.Parse(
                            (await RunGitAsync(repoPath, "show", "-s", "--format=%ct", head).ConfigureAwait(false)).Trim(),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture);
                        DateTime headCommitUtc = DateTimeOffset.FromUnixTimeSeconds(headCommitUnixSeconds).UtcDateTime;

                        Vessel vessel = new Vessel("noop-rescue-vessel", "https://github.com/test/repo.git");
                        vessel.LocalPath = repoPath;
                        vessel.WorkingDirectory = repoPath;
                        vessel.DefaultBranch = "main";
                        vessel.LandingMode = LandingModeEnum.MergeQueue;
                        await testDb.Driver.Vessels.CreateAsync(vessel);

                        Captain captain = new Captain("rescue-captain");
                        captain.State = CaptainStateEnum.Working;
                        await testDb.Driver.Captains.CreateAsync(captain);

                        Dock dock = new Dock(vessel.Id);
                        dock.CaptainId = captain.Id;
                        dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_noop_rescue_wt_" + Guid.NewGuid().ToString("N"));
                        dock.BranchName = captainBranch;
                        dock.Active = true;
                        await testDb.Driver.Docks.CreateAsync(dock);

                        Mission parent = new Mission("Failed parent");
                        parent.VesselId = vessel.Id;
                        parent.Status = MissionStatusEnum.Failed;
                        await testDb.Driver.Missions.CreateAsync(parent);

                        Mission mission = new Mission("Rescue mission");
                        mission.VesselId = vessel.Id;
                        mission.CaptainId = captain.Id;
                        mission.DockId = dock.Id;
                        mission.ParentMissionId = parent.Id;
                        mission.StartedUtc = headCommitUtc.AddSeconds(1);
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission.CommitHash = head;
                        await testDb.Driver.Missions.CreateAsync(mission);

                        await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                        Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id);
                        AssertNotNull(updated, "Mission should still exist");
                        AssertEqual(MissionStatusEnum.Failed, updated!.Status, "Zero-commit auto-rescue must end Failed, not Complete or LandingFailed");
                        AssertEqual("rescue_produced_no_commits", updated.FailureReason ?? "", "Failure reason must be rescue_produced_no_commits");
                        AssertTrue(updated.StartedUtc.HasValue && updated.StartedUtc.Value > headCommitUtc,
                            "The zero-work rescue start time must follow its actual integrated branch commit timestamp");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 100);
                        AssertTrue(events.Any(item => item.EventType == "mission.rescue_no_commits"), "Structured mission.rescue_no_commits event must be emitted");
                        ProductionFactPage<MissionAttemptFact> facts = await testDb.Driver.MissionAttemptFacts.EnumerateAsync(
                            new ProductionFactQuery { FromUtc = DateTime.UtcNow.AddMinutes(-1), ToUtc = DateTime.UtcNow.AddMinutes(1) }).ConfigureAwait(false);
                        List<MissionAttemptFact> failedFacts = facts.Items.Where(item => item.MissionId == mission.Id
                            && item.FactType == MissionAttemptFactTypeEnum.Failed).ToList();
                        AssertEqual(1, failedFacts.Count, "The landing handler records the terminal failure once");
                        MissionAttemptFact? failedFact = failedFacts.FirstOrDefault();
                        AssertNotNull(failedFact, "The no-commit landing failure emits an attempt fact");
                        AssertEqual("rescue_produced_no_commits", failedFact!.ReasonCode,
                            "The typed no-commit landing cause is preserved on the fact");
                    }
                    finally
                    {
                        try { Directory.Delete(repoPath, true); } catch { /* best-effort */ }
                    }
                }
            });

            await RunTest("MergeQueueLanding_NonRescueAlreadyIntegrated_ReconcilesWithoutFalseFailure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                    IMessageTemplateService templateService = new MessageTemplateService(logging);
                    MissionLandingHandler handler = new MissionLandingHandler(
                        logging,
                        testDb.Driver,
                        settings,
                        git,
                        new PersistingMergeQueueService(testDb.Driver),
                        landingService,
                        new AutoLandEvaluator(),
                        new ConventionChecker(),
                        new CriticalTriggerEvaluator(),
                        templateService,
                        null,
                        dockService,
                        new NoOpRemoteTriggerService(),
                        null);

                    string repoPath = Path.Combine(Path.GetTempPath(), "armada_noop_plain_" + Guid.NewGuid().ToString("N"));
                    string captainBranch = "armada/worker-captain/msn_plain123";
                    try
                    {
                        string head = await InitNoOpRepoAsync(repoPath, captainBranch);
                        long headCommitUnixSeconds = Int64.Parse(
                            (await RunGitAsync(repoPath, "show", "-s", "--format=%ct", head).ConfigureAwait(false)).Trim(),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture);
                        DateTime headCommitUtc = DateTimeOffset.FromUnixTimeSeconds(headCommitUnixSeconds).UtcDateTime;

                        Vessel vessel = new Vessel("noop-plain-vessel", "https://github.com/test/repo.git");
                        vessel.LocalPath = repoPath;
                        vessel.WorkingDirectory = repoPath;
                        vessel.DefaultBranch = "main";
                        vessel.LandingMode = LandingModeEnum.MergeQueue;
                        await testDb.Driver.Vessels.CreateAsync(vessel);

                        Captain captain = new Captain("worker-captain");
                        captain.State = CaptainStateEnum.Working;
                        await testDb.Driver.Captains.CreateAsync(captain);

                        Dock dock = new Dock(vessel.Id);
                        dock.CaptainId = captain.Id;
                        dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_noop_plain_wt_" + Guid.NewGuid().ToString("N"));
                        dock.BranchName = captainBranch;
                        dock.Active = true;
                        await testDb.Driver.Docks.CreateAsync(dock);

                        // Non-rescue mission (no ParentMissionId) whose branch was genuinely
                        // integrated earlier -- the legitimate already-integrated path must
                        // reconcile without manufacturing a failure.
                        Mission mission = new Mission("Already integrated mission");
                        mission.VesselId = vessel.Id;
                        mission.CaptainId = captain.Id;
                        mission.DockId = dock.Id;
                        mission.Persona = "Worker";
                        mission.StartedUtc = headCommitUtc.AddSeconds(-1);
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission.CommitHash = head;
                        await testDb.Driver.Missions.CreateAsync(mission);

                        await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                        Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id);
                        AssertNotNull(updated, "Mission should still exist");
                        AssertEqual(MissionStatusEnum.Complete, updated!.Status, "A non-rescue already-integrated mission must reconcile to Complete");
                        AssertTrue(updated.StartedUtc.HasValue && updated.StartedUtc.Value < headCommitUtc,
                            "The Worker start time must precede the actual integrated branch commit timestamp");

                        EnumerationResult<MergeEntry> queued = await testDb.Driver.MergeEntries.EnumerateAsync(
                            new EnumerationQuery { MissionId = mission.Id }).ConfigureAwait(false);
                        AssertEqual(0, queued.Objects.Count, "A branch already at the target head is refused before enqueue, so no merge entry exists");
                        AssertNotEqual("rescue_produced_no_commits", updated.FailureReason ?? "", "Non-rescue path must not borrow the rescue failure reason");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 100);
                        AssertFalse(events.Any(item => item.EventType == "mission.rescue_no_commits"), "Non-rescue path must not emit the rescue no-commits event");
                        AssertTrue(events.Any(item => item.EventType == "mission.noop_already_integrated"), "Verified Worker no-op must emit its reconciliation event");
                        AuthContext auth = AuthContext.Authenticated(
                            mission.TenantId ?? Armada.Core.Constants.DefaultTenantId,
                            mission.UserId ?? Armada.Core.Constants.DefaultUserId,
                            false,
                            true,
                            "UnitTest");
                        EnumerationResult<Incident> incidents = await new IncidentService(testDb.Driver).EnumerateAsync(
                            auth,
                            new IncidentQuery { MissionId = mission.Id }).ConfigureAwait(false);
                        AssertEqual(0, incidents.TotalRecords, "A verified Worker no-op must not create an incident record");
                    }
                    finally
                    {
                        try { Directory.Delete(repoPath, true); } catch { /* best-effort */ }
                    }
                }
            });

            // Guard 2 second detection branch: a zero-commit auto-rescue identified by the
            // description marker alone (no ParentMissionId) must also be failed, proving the
            // marker -- not just the parent link -- discriminates a rescue from the legitimate
            // non-rescue already-integrated mission above (same Persona, same head, no marker).
            await RunTest("MergeQueueLanding_MarkerOnlyAutoRescueZeroCommits_FailsWithRescueNoCommitsReason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                    IMessageTemplateService templateService = new MessageTemplateService(logging);
                    MissionLandingHandler handler = new MissionLandingHandler(
                        logging,
                        testDb.Driver,
                        settings,
                        git,
                        new StubMergeQueueService(),
                        landingService,
                        new AutoLandEvaluator(),
                        new ConventionChecker(),
                        new CriticalTriggerEvaluator(),
                        templateService,
                        null,
                        dockService,
                        new NoOpRemoteTriggerService(),
                        null);

                    string repoPath = Path.Combine(Path.GetTempPath(), "armada_noop_marker_" + Guid.NewGuid().ToString("N"));
                    string captainBranch = "armada/marker-captain/msn_marker123";
                    try
                    {
                        string head = await InitNoOpRepoAsync(repoPath, captainBranch);

                        Vessel vessel = new Vessel("noop-marker-vessel", "https://github.com/test/repo.git");
                        vessel.LocalPath = repoPath;
                        vessel.WorkingDirectory = repoPath;
                        vessel.DefaultBranch = "main";
                        vessel.LandingMode = LandingModeEnum.MergeQueue;
                        await testDb.Driver.Vessels.CreateAsync(vessel);

                        Captain captain = new Captain("marker-captain");
                        captain.State = CaptainStateEnum.Working;
                        await testDb.Driver.Captains.CreateAsync(captain);

                        Dock dock = new Dock(vessel.Id);
                        dock.CaptainId = captain.Id;
                        dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_noop_marker_wt_" + Guid.NewGuid().ToString("N"));
                        dock.BranchName = captainBranch;
                        dock.Active = true;
                        await testDb.Driver.Docks.CreateAsync(dock);

                        // Auto-rescue identified by the marker only -- no ParentMissionId set.
                        Mission mission = new Mission("Rescue mission");
                        mission.VesselId = vessel.Id;
                        mission.CaptainId = captain.Id;
                        mission.DockId = dock.Id;
                        mission.Persona = "Worker";
                        mission.Description = "Autonomous rescue. <!-- ARMADA:AUTO-RESCUE -->";
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission.CommitHash = head;
                        await testDb.Driver.Missions.CreateAsync(mission);

                        await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                        Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id);
                        AssertNotNull(updated, "Mission should still exist");
                        AssertEqual(MissionStatusEnum.Failed, updated!.Status, "Marker-only zero-commit auto-rescue must end Failed");
                        AssertEqual("rescue_produced_no_commits", updated.FailureReason ?? "", "Failure reason must be rescue_produced_no_commits");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 100);
                        AssertTrue(events.Any(item => item.EventType == "mission.rescue_no_commits"), "Structured mission.rescue_no_commits event must be emitted");
                    }
                    finally
                    {
                        try { Directory.Delete(repoPath, true); } catch { /* best-effort */ }
                    }
                }
            });

            // === MergeQueue already-integrated provenance ===

            await RunTest("MergeQueueLanding_IntegratedWorkerCommitAfterStart_CompletesAndEmitsNoOpEvent", async () =>
            {
                await AssertNoOpLandingScenarioAsync(
                    new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    new[] { new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc) },
                    Array.Empty<DateTime>(),
                    "Worker",
                    false,
                    MissionStatusEnum.Complete,
                    null,
                    "mission.noop_already_integrated",
                    true).ConfigureAwait(false);
            });

            await RunTest("MergeQueueLanding_IntegratedWorkerCommitBeforeStart_RemainsLandingFailed", async () =>
            {
                await AssertNoOpLandingScenarioAsync(
                    new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    new[] { new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                    Array.Empty<DateTime>(),
                    "Worker",
                    false,
                    MissionStatusEnum.LandingFailed,
                    "no verified branch commit after mission start",
                    "mission.landing_failed",
                    false).ConfigureAwait(false);
            });

            await RunTest("MergeQueueLanding_IntegratedWorkerCommitAtStart_RemainsLandingFailed", async () =>
            {
                await AssertNoOpLandingScenarioAsync(
                    new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    new[] { new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) },
                    Array.Empty<DateTime>(),
                    "Worker",
                    false,
                    MissionStatusEnum.LandingFailed,
                    "no verified branch commit after mission start",
                    "mission.landing_failed",
                    false).ConfigureAwait(false);
            });

            await RunTest("MergeQueueLanding_IntegratedWorkerWithMissingStart_RemainsLandingFailed", async () =>
            {
                await AssertNoOpLandingScenarioAsync(
                    null,
                    new[] { new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc) },
                    Array.Empty<DateTime>(),
                    "Worker",
                    false,
                    MissionStatusEnum.LandingFailed,
                    "no verified branch commit after mission start",
                    "mission.landing_failed",
                    false).ConfigureAwait(false);
            });

            await RunTest("MergeQueueLanding_CommitAfterStartOnUnrelatedBranch_DoesNotComplete", async () =>
            {
                await AssertNoOpLandingScenarioAsync(
                    new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    Array.Empty<DateTime>(),
                    new[] { new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc) },
                    "Worker",
                    false,
                    MissionStatusEnum.LandingFailed,
                    "no verified branch commit after mission start",
                    "mission.landing_failed",
                    false).ConfigureAwait(false);
            });

            await RunTest("MergeQueueLanding_ReachablePostStartAncestorWithOlderTip_Completes", async () =>
            {
                await AssertNoOpLandingScenarioAsync(
                    new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    new[]
                    {
                        new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    },
                    Array.Empty<DateTime>(),
                    "Worker",
                    false,
                    MissionStatusEnum.Complete,
                    null,
                    "mission.noop_already_integrated",
                    true).ConfigureAwait(false);
            });

            await RunTest("MergeQueueLanding_IntegratedJudgeWithPostStartCommit_RemainsLandingFailed", async () =>
            {
                await AssertNoOpLandingScenarioAsync(
                    new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    new[] { new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc) },
                    Array.Empty<DateTime>(),
                    "Judge",
                    false,
                    MissionStatusEnum.LandingFailed,
                    null,
                    "mission.landing_failed",
                    false).ConfigureAwait(false);
            });

            await RunTest("MergeQueueLanding_RescueWithPostStartCommit_CompletesAsAlreadyIntegrated", async () =>
            {
                await AssertNoOpLandingScenarioAsync(
                    new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    new[] { new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc) },
                    Array.Empty<DateTime>(),
                    "Worker",
                    true,
                    MissionStatusEnum.Complete,
                    null,
                    "mission.noop_already_integrated",
                    true).ConfigureAwait(false);
            });

            // === Landing records carry the mission owner's scope ===
            // Scoped operator reads filter by tenant and user. A merge entry or landing event
            // written without them is invisible to every non-admin reader of its own mission.

            await RunTest("Merge-queue auto-land skip scopes the merge entry and its events to the mission owner", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.DiffResult = "+++ b/docs/readme.md\n+changed\n";
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null, Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId);
                    entities.Vessel.AutoLandPredicate = "{\"Enabled\":true,\"DenyPaths\":[\"docs/**\"]}";
                    await testDb.Driver.Vessels.UpdateAsync(entities.Vessel).ConfigureAwait(false);
                    Mission mission = await OwnMissionAsync(testDb, entities.Mission, "diff --git a/src/Foo.cs b/src/Foo.cs").ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                    await AssertMergeEntryOwnedAsync(testDb, mission.Id).ConfigureAwait(false);
                    await AssertScopedEventAsync(testDb, mission.Id, "merge_queue.auto_land_skipped").ConfigureAwait(false);
                    await AssertScopedEventAsync(testDb, mission.Id, "merge_queue.enqueued").ConfigureAwait(false);
                }
            });

            await RunTest("Merge-queue auto-land trigger scopes its event to the mission owner", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.DiffResult = "+++ b/src/Foo.cs\n+changed\n";
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null, Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId);
                    entities.Vessel.AutoLandPredicate = "{\"Enabled\":true}";
                    await testDb.Driver.Vessels.UpdateAsync(entities.Vessel).ConfigureAwait(false);
                    Mission mission = await OwnMissionAsync(testDb, entities.Mission, "diff --git a/src/Foo.cs b/src/Foo.cs").ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                    await AssertMergeEntryOwnedAsync(testDb, mission.Id).ConfigureAwait(false);
                    await AssertScopedEventAsync(testDb, mission.Id, "merge_queue.auto_land_triggered").ConfigureAwait(false);
                }
            });

            await RunTest("Merge-queue landing records a lost handoff as LandingFailed with its reason", async () =>
            {
                foreach (bool throwOnEnqueue in new[] { true, false })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        StubGitService git = new StubGitService();
                        git.DiffResult = "+++ b/src/Foo.cs\n+changed\n";
                        MissionLandingHandler handler = CreateScopeHandler(testDb, git, CreateLogging(), CreateSettings(),
                            mergeQueue: new FaultingMergeQueueService(throwOnEnqueue));

                        LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null, Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId);
                        Mission mission = await OwnMissionAsync(testDb, entities.Mission, "diff --git a/src/Foo.cs b/src/Foo.cs").ConfigureAwait(false);

                        await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                        string mode = throwOnEnqueue ? "a thrown enqueue" : "an enqueue that returns no entry";
                        Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.LandingFailed, stored!.Status, mode + " leaves no landing path, so the mission must not stay WorkProduced");
                        AssertContains("merge_queue_handoff_lost", stored.FailureReason ?? "", mode + " records its reason on the mission");
                        await AssertScopedEventAsync(testDb, mission.Id, "mission.landing_failed").ConfigureAwait(false);
                    }
                }
            });

            await RunTest("Merge-queue landing keeps WorkProduced when a failed enqueue left an active entry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.DiffResult = "+++ b/src/Foo.cs\n+changed\n";
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, CreateLogging(), CreateSettings(),
                        mergeQueue: new FaultingMergeQueueService(true, activeEntryExists: true));

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null, Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId);
                    Mission mission = await OwnMissionAsync(testDb, entities.Mission, "diff --git a/src/Foo.cs b/src/Foo.cs").ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, stored!.Status, "an active merge entry still lands the work, so the mission is not failed");
                    AssertNull(stored.FailureReason, "no failure is recorded while the queue holds the entry");
                }
            });

            await RunTest("Local landing completion event is scoped to the mission owner", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(
                        testDb.Driver, LandingModeEnum.LocalMerge, BranchCleanupPolicyEnum.LocalAndRemote);
                    git.ExistingBranches.Add(entities.Dock.BranchName!);
                    Mission mission = await OwnMissionAsync(testDb, entities.Mission, "diff --git a/app/routes_ops.py b/app/routes_ops.py").ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                    Mission? landed = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, landed!.Status, "Mission should land");
                    await AssertScopedEventAsync(testDb, mission.Id, "mission.completed").ConfigureAwait(false);
                }
            });

            await RunTest("Landing-drain safety-net enqueue scopes the merge entry user and its events", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MergeQueueService mergeQueue = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null, Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId);
                    entities.Vessel.AutoLandPredicate = "{\"Enabled\":true}";
                    await testDb.Driver.Vessels.UpdateAsync(entities.Vessel).ConfigureAwait(false);
                    Mission mission = await OwnMissionAsync(testDb, entities.Mission, "diff --git a/src/Foo.cs b/src/Foo.cs").ConfigureAwait(false);
                    mission.BranchName = entities.Dock.BranchName;

                    // No diff: the safety net flags the entry for review and records a skip.
                    SafetyNetEnqueueResult result = await mergeQueue.TrySafetyNetEnqueueAsync(
                        mission, entities.Vessel, null, new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator()).ConfigureAwait(false);

                    AssertEqual(SafetyNetEnqueueOutcomeEnum.EnqueuedFlaggedForReview, result.Outcome, "Missing diff is flagged for review");
                    await AssertMergeEntryOwnedAsync(testDb, mission.Id).ConfigureAwait(false);
                    await AssertScopedEventAsync(testDb, mission.Id, "merge_queue.auto_land_skipped").ConfigureAwait(false);
                    await AssertScopedEventAsync(testDb, mission.Id, "merge_queue.enqueued").ConfigureAwait(false);
                }
            });

            await RunTest("Merge-queue enqueue for a vessel without a tenant leaves the entry tenant unset so processing can read the vessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);

                    // Legacy vessel row: no tenant. Queue processing reads the vessel in the entry's tenant,
                    // so stamping the mission tenant on the entry would make the vessel unreadable.
                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null, null, null);
                    Mission mission = await OwnMissionAsync(testDb, entities.Mission, "diff --git a/src/Foo.cs b/src/Foo.cs").ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                    EnumerationResult<MergeEntry> entries = await testDb.Driver.MergeEntries.EnumerateAsync(
                        new EnumerationQuery { MissionId = mission.Id }).ConfigureAwait(false);
                    AssertEqual(1, entries.Objects.Count, "The handler enqueues one entry");
                    MergeEntry entry = entries.Objects[0];
                    AssertNull(entry.TenantId, "A vessel without a tenant must not receive the mission tenant on its entry");
                    AssertEqual(Armada.Core.Constants.DefaultUserId, entry.UserId, "The entry still records the mission user");
                }
            });

            await RunTest("Merge-queue enqueue leaves the entry tenant unset when the mission and vessel tenants differ", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);

                    // Queue processing reads both the vessel and the mission in the entry's tenant. With
                    // different tenants one of those reads finds nothing and the mission is never
                    // reconciled, so the entry must stay unscoped.
                    await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = "ten_scope_vessel", Name = "ten_scope_vessel" }).ConfigureAwait(false);
                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null, "ten_scope_vessel", null);
                    Mission mission = await OwnMissionAsync(testDb, entities.Mission, "diff --git a/src/Foo.cs b/src/Foo.cs").ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                    EnumerationResult<MergeEntry> entries = await testDb.Driver.MergeEntries.EnumerateAsync(
                        new EnumerationQuery { MissionId = mission.Id }).ConfigureAwait(false);
                    AssertEqual(1, entries.Objects.Count, "The handler enqueues one entry");
                    AssertNull(entries.Objects[0].TenantId, "Differing mission and vessel tenants must leave the entry tenant unset");
                    await AssertScopedEventAsync(testDb, mission.Id, "merge_queue.enqueued").ConfigureAwait(false);
                }
            });

            await RunTest("Landing-drain safety-net enqueue leaves the entry tenant unset when the mission and vessel tenants differ", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MergeQueueService mergeQueue = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                    await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = "ten_scope_vessel", Name = "ten_scope_vessel" }).ConfigureAwait(false);
                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null, "ten_scope_vessel", null);
                    Mission mission = await OwnMissionAsync(testDb, entities.Mission, "diff --git a/src/Foo.cs b/src/Foo.cs").ConfigureAwait(false);
                    mission.BranchName = entities.Dock.BranchName;

                    await mergeQueue.TrySafetyNetEnqueueAsync(
                        mission, entities.Vessel, null, new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator()).ConfigureAwait(false);

                    EnumerationResult<MergeEntry> entries = await testDb.Driver.MergeEntries.EnumerateAsync(
                        new EnumerationQuery { MissionId = mission.Id }).ConfigureAwait(false);
                    AssertEqual(1, entries.Objects.Count, "The safety net enqueues one entry");
                    AssertNull(entries.Objects[0].TenantId, "Differing mission and vessel tenants must leave the entry tenant unset");
                }
            });

            // === D7 leak_hunk advisory pass on the landing gate ===

            await RunTest("A no-changes landing is proven by the start commit: recorded commits with a missing diff or branch fail, an empty change completes", async () =>
            {
                const string startCommit = "1111111111111111111111111111111111111111";
                const string newCommit = "2222222222222222222222222222222222222222";
                List<NoChangesLandingCase> cases = new List<NoChangesLandingCase>
                {
                    new NoChangesLandingCase("commits recorded, diff snapshot empty", "", true, newCommit, startCommit, null, false),
                    new NoChangesLandingCase("commits recorded, branch missing", "diff --git a/docs/notes.md b/docs/notes.md", false, newCommit, startCommit, null, false),
                    new NoChangesLandingCase("no commits since start, diff empty", "", true, startCommit, startCommit, null, true),
                    new NoChangesLandingCase("commits already on the target, diff empty", "", true, newCommit, startCommit, true, true),
                    new NoChangesLandingCase("start commit not recorded, diff empty", "", true, newCommit, null, null, true)
                };

                foreach (NoChangesLandingCase item in cases)
                {
                    string logDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                        {
                            StubGitService git = new StubGitService();
                            git.IsAncestorResult = item.OnTarget;
                            LoggingModule logging = CreateLogging();
                            ArmadaSettings settings = CreateSettings();
                            settings.LogDirectory = logDirectory;
                            MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);
                            LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.LocalMerge, BranchCleanupPolicyEnum.LocalAndRemote);
                            if (item.BranchExists) git.ExistingBranches.Add(entities.Dock.BranchName!);

                            Mission mission = entities.Mission;
                            mission.Persona = "TestEngineer";
                            mission.Status = MissionStatusEnum.WorkProduced;
                            mission.DiffSnapshot = item.DiffSnapshot;
                            mission.CommitHash = item.CommitHash;
                            await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                            if (item.StartCommit != null)
                                await MissionStageStartCommitResolver.WriteStageStartRecordAsync(logDirectory, mission.Id, item.StartCommit).ConfigureAwait(false);

                            await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                            Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                            if (item.Lands)
                            {
                                AssertEqual(MissionStatusEnum.Complete, after!.Status, item.Name + ": a verified empty change completes");
                            }
                            else
                            {
                                AssertEqual(MissionStatusEnum.LandingFailed, after!.Status, item.Name + ": unproven absence of changes is a landing failure");
                                AssertContains("landing_source_unavailable", after.FailureReason ?? "", item.Name + ": the failure states its reason");
                                AssertContains(newCommit.Substring(0, 12), after.FailureReason ?? "", item.Name + ": the failure names the recorded commit");
                                AssertFalse(git.OperationCalls.Contains("delete-local-branch:" + entities.Dock.BranchName), item.Name + ": the branch is retained for retry");
                                ProductionFactPage<MissionAttemptFact> facts = await testDb.Driver.MissionAttemptFacts.EnumerateAsync(
                                    new ProductionFactQuery { FromUtc = DateTime.UtcNow.AddMinutes(-1), ToUtc = DateTime.UtcNow.AddMinutes(1) }).ConfigureAwait(false);
                                AssertEqual("landing_source_unavailable", facts.Items.Single(fact => fact.MissionId == mission.Id && fact.FactType == MissionAttemptFactTypeEnum.Failed).ReasonCode,
                                    item.Name + ": the attempt fact carries the typed cause");
                            }
                        }
                    }
                    finally
                    {
                        try { if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, true); } catch { }
                    }
                }
            });

            await RunTest("A mission whose deliverable is a ruling or a report is refused by every landing entry point; a voyage-less Worker still lands", async () =>
            {
                List<NotLandableCase> cases = new List<NotLandableCase>
                {
                    new NotLandableCase("standalone Challenger", PersonaCatalog.Challenger, MissionModeEnum.Audit, false),
                    new NotLandableCase("standalone Audit", "Judge", MissionModeEnum.Audit, false),
                    new NotLandableCase("standalone Research", "Worker", MissionModeEnum.Research, false),
                    new NotLandableCase("standalone Worker", "Worker", MissionModeEnum.Implementation, true)
                };

                foreach (NotLandableCase item in cases)
                {
                    // Completion: the landing handler that completion, review approve, hold release and
                    // retry landing all reach.
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        StubGitService git = new StubGitService();
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);
                        LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.LocalMerge);
                        git.ExistingBranches.Add(entities.Dock.BranchName!);
                        Mission mission = await ShapeMissionAsync(testDb, entities, item).ConfigureAwait(false);

                        await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                        Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Complete, after!.Status, item.Name + ": completion ends Complete");
                        AssertEqual(item.Lands ? 1 : 0, git.MergeBranchCalls.Count(c => c.StartsWith(entities.Dock.BranchName + " -> ", StringComparison.Ordinal)),
                            item.Name + ": merges the mission branch. Calls: " + String.Join(", ", git.MergeBranchCalls));
                        if (!item.Lands)
                        {
                            AssertEqual(0, git.MergeBranchCalls.Count, item.Name + ": runs no merge at all");
                            AssertEqual(0, git.PushCalls.Count, item.Name + ": pushes nothing. Calls: " + String.Join(", ", git.PushCalls));
                            AssertEqual(0, git.CompareAndSwapCalls.Count + git.ForceUpdateBranchRefCalls.Count, item.Name + ": moves no ref");
                            await AssertScopedEventAsync(testDb, mission.Id, MissionLandingEligibility.SkippedEvent).ConfigureAwait(false);
                            AssertEqual(0, await CountEventsAsync(testDb, mission.Id, "mission.completed").ConfigureAwait(false),
                                item.Name + ": records no landing");
                        }
                        AssertEqual(0, await CountMergeEntriesAsync(testDb, mission.Id).ConfigureAwait(false),
                            item.Name + ": a local merge vessel enqueues nothing");
                    }

                    // Merge queue enqueue (REST, MCP, WebSocket and the landing handler) and the landing-drain safety net.
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        StubGitService git = new StubGitService();
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        MergeQueueService mergeQueue = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null,
                            Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId);
                        Mission mission = await ShapeMissionAsync(testDb, entities, item).ConfigureAwait(false);

                        bool enqueued = true;
                        try
                        {
                            await mergeQueue.EnqueueAsync(new MergeEntry(entities.Dock.BranchName!, entities.Vessel.DefaultBranch)
                            {
                                MissionId = mission.Id,
                                VesselId = entities.Vessel.Id,
                                TenantId = mission.TenantId,
                                UserId = mission.UserId
                            }).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            enqueued = false;
                            AssertContains("cannot be enqueued", ex.Message, item.Name + ": the refusal says why");
                        }
                        AssertEqual(item.Lands, enqueued, item.Name + ": enqueue accepts the mission");

                        if (!item.Lands)
                        {
                            SafetyNetEnqueueResult result = await mergeQueue.TrySafetyNetEnqueueAsync(
                                mission, entities.Vessel, "diff --git a/src/Foo.cs b/src/Foo.cs",
                                new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator()).ConfigureAwait(false);
                            AssertEqual(SafetyNetEnqueueOutcomeEnum.SkippedNotLandable, result.Outcome, item.Name + ": the landing drain skips it");
                        }
                        AssertEqual(item.Lands ? 1 : 0, await CountMergeEntriesAsync(testDb, mission.Id).ConfigureAwait(false),
                            item.Name + ": merge entries");
                    }

                    // Retry landing. The voyage-less Worker's retry is covered by the landing service's own suite.
                    if (item.Lands) continue;
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        StubGitService git = new StubGitService();
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings);
                        LandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                        int landingCalls = 0;
                        landingService.OnPerformLanding = (retriedMission, retryDock) =>
                        {
                            landingCalls++;
                            return handler.HandleMissionCompleteAsync(retriedMission, retryDock);
                        };
                        LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.LocalMerge);
                        git.ExistingBranches.Add(entities.Dock.BranchName!);
                        Mission mission = await ShapeMissionAsync(testDb, entities, item).ConfigureAwait(false);
                        mission.Status = MissionStatusEnum.LandingFailed;
                        await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                        bool retried = await landingService.RetryLandingAsync(mission.Id).ConfigureAwait(false);

                        AssertFalse(retried, item.Name + ": retry landing refuses");
                        AssertEqual(0, landingCalls, item.Name + ": retry never reaches the landing handler");
                        AssertEqual(0, await CountEventsAsync(testDb, mission.Id, "mission.landing_retry").ConfigureAwait(false),
                            item.Name + ": retry starts no landing attempt");
                        Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.LandingFailed, after!.Status, item.Name + ": retry leaves the status alone");
                        AssertEqual(0, git.MergeBranchCalls.Count + git.PushCalls.Count, item.Name + ": retry merges and pushes nothing");
                    }
                }
            });

            await RunTest("A read-only mission inside a voyage is left to the pipeline's landing decision", () =>
            {
                Mission judge = new Mission("Final Judge") { Persona = "Judge", Mode = MissionModeEnum.Audit, VoyageId = "vyg_example" };
                AssertNull(MissionLandingEligibility.RefusalReason(judge), "A voyage's final read-only stage may land the shared branch");
                Mission challenger = new Mission("Challenge") { Persona = PersonaCatalog.Challenger, Mode = MissionModeEnum.Audit, VoyageId = "vyg_example" };
                AssertNotNull(MissionLandingEligibility.RefusalReason(challenger), "A Challenger never lands, even inside a voyage");
                return Task.CompletedTask;
            });

            await RunTest("Landing gate advisory leak flag does not hold the landing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    // The model reads the hunk as a leak at high confidence. The deterministic scan is
                    // clean, so the landing still proceeds and only an advisory flag is raised.
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(LeakAnswer(0.97, "operator_note"));
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings, BuildLeakHunkAdapter(testDb, client));

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null, null, null);
                    Mission mission = await OwnMissionAsync(testDb, entities.Mission, CleanHunkDiff()).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                    Mission? landed = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(landed, "The mission still exists");
                    AssertFalse(landed!.Status == MissionStatusEnum.Failed, "An advisory flag must never fail the mission");
                    EnumerationResult<MergeEntry> entries = await testDb.Driver.MergeEntries.EnumerateAsync(
                        new EnumerationQuery { MissionId = mission.Id }).ConfigureAwait(false);
                    AssertEqual(1, entries.Objects.Count, "A flagged but deterministically clean mission still lands");
                    AssertTrue(client.CallCount >= 1, "The landing gate consults the advisory pass");
                    EnumerationResult<ArmadaEvent> gated = await testDb.Driver.Events.EnumerateAsync(
                        new EnumerationQuery { EventType = TypedDecisionRecorder.EventTypeGated, PageNumber = 1, PageSize = 50 }).ConfigureAwait(false);
                    AssertTrue(gated.Objects.Count >= 1, "The gated flag is recorded on the landing path");
                }
            });

            await RunTest("Landing gate deterministic finding survives a clean model answer", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    // The model reads the hunk as ordinary product content. The deterministic finding on
                    // the protected path is never demoted, so the landing still fails.
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(LeakAnswer(0.0, "none"));
                    MissionLandingHandler handler = CreateScopeHandler(testDb, git, logging, settings, BuildLeakHunkAdapter(testDb, client));

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.MergeQueue, null, null, null);
                    Mission mission = await OwnMissionAsync(testDb, entities.Mission, ProtectedPathDiff()).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, entities.Dock).ConfigureAwait(false);

                    Mission? blocked = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(blocked, "The mission still exists");
                    AssertEqual(MissionStatusEnum.Failed, blocked!.Status, "A deterministic finding still fails the landing");
                    AssertTrue(!String.IsNullOrEmpty(blocked.FailureReason), "The deterministic failure reason stands");
                    EnumerationResult<MergeEntry> entries = await testDb.Driver.MergeEntries.EnumerateAsync(
                        new EnumerationQuery { MissionId = mission.Id }).ConfigureAwait(false);
                    AssertEqual(0, entries.Objects.Count, "A blocked mission never reaches the merge queue");
                }
            });
        }

        private static LeakHunkAdapter BuildLeakHunkAdapter(TestDatabase testDb, FakeTypedDecisionClient client)
        {
            TypedDecisionSettings typed = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            typed.Decisions["leak_hunk"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 };
            return new LeakHunkAdapter(client, new TypedDecisionRecorder(testDb.Driver, new LoggingModule()), typed, new LoggingModule());
        }

        private static TypedDecisionResult LeakAnswer(double leaks, string kind)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["leaks_private_context"] = new TypedAnswer { Type = "noul", Noul = leaks },
                ["leak_kind"] = new TypedAnswer { Type = "choice", Choice = kind, Confidence = leaks }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static string CleanHunkDiff()
        {
            return "diff --git a/src/Example/Widget.cs b/src/Example/Widget.cs\n"
                + "--- a/src/Example/Widget.cs\n"
                + "+++ b/src/Example/Widget.cs\n"
                + "@@ -1,1 +1,2 @@\n"
                + " public class Widget\n"
                + "+    private int _Counter;\n";
        }

        private static string ProtectedPathDiff()
        {
            return "diff --git a/CLAUDE.md b/CLAUDE.md\n"
                + "--- a/CLAUDE.md\n"
                + "+++ b/CLAUDE.md\n"
                + "@@ -1,1 +1,2 @@\n"
                + " # Rules\n"
                + "+A new rule line.\n";
        }

        private MissionLandingHandler CreateScopeHandler(
            TestDatabase testDb,
            StubGitService git,
            LoggingModule logging,
            ArmadaSettings settings,
            LeakHunkAdapter? leakHunkAdapter = null,
            IMergeQueueService? mergeQueue = null)
        {
            IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
            ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
            MissionLandingHandler handler = new MissionLandingHandler(
                logging,
                testDb.Driver,
                settings,
                git,
                mergeQueue ?? new PersistingMergeQueueService(testDb.Driver),
                landingService,
                new AutoLandEvaluator(),
                new ConventionChecker(),
                new CriticalTriggerEvaluator(),
                new MessageTemplateService(logging),
                null,
                dockService,
                new NoOpRemoteTriggerService(),
                null);
            if (leakHunkAdapter != null) handler.SetLeakHunkAdapter(leakHunkAdapter);
            return handler;
        }

        private sealed class NoChangesLandingCase
        {
            public NoChangesLandingCase(string name, string diffSnapshot, bool branchExists, string commitHash, string? startCommit, bool? onTarget, bool lands)
            {
                Name = name;
                DiffSnapshot = diffSnapshot;
                BranchExists = branchExists;
                CommitHash = commitHash;
                StartCommit = startCommit;
                OnTarget = onTarget;
                Lands = lands;
            }

            public string Name { get; }
            public string DiffSnapshot { get; }
            public bool BranchExists { get; }
            public string CommitHash { get; }
            public string? StartCommit { get; }
            public bool? OnTarget { get; }
            public bool Lands { get; }
        }

        private sealed class NotLandableCase
        {
            public NotLandableCase(string name, string persona, MissionModeEnum mode, bool lands)
            {
                Name = name;
                Persona = persona;
                Mode = mode;
                Lands = lands;
            }

            public string Name { get; }
            public string Persona { get; }
            public MissionModeEnum Mode { get; }
            public bool Lands { get; }
        }

        // A voyage-less WorkProduced mission on the dock's branch, carrying a commit, shaped by the case.
        private static async Task<Mission> ShapeMissionAsync(TestDatabase testDb, LandingTestEntitiesResult entities, NotLandableCase item)
        {
            Mission mission = entities.Mission;
            mission.Persona = item.Persona;
            mission.Mode = item.Mode;
            mission.VoyageId = null;
            mission.BranchName = entities.Dock.BranchName;
            if (!item.Lands) mission.CommitHash = "0123456789abcdef0123456789abcdef01234567";
            return await OwnMissionAsync(testDb, mission, "diff --git a/src/Foo.cs b/src/Foo.cs").ConfigureAwait(false);
        }

        private static async Task<int> CountMergeEntriesAsync(TestDatabase testDb, string missionId)
        {
            EnumerationResult<MergeEntry> entries = await testDb.Driver.MergeEntries.EnumerateAsync(
                new EnumerationQuery { MissionId = missionId }).ConfigureAwait(false);
            return entries.Objects.Count;
        }

        private static async Task<int> CountEventsAsync(TestDatabase testDb, string missionId, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(
                new EnumerationQuery { MissionId = missionId, EventType = eventType }).ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static async Task<Mission> OwnMissionAsync(TestDatabase testDb, Mission mission, string diffSnapshot)
        {
            mission.TenantId = Armada.Core.Constants.DefaultTenantId;
            mission.UserId = Armada.Core.Constants.DefaultUserId;
            mission.Status = MissionStatusEnum.WorkProduced;
            mission.DiffSnapshot = diffSnapshot;
            await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
            return mission;
        }

        private async Task AssertMergeEntryOwnedAsync(TestDatabase testDb, string missionId)
        {
            EnumerationResult<MergeEntry> entries = await testDb.Driver.MergeEntries.EnumerateAsync(
                Armada.Core.Constants.DefaultTenantId,
                Armada.Core.Constants.DefaultUserId,
                new EnumerationQuery { MissionId = missionId }).ConfigureAwait(false);
            AssertEqual(1, entries.Objects.Count, "The mission owner's scoped read must find the merge entry");
            AssertEqual(Armada.Core.Constants.DefaultTenantId, entries.Objects[0].TenantId, "Merge entry tenant");
            AssertEqual(Armada.Core.Constants.DefaultUserId, entries.Objects[0].UserId, "Merge entry user");
        }

        private async Task AssertScopedEventAsync(TestDatabase testDb, string missionId, string eventType)
        {
            EnumerationResult<ArmadaEvent> scoped = await testDb.Driver.Events.EnumerateAsync(
                Armada.Core.Constants.DefaultTenantId,
                Armada.Core.Constants.DefaultUserId,
                new EnumerationQuery { MissionId = missionId, EventType = eventType }).ConfigureAwait(false);
            AssertTrue(scoped.Objects.Count >= 1, "The mission owner's scoped read must find " + eventType);
        }

        private sealed class BranchCheckResult
        {
            public string CapturedCommit { get; set; } = String.Empty;
            public string BranchTipBefore { get; set; } = String.Empty;
            public string BranchTipAfter { get; set; } = String.Empty;
            public List<ArmadaEvent> VerifiedEvents { get; set; } = new List<ArmadaEvent>();
            public List<ArmadaEvent> UnverifiedEvents { get; set; } = new List<ArmadaEvent>();
        }

        // A real checkout whose HEAD commit is either on the mission branch or one commit above it (a detached HEAD).
        private async Task<BranchCheckResult> CaptureBranchCheckAsync(bool branchHoldsCommit)
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "armada_branchcheck_" + Guid.NewGuid().ToString("N"));
            try
            {
                string captainBranch = "armada/test-captain/msn_branchcheck";
                await InitNoOpRepoAsync(rootDir, captainBranch).ConfigureAwait(false);
                await RunGitAsync(rootDir, "checkout", branchHoldsCommit ? captainBranch : "--detach").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(rootDir, "work.txt"), "work\n").ConfigureAwait(false);
                await RunGitAsync(rootDir, "add", "work.txt").ConfigureAwait(false);
                await RunGitAsync(rootDir, "commit", "-m", "Captain work").ConfigureAwait(false);

                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    GitService git = new GitService(logging);
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    MissionLandingHandler handler = new MissionLandingHandler(
                        logging, testDb.Driver, settings, git, new StubMergeQueueService(), new LandingService(logging, testDb.Driver, settings, git),
                        new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator(), new MessageTemplateService(logging),
                        null, dockService, new NoOpRemoteTriggerService(), null);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.LocalMerge);
                    Dock dock = entities.Dock;
                    dock.WorktreePath = rootDir;
                    dock.BranchName = captainBranch;
                    await testDb.Driver.Docks.UpdateAsync(dock);

                    BranchCheckResult result = new BranchCheckResult();
                    result.BranchTipBefore = (await RunGitAsync(rootDir, "rev-parse", captainBranch).ConfigureAwait(false)).Trim();
                    await handler.HandleCaptureDiffAsync(entities.Mission, dock);

                    Mission stored = (await testDb.Driver.Missions.ReadAsync(entities.Mission.Id))!;
                    result.CapturedCommit = stored.CommitHash ?? String.Empty;
                    result.BranchTipAfter = (await RunGitAsync(rootDir, "rev-parse", captainBranch).ConfigureAwait(false)).Trim();
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(entities.Mission.Id, 50);
                    result.VerifiedEvents = events.Where(e => e.EventType == "mission.branch_verified").ToList();
                    result.UnverifiedEvents = events.Where(e => e.EventType == "mission.branch_unverified").ToList();
                    return result;
                }
            }
            finally
            {
                try { Directory.Delete(rootDir, true); } catch (Exception ex) { Console.WriteLine("cleanup of " + rootDir + " failed: " + ex.Message); }
            }
        }

        private static async Task<string> InitNoOpRepoAsync(string repoPath, string captainBranch)
        {
            Directory.CreateDirectory(repoPath);
            await RunGitAsync(repoPath, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "# test\n").ConfigureAwait(false);
            await RunGitAsync(repoPath, "add", "README.md").ConfigureAwait(false);
            await RunGitAsync(repoPath, "commit", "-m", "Initial commit").ConfigureAwait(false);
            // Captain branch points at the same commit as main -- a zero-commit identity branch.
            await RunGitAsync(repoPath, "branch", captainBranch).ConfigureAwait(false);
            string head = await RunGitAsync(repoPath, "rev-parse", "main").ConfigureAwait(false);
            return head.Trim();
        }

        private async Task AssertNoOpLandingScenarioAsync(
            DateTime? startedUtc,
            DateTime[] reachableCommitTimesUtc,
            DateTime[] unrelatedCommitTimesUtc,
            string persona,
            bool autoRescue,
            MissionStatusEnum expectedStatus,
            string? expectedFailureReason,
            string expectedEventType,
            bool expectCompletionEvent)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
            {
                StubGitService git = new StubGitService();
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();
                IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                IMessageTemplateService templateService = new MessageTemplateService(logging);
                MissionLandingHandler handler = new MissionLandingHandler(
                    logging,
                    testDb.Driver,
                    settings,
                    git,
                    new StubMergeQueueService(),
                    landingService,
                    new AutoLandEvaluator(),
                    new ConventionChecker(),
                    new CriticalTriggerEvaluator(),
                    templateService,
                    null,
                    dockService,
                    new NoOpRemoteTriggerService(),
                    null);

                string repoPath = Path.Combine(Path.GetTempPath(), "armada_noop_provenance_" + Guid.NewGuid().ToString("N"));
                string captainBranch = "armada/provenance-captain/msn_" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.CreateDirectory(repoPath);
                    await RunGitAsync(repoPath, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(repoPath, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(repoPath, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await CreateTimestampedCommitAsync(
                        repoPath,
                        "base.txt",
                        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);
                    await RunGitAsync(repoPath, "branch", captainBranch).ConfigureAwait(false);

                    if (unrelatedCommitTimesUtc.Length > 0)
                    {
                        await RunGitAsync(repoPath, "branch", "unrelated").ConfigureAwait(false);
                        await RunGitAsync(repoPath, "checkout", "unrelated").ConfigureAwait(false);
                        for (int index = 0; index < unrelatedCommitTimesUtc.Length; index++)
                        {
                            await CreateTimestampedCommitAsync(
                                repoPath,
                                "unrelated-" + index.ToString(CultureInfo.InvariantCulture) + ".txt",
                                unrelatedCommitTimesUtc[index]).ConfigureAwait(false);
                        }
                        await RunGitAsync(repoPath, "checkout", "main").ConfigureAwait(false);
                    }

                    for (int index = 0; index < reachableCommitTimesUtc.Length; index++)
                    {
                        await CreateTimestampedCommitAsync(
                            repoPath,
                            "reachable-" + index.ToString(CultureInfo.InvariantCulture) + ".txt",
                            reachableCommitTimesUtc[index]).ConfigureAwait(false);
                    }
                    await RunGitAsync(repoPath, "branch", "-f", captainBranch, "HEAD").ConfigureAwait(false);
                    string head = (await RunGitAsync(repoPath, "rev-parse", "main").ConfigureAwait(false)).Trim();

                    Vessel vessel = new Vessel("noop-provenance-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = repoPath;
                    vessel.WorkingDirectory = repoPath;
                    vessel.DefaultBranch = "main";
                    vessel.LandingMode = LandingModeEnum.MergeQueue;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("provenance-captain");
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_noop_provenance_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = captainBranch;
                    dock.Active = true;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    Mission mission = new Mission("Already integrated provenance mission");
                    mission.VesselId = vessel.Id;
                    mission.CaptainId = captain.Id;
                    mission.DockId = dock.Id;
                    mission.Persona = persona;
                    mission.StartedUtc = startedUtc;
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.CommitHash = head;
                    if (autoRescue)
                        mission.Description = "Auto-rescue. <!-- ARMADA:AUTO-RESCUE -->";
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Mission should still exist");
                    AssertEqual(expectedStatus, updated!.Status, "Unexpected mission status for the no-op landing case");
                    if (expectedStatus == MissionStatusEnum.Complete)
                        AssertTrue(String.IsNullOrWhiteSpace(updated.FailureReason), "A successful no-op must not retain a failure reason");
                    else
                    {
                        AssertFalse(String.IsNullOrWhiteSpace(updated.FailureReason), "A rejected no-op must retain a failure reason");
                        if (expectedFailureReason != null)
                            AssertContains(expectedFailureReason, updated.FailureReason ?? "", "Failure reason must preserve the no-op outcome");
                    }

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);
                    AssertTrue(events.Any(item => item.EventType == expectedEventType), "Expected event was not emitted: " + expectedEventType);
                    AssertEqual(expectCompletionEvent, events.Any(item => item.EventType == "mission.completed"),
                        "Completion event must match the verified landing result");
                    if (expectedStatus == MissionStatusEnum.Complete)
                    {
                        AssertTrue(events.Any(item => item.EventType == "mission.noop_already_integrated"), "Successful reconciliation must emit the no-op event");
                        AssertEqual("no-op integration: work already at target",
                            events.Single(item => item.EventType == "mission.noop_already_integrated").Message,
                            "Successful reconciliation must retain the requested no-op note");
                    }
                    else
                        AssertFalse(events.Any(item => item.EventType == "mission.noop_already_integrated"), "An unverified no-op must not emit a success event");

                    if (expectedStatus == MissionStatusEnum.Complete && !autoRescue)
                    {
                        AuthContext auth = AuthContext.Authenticated(
                            mission.TenantId ?? Armada.Core.Constants.DefaultTenantId,
                            mission.UserId ?? Armada.Core.Constants.DefaultUserId,
                            false,
                            true,
                            "UnitTest");
                        EnumerationResult<Incident> incidents = await new IncidentService(testDb.Driver).EnumerateAsync(
                            auth,
                            new IncidentQuery { MissionId = mission.Id }).ConfigureAwait(false);
                        AssertEqual(0, incidents.TotalRecords, "A successful Worker no-op must not create an incident record");
                    }

                    EnumerationResult<MergeEntry> entries = await testDb.Driver.MergeEntries.EnumerateAsync(
                        new EnumerationQuery { MissionId = mission.Id }).ConfigureAwait(false);
                    AssertEqual(0, entries.Objects.Count, "An already-integrated branch must not create a merge-queue entry");
                }
                finally
                {
                    try { Directory.Delete(repoPath, true); } catch { /* best-effort */ }
                }
            }
        }

        private static async Task CreateTimestampedCommitAsync(string repoPath, string fileName, DateTime committedUtc)
        {
            await File.WriteAllTextAsync(Path.Combine(repoPath, fileName), "controlled commit time\n").ConfigureAwait(false);
            await RunGitAsync(repoPath, "add", fileName).ConfigureAwait(false);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.Environment["GIT_AUTHOR_DATE"] = committedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            startInfo.Environment["GIT_COMMITTER_DATE"] = committedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            startInfo.ArgumentList.Add("commit");
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("Controlled timestamp " + fileName);

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git commit failed (exit " + process.ExitCode + "): " + stderr.Trim() + stdout.Trim());
            }
        }

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git failed (exit " + process.ExitCode + "): " + stderr.Trim());
                }

                return stdout;
            }
        }

        private static string ExtractBetween(string contents, string startToken, string endToken)
        {
            int start = contents.IndexOf(startToken, StringComparison.Ordinal);
            if (start < 0)
                throw new Exception("Start token not found: " + startToken);

            int end = contents.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
            if (end < 0)
                throw new Exception("End token not found: " + endToken);

            return contents.Substring(start, end - start);
        }

        private static string ReadRepositoryFile(params string[] relativePath)
        {
            return File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(relativePath)));
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                    Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }

        private void AssertDoesNotContain(string unexpected, string actual, string message)
        {
            if (actual.Contains(unexpected, StringComparison.Ordinal))
            {
                throw new Exception(message + " Unexpected text: " + unexpected);
            }
        }

        private sealed class NoOpRemoteTriggerService : IRemoteTriggerService
        {
            public Task FireDrainerAsync(string vesselId, string text, CancellationToken token = default) => Task.CompletedTask;
            public Task FireCriticalAsync(string text, CancellationToken token = default) => Task.CompletedTask;
            public AgentWakeSessionRegistration RegisterAgentWakeSession(AgentWakeSessionRegistration registration) => registration;
            public AgentWakeSessionRegistration? GetAgentWakeSession() => null;
            public AgentWakeStatusSnapshot GetAgentWakeStatus() => new AgentWakeStatusSnapshot();
            public Task FireBoardWakeAsync(string participantKey, string text, CancellationToken token = default) => Task.CompletedTask;
        }

        /// <summary>
        /// Merge-queue double that stores enqueued entries, so the handler's later entry update and the
        /// test's scoped read see the same row the handler created.
        /// </summary>
        private sealed class PersistingMergeQueueService : IMergeQueueService
        {
            private readonly SqliteDatabaseDriver _Database;

            public PersistingMergeQueueService(SqliteDatabaseDriver database)
            {
                _Database = database;
            }

            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default) => _Database.MergeEntries.CreateAsync(entry, token);
            public Task ProcessQueueAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.CompletedTask;
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default) => Task.FromResult(new List<MergeEntry>());
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default) => Task.CompletedTask;
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult(false);
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(new MergeQueuePurgeResult());
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(0);
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcileLandingStateMachineAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> RecoverInFlightLandingsAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> HasActiveMergeEntryForMissionAsync(string missionId, CancellationToken token = default) => Task.FromResult(false);
            public Task<SafetyNetEnqueueResult> TrySafetyNetEnqueueAsync(Mission mission, Vessel vessel, string? unifiedDiff, IAutoLandEvaluator autoLandEvaluator, IConventionChecker conventionChecker, ICriticalTriggerEvaluator criticalTriggerEvaluator, CancellationToken token = default)
                => Task.FromResult(new SafetyNetEnqueueResult(SafetyNetEnqueueOutcomeEnum.Enqueued, null));
        }

        private class StubMergeQueueService : IMergeQueueService
        {
            public virtual Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default) => Task.FromResult(entry);
            public Task ProcessQueueAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.CompletedTask;
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default) => Task.FromResult(new List<MergeEntry>());
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default) => Task.CompletedTask;
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult(false);
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(new MergeQueuePurgeResult());
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(0);
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcileLandingStateMachineAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> RecoverInFlightLandingsAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default) => Task.FromResult(false);
            public virtual Task<bool> HasActiveMergeEntryForMissionAsync(string missionId, CancellationToken token = default) => Task.FromResult(false);
            public Task<SafetyNetEnqueueResult> TrySafetyNetEnqueueAsync(Mission mission, Vessel vessel, string? unifiedDiff, IAutoLandEvaluator autoLandEvaluator, IConventionChecker conventionChecker, ICriticalTriggerEvaluator criticalTriggerEvaluator, CancellationToken token = default)
                => Task.FromResult(new SafetyNetEnqueueResult(SafetyNetEnqueueOutcomeEnum.Enqueued, null));
        }

        /// <summary>
        /// Merge-queue double whose enqueue fails by throwing or by returning no entry, and that reports
        /// whether the mission already has an active entry.
        /// </summary>
        private sealed class FaultingMergeQueueService : StubMergeQueueService
        {
            private readonly bool _Throw;
            private readonly bool _ActiveEntryExists;

            public FaultingMergeQueueService(bool throwOnEnqueue, bool activeEntryExists = false)
            {
                _Throw = throwOnEnqueue;
                _ActiveEntryExists = activeEntryExists;
            }

            public override Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default)
            {
                if (_Throw) throw new InvalidOperationException("queue storage unavailable");
                return Task.FromResult<MergeEntry>(null!);
            }

            public override Task<bool> HasActiveMergeEntryForMissionAsync(string missionId, CancellationToken token = default)
                => Task.FromResult(_ActiveEntryExists);
        }

    }
}
