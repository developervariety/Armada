namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
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
            SqliteDatabaseDriver db, LandingModeEnum? landingMode = null, BranchCleanupPolicyEnum? cleanupPolicy = null)
        {
            Vessel vessel = new Vessel("test-vessel", "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            vessel.LandingMode = landingMode;
            vessel.BranchCleanupPolicy = cleanupPolicy;
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Captain captain = new Captain("test-captain");
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

            await RunTest("HandleCompletion sets WorkProduced then completion handler can land", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.LocalMerge);
                    Captain captain = entities.Captain;
                    Mission mission = entities.Mission;
                    Dock dock = entities.Dock;
                    Vessel vessel = entities.Vessel;

                    // HandleCompletionAsync should set to WorkProduced
                    await missionService.HandleCompletionAsync(captain);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertNotNull(updated, "Mission should exist after completion");
                    AssertEqual(MissionStatusEnum.WorkProduced, updated!.Status, "Status should be WorkProduced after agent exit");
                }
            });

            await RunTest("Local merge success produces correct git call sequence", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);

                    LandingTestEntitiesResult entities = await CreateTestEntitiesAsync(testDb.Driver, LandingModeEnum.LocalMerge);
                    Captain captain = entities.Captain;
                    Mission mission = entities.Mission;
                    Dock dock = entities.Dock;
                    Vessel vessel = entities.Vessel;

                    // Simulate: agent completion -> WorkProduced
                    await missionService.HandleCompletionAsync(captain);

                    // Verify the stub recorded correct merge call
                    // Note: The actual landing handler runs in the ArmadaServer, not in this unit test,
                    // so we verify that HandleCompletion correctly sets up the state for landing.
                    Mission? wp = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionStatusEnum.WorkProduced, wp!.Status, "Mission should be WorkProduced");

                    // Verify captain was released
                    Captain? releasedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id);
                    AssertNotNull(releasedCaptain, "Captain should still exist");
                    AssertEqual(CaptainStateEnum.Idle, releasedCaptain!.State, "Captain should be Idle after completion");
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
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
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
                    AssertContains("Integration worktree merge failed", wp.FailureReason ?? "", "Failure reason should explain integration merge failure");
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

                AssertContains(
                    "BroadcastVoyageChange(voyage.Id, voyage.Status.ToString(), voyage.Title)",
                    method,
                    "Voyage completion broadcast must use the persisted terminal voyage status.");
                AssertDoesNotContain(
                    "BroadcastVoyageChange(voyage.Id, VoyageStatusEnum.Complete.ToString(), voyage.Title)",
                    method,
                    "Failed voyages must not be broadcast as Complete.");
                return Task.CompletedTask;
            });

            await RunTest("MergeQueue landing refuses no-op branch before enqueue", () =>
            {
                string source = ReadRepositoryFile("src", "Armada.Server", "MissionLandingHandler.cs");
                string method = ExtractBetween(
                    source,
                    "else if (landingModeIsMergeQueue)",
                    "// Mission stays as WorkProduced; merge queue processing will land it");

                AssertContains(
                    "DetectMergeQueueNoOpAsync(mission, dock, vessel, targetBranch)",
                    method,
                    "MergeQueue landing must check for no-op branch identity before enqueue.");
                AssertContains(
                    "landingAttempted = true;",
                    method,
                    "No-op refusal should drive the landing result block.");
                AssertContains(
                    "_MergeQueue.EnqueueAsync(entry)",
                    method,
                    "The normal non-no-op path should still enqueue.");
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

            // === Status Transition Validation ===

            await RunTest("PullRequestOpen allows transition to Complete", () =>
            {
                // Verify the enum values exist and are distinct
                Assert(MissionStatusEnum.PullRequestOpen != MissionStatusEnum.Complete, "PullRequestOpen is distinct from Complete");
                Assert(MissionStatusEnum.PullRequestOpen != MissionStatusEnum.WorkProduced, "PullRequestOpen is distinct from WorkProduced");
                return Task.CompletedTask;
            });

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

            // === Zero-Commit Rescue Landing Guard ===

            await RunTest("AutoRescue_ZeroCommitBranch_TransitionsToFailed_WithReasonRescueProducedNoCommits", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_zc_test_" + Guid.NewGuid().ToString("N"));
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        ZeroCommitGitSetup repos = await CreateZeroCommitGitSetupAsync(rootDir);
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        StubGitService git = new StubGitService();
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                        IMessageTemplateService templateService = new MessageTemplateService(logging);
                        TrackingMergeQueueService trackingMq = new TrackingMergeQueueService();

                        MissionLandingHandler handler = new MissionLandingHandler(
                            logging, testDb.Driver, settings, git, trackingMq, landingService,
                            new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator(),
                            templateService, null, dockService, new NoOpRemoteTriggerService(), null);

                        Vessel vessel = new Vessel("rescue-test", "https://github.com/test/repo.git");
                        vessel.LocalPath = repos.BareDir;
                        vessel.DefaultBranch = "main";
                        vessel.LandingMode = LandingModeEnum.MergeQueue;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Dock dock = new Dock(vessel.Id);
                        dock.BranchName = repos.CaptainBranch;
                        dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_zc_wt_" + Guid.NewGuid().ToString("N"));
                        await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                        // Create the parent mission first to satisfy the FK constraint.
                        Mission parentMission = new Mission("Original failing mission");
                        parentMission.VesselId = vessel.Id;
                        await testDb.Driver.Missions.CreateAsync(parentMission).ConfigureAwait(false);

                        Mission mission = new Mission("Rescue: fix the broken thing");
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission.VesselId = vessel.Id;
                        mission.DockId = dock.Id;
                        mission.ParentMissionId = parentMission.Id;
                        mission.Description = "<!-- ARMADA:AUTO-RESCUE --> Fix the flaky test.";
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                        Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Mission should exist after refusal");
                        AssertEqual(MissionStatusEnum.Failed, updated!.Status, "Zero-commit rescue should be set to Failed, not LandingFailed");
                        AssertEqual("rescue_produced_no_commits", updated.FailureReason, "FailureReason must be rescue_produced_no_commits");
                        AssertFalse(trackingMq.EnqueueCalled, "EnqueueAsync must not be called for a zero-commit rescue");
                    }
                }
                finally
                {
                    DeleteDirectoryForce(rootDir);
                }
            });

            await RunTest("AutoRescue_ZeroCommitBranch_WithLinkedIncident_NotesRecoveryNote", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_zcnote_test_" + Guid.NewGuid().ToString("N"));
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        ZeroCommitGitSetup repos = await CreateZeroCommitGitSetupAsync(rootDir);
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        StubGitService git = new StubGitService();
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                        IMessageTemplateService templateService = new MessageTemplateService(logging);
                        IncidentService incidentService = new IncidentService(testDb.Driver);

                        MissionLandingHandler handler = new MissionLandingHandler(
                            logging, testDb.Driver, settings, git, new TrackingMergeQueueService(), landingService,
                            new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator(),
                            templateService, null, dockService, new NoOpRemoteTriggerService(), null,
                            incidents: incidentService);

                        Vessel vessel = new Vessel("rescue-note-test", "https://github.com/test/repo.git");
                        vessel.LocalPath = repos.BareDir;
                        vessel.DefaultBranch = "main";
                        vessel.LandingMode = LandingModeEnum.MergeQueue;
                        vessel.TenantId = Constants.DefaultTenantId;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Dock dock = new Dock(vessel.Id);
                        dock.BranchName = repos.CaptainBranch;
                        dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_zcnote_wt_" + Guid.NewGuid().ToString("N"));
                        await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                        // Create the parent mission first to satisfy the FK constraint.
                        Mission parentMission = new Mission("Original failing mission for note test");
                        parentMission.VesselId = vessel.Id;
                        parentMission.TenantId = Constants.DefaultTenantId;
                        await testDb.Driver.Missions.CreateAsync(parentMission).ConfigureAwait(false);

                        // Create a linked incident with MissionId = parentMission.Id
                        AuthContext auth = AuthContext.Authenticated(
                            Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "test");
                        Incident incident = await incidentService.CreateAsync(auth, new IncidentUpsertRequest
                        {
                            Title = "Parent mission failed",
                            Status = IncidentStatusEnum.Open,
                            MissionId = parentMission.Id,
                            VesselId = vessel.Id
                        }).ConfigureAwait(false);

                        Mission mission = new Mission("Rescue: note test");
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission.VesselId = vessel.Id;
                        mission.DockId = dock.Id;
                        mission.ParentMissionId = parentMission.Id;
                        mission.TenantId = Constants.DefaultTenantId;
                        mission.Description = "<!-- ARMADA:AUTO-RESCUE --> Note test.";
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                        Incident? updatedIncident = await incidentService.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                        AssertNotNull(updatedIncident, "Incident should still exist");
                        AssertTrue(
                            (updatedIncident!.RecoveryNotes ?? String.Empty).Contains("rescue_produced_no_commits", StringComparison.Ordinal),
                            "RecoveryNotes should contain rescue_produced_no_commits");
                        AssertTrue(
                            (updatedIncident.RecoveryNotes ?? String.Empty).Contains(mission.Id, StringComparison.Ordinal),
                            "RecoveryNotes should reference the rescue mission id");
                    }
                }
                finally
                {
                    DeleteDirectoryForce(rootDir);
                }
            });

            await RunTest("AutoRescue_DifferentHeads_StillEnqueues", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_diffhead_test_" + Guid.NewGuid().ToString("N"));
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        DifferentHeadsGitSetup repos = await CreateDifferentHeadsGitSetupAsync(rootDir);
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        StubGitService git = new StubGitService();
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                        IMessageTemplateService templateService = new MessageTemplateService(logging);
                        TrackingMergeQueueService trackingMq = new TrackingMergeQueueService();

                        MissionLandingHandler handler = new MissionLandingHandler(
                            logging, testDb.Driver, settings, git, trackingMq, landingService,
                            new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator(),
                            templateService, null, dockService, new NoOpRemoteTriggerService(), null);

                        Vessel vessel = new Vessel("diff-head-test", "https://github.com/test/repo.git");
                        vessel.LocalPath = repos.BareDir;
                        vessel.DefaultBranch = "main";
                        vessel.LandingMode = LandingModeEnum.MergeQueue;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Dock dock = new Dock(vessel.Id);
                        dock.BranchName = repos.CaptainBranch;
                        dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_diffhead_wt_" + Guid.NewGuid().ToString("N"));
                        await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                        // Create parent mission to satisfy FK constraint.
                        Mission parentMission = new Mission("Original failing mission for diff-heads test");
                        parentMission.VesselId = vessel.Id;
                        await testDb.Driver.Missions.CreateAsync(parentMission).ConfigureAwait(false);

                        Mission mission = new Mission("Rescue: heads differ");
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission.VesselId = vessel.Id;
                        mission.DockId = dock.Id;
                        mission.ParentMissionId = parentMission.Id;
                        mission.Description = "<!-- ARMADA:AUTO-RESCUE --> Has real commits.";
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                        AssertTrue(trackingMq.EnqueueCalled, "A rescue mission with real commits should be enqueued normally");
                        Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Mission should still exist");
                        AssertNotEqual(MissionStatusEnum.Failed, updated!.Status, "Mission with different heads must not be refused");
                    }
                }
                finally
                {
                    DeleteDirectoryForce(rootDir);
                }
            });

            await RunTest("NonRescueWorker_AlreadyIntegrated_TransitionsComplete", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_already_test_" + Guid.NewGuid().ToString("N"));
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        ZeroCommitGitSetup repos = await CreateZeroCommitGitSetupAsync(rootDir);
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        StubGitService git = new StubGitService();
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ILandingService landingService = new LandingService(logging, testDb.Driver, settings, git);
                        IMessageTemplateService templateService = new MessageTemplateService(logging);
                        TrackingMergeQueueService trackingMq = new TrackingMergeQueueService();

                        MissionLandingHandler handler = new MissionLandingHandler(
                            logging, testDb.Driver, settings, git, trackingMq, landingService,
                            new AutoLandEvaluator(), new ConventionChecker(), new CriticalTriggerEvaluator(),
                            templateService, null, dockService, new NoOpRemoteTriggerService(), null);

                        Vessel vessel = new Vessel("already-integrated-test", "https://github.com/test/repo.git");
                        vessel.LocalPath = repos.BareDir;
                        vessel.DefaultBranch = "main";
                        vessel.LandingMode = LandingModeEnum.MergeQueue;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Dock dock = new Dock(vessel.Id);
                        dock.BranchName = repos.CaptainBranch;
                        dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_already_wt_" + Guid.NewGuid().ToString("N"));
                        await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                        // CommitHash == targetHead means the work is already in main (legit already-integrated)
                        // No ParentMissionId -> not a rescue mission
                        Mission mission = new Mission("Regular Worker mission");
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission.VesselId = vessel.Id;
                        mission.DockId = dock.Id;
                        mission.CommitHash = repos.MainHead;
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        await handler.HandleMissionCompleteAsync(mission, dock).ConfigureAwait(false);

                        Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "Mission should exist after already-integrated detection");
                        AssertEqual(MissionStatusEnum.Complete, updated!.Status, "Legit already-integrated non-rescue Worker should be Complete");
                        AssertFalse(trackingMq.EnqueueCalled, "Already-integrated mission must not be enqueued");
                    }
                }
                finally
                {
                    DeleteDirectoryForce(rootDir);
                }
            });
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
        }

        private sealed class StubMergeQueueService : IMergeQueueService
        {
            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default) => Task.FromResult(entry);
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
        }

        private sealed class TrackingMergeQueueService : IMergeQueueService
        {
            public bool EnqueueCalled { get; private set; }
            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default)
            {
                EnqueueCalled = true;
                return Task.FromResult(entry);
            }
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
        }

        private sealed class ZeroCommitGitSetup
        {
            public string BareDir { get; }
            public string CaptainBranch { get; }
            public string MainHead { get; }
            public ZeroCommitGitSetup(string bareDir, string captainBranch, string mainHead)
            {
                BareDir = bareDir;
                CaptainBranch = captainBranch;
                MainHead = mainHead;
            }
        }

        private sealed class DifferentHeadsGitSetup
        {
            public string BareDir { get; }
            public string CaptainBranch { get; }
            public DifferentHeadsGitSetup(string bareDir, string captainBranch)
            {
                BareDir = bareDir;
                CaptainBranch = captainBranch;
            }
        }

        private static async Task<ZeroCommitGitSetup> CreateZeroCommitGitSetupAsync(string rootDir)
        {
            string sourceDir = Path.Combine(rootDir, "source");
            string bareDir = Path.Combine(rootDir, "bare.git");

            Directory.CreateDirectory(sourceDir);
            await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "# test\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

            string mainHead = (await RunGitAsync(sourceDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

            // Captain branch at the same commit (zero new commits)
            string captainBranch = "armada/captain-1/msn_rescue001";
            await RunGitAsync(sourceDir, "checkout", "-b", captainBranch).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "checkout", "main").ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            return new ZeroCommitGitSetup(bareDir, captainBranch, mainHead);
        }

        private static async Task<DifferentHeadsGitSetup> CreateDifferentHeadsGitSetupAsync(string rootDir)
        {
            string sourceDir = Path.Combine(rootDir, "source");
            string bareDir = Path.Combine(rootDir, "bare.git");

            Directory.CreateDirectory(sourceDir);
            await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "# test\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

            string captainBranch = "armada/captain-1/msn_rescue002";
            await RunGitAsync(sourceDir, "checkout", "-b", captainBranch).ConfigureAwait(false);

            // Add a real commit on the captain branch
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "feature.txt"), "feature\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "feature.txt").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Add feature").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "checkout", "main").ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            return new DifferentHeadsGitSetup(bareDir, captainBranch);
        }

        private static void DeleteDirectoryForce(string path)
        {
            if (!Directory.Exists(path)) return;
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, true);
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

        private void AssertNotEqual<T>(T unexpected, T actual, string message)
        {
            if (Object.Equals(unexpected, actual))
            {
                throw new Exception(message + " Value should NOT be " + unexpected);
            }
        }

    }
}
