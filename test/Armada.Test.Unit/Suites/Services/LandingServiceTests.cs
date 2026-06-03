namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for LandingService bounded auto-rebase retry behavior.
    /// </summary>
    public class LandingServiceTests : TestSuite
    {
        public override string Name => "Landing Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("AutoRetryLandingAsync_CleanRebase_LandsAndReturnsTrue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Clean;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxLandingRetries = 3;

                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_clean").ConfigureAwait(false);

                    service.OnPerformLanding = async (m, d) =>
                    {
                        m.Status = MissionStatusEnum.Complete;
                        await testDb.Driver.Missions.UpdateAsync(m).ConfigureAwait(false);
                    };

                    bool result = await service.AutoRetryLandingAsync(mission.Id).ConfigureAwait(false);

                    AssertTrue(result, "Clean rebase plus successful landing should return true");
                    AssertEqual(1, git.RebaseCalls.Count, "Auto-retry should rebase once");
                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, updated!.Status, "Mission should be Complete after clean-rebase retry");
                    List<ArmadaEvent> events = await ReadMissionEventsAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                    AssertEqual(1, events.Count(evt => evt.EventType == "mission.landing_auto_retry"), "Clean rebase should emit auto-retry event");
                    AssertEqual(1, events.Count(evt => evt.EventType == "mission.landing_rebase_clean"), "Clean rebase should emit clean rebase event");
                    ArmadaEvent retryEvent = events.First(evt => evt.EventType == "mission.landing_auto_retry");
                    AssertContains("\"attempt\":1", retryEvent.Payload ?? String.Empty, "Auto-retry event should include attempt number");
                    AssertContains("\"max\":3", retryEvent.Payload ?? String.Empty, "Auto-retry event should include retry bound");
                }
            });

            await RunTest("AutoRetryLandingAsync_GenuineConflict_DoesNotConsumeRetryOrInvokeLanding", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Conflict;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxLandingRetries = 1;

                    bool landingInvoked = false;
                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    service.OnPerformLanding = (m, d) =>
                    {
                        landingInvoked = true;
                        return Task.CompletedTask;
                    };

                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_conflict").ConfigureAwait(false);

                    bool first = await service.AutoRetryLandingAsync(mission.Id).ConfigureAwait(false);
                    bool second = await service.AutoRetryLandingAsync(mission.Id).ConfigureAwait(false);

                    AssertFalse(first, "Conflict should return false");
                    AssertFalse(second, "Conflict should not exhaust the bounded retry budget");
                    AssertFalse(landingInvoked, "Landing handler must not run on genuine conflict");
                    AssertEqual(2, git.RebaseCalls.Count, "Conflicts should not consume auto-retry attempts");
                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.LandingFailed, updated!.Status, "Mission should remain LandingFailed on conflict");
                    List<ArmadaEvent> events = await ReadMissionEventsAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                    AssertEqual(2, events.Count(evt => evt.EventType == "mission.landing_rebase_conflict"), "Each conflict should emit a conflict event");
                    AssertFalse(events.Any(evt => evt.EventType == "mission.landing_auto_retry"), "Conflict should not emit auto-retry attempt event");
                    AssertFalse(events.Any(evt => evt.EventType == "mission.landing_retry_exhausted"), "Conflict should not emit exhaustion event");
                }
            });

            await RunTest("AutoRetryLandingAsync_MaxRetriesReached_EmitsExhaustedWithoutRebase", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Clean;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxLandingRetries = 2;

                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_exhaust").ConfigureAwait(false);
                    service.OnPerformLanding = async (m, d) =>
                    {
                        m.Status = MissionStatusEnum.LandingFailed;
                        await testDb.Driver.Missions.UpdateAsync(m).ConfigureAwait(false);
                    };

                    bool first = await service.AutoRetryLandingAsync(mission.Id).ConfigureAwait(false);
                    bool second = await service.AutoRetryLandingAsync(mission.Id).ConfigureAwait(false);
                    bool third = await service.AutoRetryLandingAsync(mission.Id).ConfigureAwait(false);

                    AssertFalse(first, "First failed landing retry should return false");
                    AssertFalse(second, "Second failed landing retry should return false");
                    AssertFalse(third, "Exhausted retry should return false");
                    AssertEqual(2, git.RebaseCalls.Count, "Rebase should stop at MaxLandingRetries");
                    List<ArmadaEvent> events = await ReadMissionEventsAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                    AssertEqual(2, events.Count(evt => evt.EventType == "mission.landing_auto_retry"), "Only consumed attempts should emit auto-retry events");
                    AssertEqual(1, events.Count(evt => evt.EventType == "mission.landing_retry_exhausted"), "Exhaustion should emit one event");
                    ArmadaEvent exhaustedEvent = events.First(evt => evt.EventType == "mission.landing_retry_exhausted");
                    AssertContains("\"attempts\":2", exhaustedEvent.Payload ?? String.Empty, "Exhaustion event should include consumed attempts");
                    AssertContains("\"max\":2", exhaustedEvent.Payload ?? String.Empty, "Exhaustion event should include retry bound");
                }
            });

            await RunTest("AutoRetryLandingAsync_MaxRetriesZero_DisablesAutoRetry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Clean;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxLandingRetries = 0;

                    bool landingInvoked = false;
                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    service.OnPerformLanding = (m, d) =>
                    {
                        landingInvoked = true;
                        return Task.CompletedTask;
                    };

                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_zero").ConfigureAwait(false);

                    bool result = await service.AutoRetryLandingAsync(mission.Id).ConfigureAwait(false);

                    AssertFalse(result, "MaxLandingRetries=0 should disable auto-retry");
                    AssertEqual(0, git.RebaseCalls.Count, "No rebase should be attempted when auto-retry is disabled");
                    AssertFalse(landingInvoked, "Landing handler must not run when auto-retry is disabled");
                    List<ArmadaEvent> events = await ReadMissionEventsAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                    AssertEqual(0, events.Count, "Disabled auto-retry should not emit mission retry events");
                }
            });

            await RunTest("AutoRetryLandingAsync_RebaseError_DoesNotConsumeRetryOrInvokeLanding", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Error;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxLandingRetries = 1;

                    bool landingInvoked = false;
                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    service.OnPerformLanding = (m, d) =>
                    {
                        landingInvoked = true;
                        return Task.CompletedTask;
                    };

                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_rebase_error").ConfigureAwait(false);

                    bool first = await service.AutoRetryLandingAsync(mission.Id).ConfigureAwait(false);
                    bool second = await service.AutoRetryLandingAsync(mission.Id).ConfigureAwait(false);

                    AssertFalse(first, "Rebase error should return false");
                    AssertFalse(second, "Rebase error should not exhaust the bounded retry budget");
                    AssertFalse(landingInvoked, "Landing handler must not run after a rebase error");
                    AssertEqual(2, git.RebaseCalls.Count, "Rebase errors should not consume auto-retry attempts");
                    List<ArmadaEvent> events = await ReadMissionEventsAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                    AssertFalse(events.Any(evt => evt.EventType == "mission.landing_auto_retry"), "Rebase errors should not emit auto-retry attempt events");
                    AssertFalse(events.Any(evt => evt.EventType == "mission.landing_retry_exhausted"), "Rebase errors should not emit exhaustion events");
                }
            });

            await RunTest("RetryLandingAsync_CleanRebase_LandsAndReturnsTrue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Clean;
                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, new ArmadaSettings(), git);
                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_manual").ConfigureAwait(false);

                    service.OnPerformLanding = async (m, d) =>
                    {
                        m.Status = MissionStatusEnum.Complete;
                        await testDb.Driver.Missions.UpdateAsync(m).ConfigureAwait(false);
                    };

                    bool result = await service.RetryLandingAsync(mission.Id).ConfigureAwait(false);

                    AssertTrue(result, "Manual retry with clean rebase should land");
                    AssertEqual(1, git.RebaseCalls.Count, "Manual retry should rebase the mission branch");
                }
            });

            await RunTest("RetryLandingAsync_WorkProducedConflict_MarksLandingFailed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Conflict;
                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, new ArmadaSettings(), git);
                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_manual_conflict").ConfigureAwait(false);
                    mission.Status = MissionStatusEnum.WorkProduced;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                    bool result = await service.RetryLandingAsync(mission.Id).ConfigureAwait(false);

                    AssertFalse(result, "Manual retry should fail on rebase conflict");
                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.LandingFailed, updated!.Status, "WorkProduced mission should become LandingFailed after retry conflict");
                }
            });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<Mission> CreateLandingFailedMissionAsync(SqliteDatabaseDriver db, StubGitService git, string branchName)
        {
            Vessel vessel = new Vessel("landing-vessel", "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Mission mission = new Mission("Drift landing mission");
            mission.Status = MissionStatusEnum.LandingFailed;
            mission.VesselId = vessel.Id;
            mission.BranchName = branchName;
            await db.Missions.CreateAsync(mission).ConfigureAwait(false);

            git.ExistingBranches.Add(branchName);
            return mission;
        }

        private static async Task<List<ArmadaEvent>> ReadMissionEventsAsync(SqliteDatabaseDriver db, string missionId)
        {
            return await db.Events.EnumerateByMissionAsync(missionId).ConfigureAwait(false);
        }
    }
}
