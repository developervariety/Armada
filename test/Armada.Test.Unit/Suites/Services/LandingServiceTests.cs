namespace Armada.Test.Unit.Suites.Services
{
    using System.IO;
    using System.Linq;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for LandingService bounded auto-rebase retry on target-branch drift.
    /// Uses StubGitService (no real git) and a real SQLite test database; the landing
    /// handler is simulated via the OnPerformLanding delegate.
    /// </summary>
    public class LandingServiceTests : TestSuite
    {
        public override string Name => "Landing Service";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private async Task<Mission> CreateLandingFailedMissionAsync(SqliteDatabaseDriver db, StubGitService git, string branchName)
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

        private async Task<List<ArmadaEvent>> ReadMissionEventsAsync(SqliteDatabaseDriver db, string missionId)
        {
            List<ArmadaEvent> events = await db.Events.EnumerateRecentAsync(100).ConfigureAwait(false);
            return events.Where(evt => evt.MissionId == missionId).ToList();
        }

        private void AssertMissionEvent(List<ArmadaEvent> events, string eventType)
        {
            Assert(events.Any(evt => evt.EventType == eventType), "Expected mission event " + eventType);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("AutoRetryLandingAsync_CleanRebase_LandsAndReturnsTrue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Clean;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxLandingRetries = 3;

                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_clean");

                    service.OnPerformLanding = async (m, d) =>
                    {
                        m.Status = MissionStatusEnum.Complete;
                        await testDb.Driver.Missions.UpdateAsync(m);
                    };

                    bool result = await service.AutoRetryLandingAsync(mission.Id);

                    AssertTrue(result, "Clean rebase + successful landing should return true");
                    AssertEqual(1, git.RebaseCalls.Count, "Rebase should be attempted once");
                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionStatusEnum.Complete, updated!.Status, "Mission should be Complete after clean-rebase retry");
                    List<ArmadaEvent> events = await ReadMissionEventsAsync(testDb.Driver, mission.Id);
                    AssertMissionEvent(events, "mission.landing_auto_retry");
                    AssertMissionEvent(events, "mission.landing_rebase_clean");
                    ArmadaEvent retryEvent = events.First(evt => evt.EventType == "mission.landing_auto_retry");
                    AssertContains("\"attempt\":1", retryEvent.Payload ?? String.Empty, "Auto-retry event should include attempt number");
                    AssertContains("\"max\":3", retryEvent.Payload ?? String.Empty, "Auto-retry event should include retry bound");
                }
            });

            await RunTest("AutoRetryLandingAsync_GenuineConflict_AbortsAndDoesNotConsumeRetry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Conflict;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxLandingRetries = 1;

                    bool landingInvoked = false;
                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    service.OnPerformLanding = (m, d) => { landingInvoked = true; return Task.CompletedTask; };

                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_conflict");

                    bool first = await service.AutoRetryLandingAsync(mission.Id);
                    bool second = await service.AutoRetryLandingAsync(mission.Id);

                    AssertFalse(first, "Genuine conflict should return false");
                    AssertFalse(second, "Genuine conflict should still return false");
                    AssertFalse(landingInvoked, "Landing handler must not run on a genuine conflict");
                    // A conflict does not consume a retry, so both calls re-attempt the rebase
                    // even though MaxLandingRetries is 1.
                    AssertEqual(2, git.RebaseCalls.Count, "Conflict must not exhaust the retry budget");
                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionStatusEnum.LandingFailed, updated!.Status, "Mission should remain LandingFailed on conflict");
                    List<ArmadaEvent> events = await ReadMissionEventsAsync(testDb.Driver, mission.Id);
                    AssertEqual(2, events.Count(evt => evt.EventType == "mission.landing_rebase_conflict"), "Each conflict should emit conflict event without consuming retry budget");
                    AssertFalse(events.Any(evt => evt.EventType == "mission.landing_auto_retry"), "Conflict should not emit auto-retry attempt event");
                }
            });

            await RunTest("AutoRetryLandingAsync_BoundReached_ExhaustsAndReturnsFalse", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Clean;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxLandingRetries = 2;

                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_exhaust");

                    // Landing never reaches Complete: each clean-rebase retry is consumed but fails to land.
                    service.OnPerformLanding = async (m, d) =>
                    {
                        m.Status = MissionStatusEnum.LandingFailed;
                        await testDb.Driver.Missions.UpdateAsync(m);
                    };

                    bool a1 = await service.AutoRetryLandingAsync(mission.Id);
                    bool a2 = await service.AutoRetryLandingAsync(mission.Id);
                    bool a3 = await service.AutoRetryLandingAsync(mission.Id);

                    AssertFalse(a1, "First attempt fails to land");
                    AssertFalse(a2, "Second attempt fails to land");
                    AssertFalse(a3, "Third attempt should be rejected (budget exhausted)");
                    // Only 2 retries allowed: the third call short-circuits before rebasing.
                    AssertEqual(2, git.RebaseCalls.Count, "Rebase attempted only up to MaxLandingRetries");
                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionStatusEnum.LandingFailed, updated!.Status, "Mission stays LandingFailed when exhausted");
                    List<ArmadaEvent> events = await ReadMissionEventsAsync(testDb.Driver, mission.Id);
                    AssertEqual(2, events.Count(evt => evt.EventType == "mission.landing_auto_retry"), "Only successful drift rebase attempts should emit auto-retry events");
                    AssertEqual(2, events.Count(evt => evt.EventType == "mission.landing_rebase_clean"), "Only attempted clean rebases should emit clean rebase events");
                    AssertEqual(1, events.Count(evt => evt.EventType == "mission.landing_retry_exhausted"), "Exhaustion should emit one bounded-retry event");
                    ArmadaEvent exhaustedEvent = events.First(evt => evt.EventType == "mission.landing_retry_exhausted");
                    AssertContains("\"attempts\":2", exhaustedEvent.Payload ?? String.Empty, "Exhaustion event should include consumed attempts");
                    AssertContains("\"max\":2", exhaustedEvent.Payload ?? String.Empty, "Exhaustion event should include retry bound");
                }
            });

            await RunTest("AutoRetryLandingAsync_RebaseError_DoesNotConsumeRetryOrInvokeLanding", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Error;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxLandingRetries = 1;

                    bool landingInvoked = false;
                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    service.OnPerformLanding = (m, d) => { landingInvoked = true; return Task.CompletedTask; };

                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_rebase_error");

                    bool first = await service.AutoRetryLandingAsync(mission.Id);
                    bool second = await service.AutoRetryLandingAsync(mission.Id);

                    AssertFalse(first, "Rebase error should return false");
                    AssertFalse(second, "Rebase error should not consume the retry budget");
                    AssertFalse(landingInvoked, "Landing handler must not run after a rebase error");
                    AssertEqual(2, git.RebaseCalls.Count, "Rebase errors should not exhaust the bounded auto-retry budget");
                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionStatusEnum.LandingFailed, updated!.Status, "Mission should remain LandingFailed on rebase error");
                    List<ArmadaEvent> events = await ReadMissionEventsAsync(testDb.Driver, mission.Id);
                    AssertFalse(events.Any(evt => evt.EventType == "mission.landing_auto_retry"), "Rebase errors should not emit auto-retry attempt events");
                    AssertFalse(events.Any(evt => evt.EventType == "mission.landing_retry_exhausted"), "Rebase errors should not emit exhaustion events");
                }
            });

            await RunTest("AutoRetryLandingAsync_MaxRetriesZero_NoAutoRetry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Clean;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.MaxLandingRetries = 0;

                    bool landingInvoked = false;
                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    service.OnPerformLanding = (m, d) => { landingInvoked = true; return Task.CompletedTask; };

                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_zero");

                    bool result = await service.AutoRetryLandingAsync(mission.Id);

                    AssertFalse(result, "MaxLandingRetries=0 should disable auto-retry");
                    AssertEqual(0, git.RebaseCalls.Count, "No rebase should be attempted when auto-retry disabled");
                    AssertFalse(landingInvoked, "Landing handler must not run when auto-retry disabled");
                    List<ArmadaEvent> events = await ReadMissionEventsAsync(testDb.Driver, mission.Id);
                    AssertEqual(0, events.Count, "Disabled auto-retry should not emit mission retry events");
                }
            });

            await RunTest("RetryLandingAsync_CleanRebase_LandsAndReturnsTrue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Clean;
                    ArmadaSettings settings = new ArmadaSettings();

                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_manual");

                    service.OnPerformLanding = async (m, d) =>
                    {
                        m.Status = MissionStatusEnum.Complete;
                        await testDb.Driver.Missions.UpdateAsync(m);
                    };

                    bool result = await service.RetryLandingAsync(mission.Id);

                    AssertTrue(result, "Manual retry with clean rebase should land");
                    AssertEqual(1, git.RebaseCalls.Count, "Manual retry should rebase the mission branch");
                }
            });

            await RunTest("RetryLandingAsync_GenuineConflict_ReturnsFalseAndLandingFailed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    git.RebaseOutcomeResult = RebaseOutcomeEnum.Conflict;
                    ArmadaSettings settings = new ArmadaSettings();

                    bool landingInvoked = false;
                    LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, git);
                    service.OnPerformLanding = (m, d) => { landingInvoked = true; return Task.CompletedTask; };

                    Mission mission = await CreateLandingFailedMissionAsync(testDb.Driver, git, "armada/test/msn_manualconflict");

                    bool result = await service.RetryLandingAsync(mission.Id);

                    AssertFalse(result, "Manual retry should fail on genuine conflict");
                    AssertFalse(landingInvoked, "Landing handler must not run on conflict");
                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionStatusEnum.LandingFailed, updated!.Status, "Mission should remain LandingFailed on conflict");
                }
            });
        }
    }
}
