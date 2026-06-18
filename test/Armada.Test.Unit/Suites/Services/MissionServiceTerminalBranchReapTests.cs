namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Covers <see cref="MissionService.ReapTerminalMissionBranchAsync"/>: a mission that ends
    /// terminal Failed/Cancelled never runs the successful-land cleanup, so its captain branch must
    /// be reaped here honoring BranchCleanupPolicy -- but never while an autonomous rescue/retry for
    /// that mission is still active (the rescue reuses or branches from the same captain branch).
    /// </summary>
    public sealed class MissionServiceTerminalBranchReapTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MissionService Terminal Branch Reap";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_reap_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_reap_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_reap_logs_" + id);
            return settings;
        }

        private MissionService CreateMissionService(SqliteDatabaseDriver db, ArmadaSettings settings, StubGitService git)
        {
            LoggingModule logging = CreateLogging();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
            return new MissionService(logging, db, settings, dockService, captainService, null, git);
        }

        // Builds an AdmiralService whose MissionService is git-injected so the terminal reap path
        // actually fires. The AdmiralServiceTests helper constructs MissionService WITHOUT a git
        // service (its _Git is null and ReapTerminalMissionBranchAsync would silently no-op), which
        // would mask the wiring under test here.
        private AdmiralService CreateAdmiralService(SqliteDatabaseDriver db, ArmadaSettings settings, StubGitService git)
        {
            LoggingModule logging = CreateLogging();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
            IMissionService missionService = new MissionService(logging, db, settings, dockService, captainService, null, git);
            IVoyageService voyageService = new VoyageService(logging, db);
            return new AdmiralService(logging, db, settings, captainService, missionService, voyageService, dockService);
        }

        private async Task<Vessel> CreateVesselAsync(
            SqliteDatabaseDriver db,
            ArmadaSettings settings,
            BranchCleanupPolicyEnum? policy)
        {
            Vessel vessel = new Vessel("reap-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/reap.git");
            vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
            vessel.WorkingDirectory = Path.Combine(settings.ReposDirectory, vessel.Name + "-work");
            vessel.DefaultBranch = "main";
            vessel.BranchCleanupPolicy = policy;
            return await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private async Task<Mission> CreateMissionAsync(
            SqliteDatabaseDriver db,
            Vessel vessel,
            MissionStatusEnum status,
            string branchName,
            string? parentMissionId = null)
        {
            Mission mission = new Mission("Terminal mission", "Work that ended " + status + ".");
            mission.VesselId = vessel.Id;
            mission.Persona = "Worker";
            mission.Status = status;
            mission.BranchName = branchName;
            mission.ParentMissionId = parentMissionId;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Reap_TerminallyFailed_NoActiveRescue_LocalAndRemote_DeletesLocalAndRemote", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                    Mission failed = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Failed, "armada/worker/reap-1").ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(failed).ConfigureAwait(false);

                    AssertTrue(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-1"),
                        "A terminally Failed mission with no active rescue must have its local captain branch reaped.");
                    AssertTrue(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-1"),
                        "Under LocalAndRemote the remote captain branch must be reaped too.");
                }
            });

            await RunTest("Reap_TerminallyCancelled_LocalOnly_DeletesLocalNotRemote", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalOnly).ConfigureAwait(false);

                    Mission cancelled = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Cancelled, "armada/worker/reap-2").ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(cancelled).ConfigureAwait(false);

                    AssertTrue(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-2"),
                        "A terminally Cancelled mission must have its local captain branch reaped.");
                    AssertFalse(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-2"),
                        "Under LocalOnly the remote captain branch must NOT be touched.");
                }
            });

            await RunTest("Reap_TerminallyFailed_NullVesselPolicy_DefaultLocalAndRemote_DeletesLocalAndRemote", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, null).ConfigureAwait(false);

                    Mission failed = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Failed, "armada/worker/reap-default").ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(failed).ConfigureAwait(false);

                    AssertTrue(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-default"),
                        "A null vessel policy must fall back to the global default and reap the local branch.");
                    AssertTrue(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-default"),
                        "The default BranchCleanupPolicy is LocalAndRemote, so the remote branch must also be reaped.");
                }
            });

            await RunTest("Reap_FailedWithActiveRescue_RetainsBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                    Mission failed = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Failed, "armada/worker/reap-3").ConfigureAwait(false);
                    // An autonomous rescue is still in flight (ParentMissionId links back, non-terminal).
                    await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.InProgress, "armada/rescue/reap-3", parentMissionId: failed.Id).ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(failed).ConfigureAwait(false);

                    AssertFalse(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-3"),
                        "The don't-reap-while-rescuing guard must retain the branch while an active rescue depends on the mission.");
                    AssertFalse(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-3"),
                        "No remote delete may fire while an active rescue is in flight.");
                }
            });

            await RunTest("Reap_FailedWithActiveSameBranchSibling_RetainsBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                    Mission failed = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Failed, "armada/worker/reap-sibling").ConfigureAwait(false);
                    await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Pending, "armada/worker/reap-sibling").ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(failed).ConfigureAwait(false);

                    AssertFalse(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-sibling"),
                        "The same-branch guard must retain the branch while another non-terminal mission still references it.");
                    AssertFalse(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-sibling"),
                        "No remote delete may fire while a same-branch sibling is still active.");
                }
            });

            await RunTest("Reap_PolicyNone_RetainsBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.None).ConfigureAwait(false);

                    Mission failed = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Failed, "armada/worker/reap-4").ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(failed).ConfigureAwait(false);

                    AssertFalse(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-4"),
                        "Under policy None the captain branch must be retained.");
                    AssertFalse(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-4"),
                        "Under policy None no remote delete may fire.");
                }
            });

            await RunTest("Reap_NonTerminalMission_NoOp", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                    Mission inProgress = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.InProgress, "armada/worker/reap-5").ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(inProgress).ConfigureAwait(false);

                    AssertEqual(0, git.OperationCalls.Count,
                        "A non-terminal mission must never trigger any branch deletion.");
                }
            });

            // Regression: drive a REAL terminal transition (not a direct ReapTerminalMissionBranchAsync
            // call) and prove the branch is reaped. A FailPipeline review denial transitions the mission
            // to Failed via DenyReviewAsync, which must route through the reap path. Removing the reap hook
            // from DenyReviewAsync makes this fail -- it has teeth against the lifecycle integration, which
            // the direct-call tests above cannot prove.
            await RunTest("DenyReviewFailPipeline_RealTransition_ReapsBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                    Mission review = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Review, "armada/worker/reap-deny").ConfigureAwait(false);
                    review.RequiresReview = true;
                    review.ReviewDenyAction = ReviewDenyActionEnum.FailPipeline;
                    await testDb.Driver.Missions.UpdateAsync(review).ConfigureAwait(false);

                    Mission denied = await missions.DenyReviewAsync(review.Id, null, "Rework required.").ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Failed, denied.Status,
                        "A FailPipeline review denial must transition the mission to terminal Failed.");
                    AssertTrue(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-deny"),
                        "A FailPipeline review denial is a real terminal transition and must reap the local captain branch.");
                    AssertTrue(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-deny"),
                        "Under LocalAndRemote the remote captain branch must be reaped on a FailPipeline denial.");
                }
            });

            // Regression: the idempotency guard the judge flagged. A caller (process-exit failure path)
            // may persist terminal Failed BEFORE invoking the completion handler, so the in-flow terminal
            // reap block never runs for it. Drive a REAL HandleCompletionAsync call against an already-Failed
            // mission and prove the early-return guard still reaps the branch. Removing the reap call from
            // the guard block makes this fail.
            await RunTest("HandleCompletion_PrePersistedFailed_GuardReapsBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                    // Mission was already marked terminal Failed by an out-of-band writer before the
                    // completion handler runs -- this is the early-return ("already in post-work state") case.
                    Mission failed = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Failed, "armada/worker/reap-guard").ConfigureAwait(false);

                    Captain captain = new Captain("guard-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = failed.Id;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await missions.HandleCompletionAsync(captain, failed.Id).ConfigureAwait(false);

                    AssertTrue(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-guard"),
                        "The idempotency-guard early-return must still reap the branch of a pre-persisted terminal mission.");
                    AssertTrue(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-guard"),
                        "Under LocalAndRemote the guard reap must remove the remote branch too.");
                }
            });

            // Regression: drive a REAL AdmiralService process-exit terminal transition. A non-zero exit
            // with a generic (non-captain-unavailable) failure reason marks the mission Failed inside
            // HandleTerminalProcessExitFailureAsync, which must route through the reap seam. This is the
            // only terminal writer outside MissionService, and the direct-call tests cannot prove it.
            await RunTest("ProcessExit_NonZeroExit_TerminalFailure_ReapsBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    StubGitService git = new StubGitService();
                    AdmiralService admiral = CreateAdmiralService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                    // No VoyageId: keeps the failure from halting a voyage, isolating the reap behavior.
                    // No mission log file: BuildProcessExitFailureReasonAsync returns the generic
                    // "Agent process exited with code 1" reason, which is NOT captain-unavailable, so the
                    // path is a genuine terminal Failed (not a transient requeue).
                    Mission mission = new Mission("Process-exit terminal mission");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Worker";
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.BranchName = "armada/worker/reap-exit";
                    mission.ProcessId = 53117;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("exit-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 53117;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(53117, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Mission should still exist after process exit handling.");
                    AssertEqual(MissionStatusEnum.Failed, updated!.Status,
                        "A non-zero process exit with a generic failure reason must mark the mission terminal Failed.");
                    AssertTrue(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-exit"),
                        "A terminal process-exit failure must reap the local captain branch via the new seam.");
                    AssertTrue(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-exit"),
                        "Under LocalAndRemote the process-exit terminal failure must reap the remote branch too.");
                }
            });

            // Pins the default-policy flip that ships with this change (LocalOnly -> LocalAndRemote).
            // The null-vessel-policy reap test relies on this default; this asserts the default directly
            // so a silent revert is caught even if vessel-level policy masks it elsewhere.
            await RunTest("Settings_DefaultBranchCleanupPolicy_IsLocalAndRemote", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertEqual(BranchCleanupPolicyEnum.LocalAndRemote, settings.BranchCleanupPolicy,
                    "The global default BranchCleanupPolicy must be LocalAndRemote so terminal branches are reaped from the remote by default.");
                return Task.CompletedTask;
            });
        }
    }
}
