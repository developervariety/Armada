namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Item 8 PR-fallback M5 pin: a downstream mission whose upstream landed in
    /// PullRequestOpen (PR opened, not yet merged) should still be eligible for
    /// assignment so it can chain off the upstream captain branch instead of
    /// blocking on PR merge. Same-vessel only -- cross-vessel deps still require
    /// Complete (covered separately by CrossVesselDependencyTests).
    /// </summary>
    public class PrFallbackUnblockTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "PR-Fallback Dependent Unblock";

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

        private async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string name)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private async Task<Captain> CreateIdleCaptainAsync(TestDatabase testDb, string name)
        {
            Captain captain = new Captain(name);
            captain.State = CaptainStateEnum.Idle;
            return await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private MissionService BuildMissionService(TestDatabase testDb, LoggingModule logging, ArmadaSettings settings)
        {
            StubGitService git = new StubGitService();
            DockService docks = new DockService(logging, testDb.Driver, settings, git);
            CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
            captains.OnLaunchAgent = (_, _, _) => Task.FromResult(54321);
            return new MissionService(logging, testDb.Driver, settings, docks, captains);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Dependent_UpstreamPullRequestOpen_AssignsAndChains", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = BuildMissionService(testDb, logging, settings);

                    Vessel vessel = await CreateVesselAsync(testDb, "pr-unblock-vessel").ConfigureAwait(false);
                    Captain captain = await CreateIdleCaptainAsync(testDb, "captain-pr-1").ConfigureAwait(false);

                    Mission upstream = new Mission("upstream", "shipped via PR-fallback");
                    upstream.VesselId = vessel.Id;
                    upstream.Status = MissionStatusEnum.PullRequestOpen;
                    upstream.BranchName = "armada/captain/feat-critical";
                    upstream = await testDb.Driver.Missions.CreateAsync(upstream).ConfigureAwait(false);

                    Mission downstream = new Mission("downstream", "follow-up that needs upstream");
                    downstream.VesselId = vessel.Id;
                    downstream.DependsOnMissionId = upstream.Id;
                    downstream.Status = MissionStatusEnum.Pending;
                    downstream = await testDb.Driver.Missions.CreateAsync(downstream).ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(downstream, vessel).ConfigureAwait(false);
                    AssertTrue(assigned, "Downstream must assign even though upstream is still PullRequestOpen");

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                    AssertTrue(readBack!.Status == MissionStatusEnum.Assigned || readBack.Status == MissionStatusEnum.InProgress,
                        "Downstream should be Assigned or InProgress, got: " + readBack.Status);
                    AssertEqual(captain.Id, readBack.CaptainId);
                }
            });

            await RunTest("Dependent_UpstreamPending_DoesNotAssign", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = BuildMissionService(testDb, logging, settings);

                    Vessel vessel = await CreateVesselAsync(testDb, "pr-pending-vessel").ConfigureAwait(false);
                    await CreateIdleCaptainAsync(testDb, "captain-pr-2").ConfigureAwait(false);

                    Mission upstream = new Mission("upstream-pending", "");
                    upstream.VesselId = vessel.Id;
                    upstream.Status = MissionStatusEnum.Pending;
                    upstream = await testDb.Driver.Missions.CreateAsync(upstream).ConfigureAwait(false);

                    Mission downstream = new Mission("downstream-pending", "");
                    downstream.VesselId = vessel.Id;
                    downstream.DependsOnMissionId = upstream.Id;
                    downstream.Status = MissionStatusEnum.Pending;
                    downstream = await testDb.Driver.Missions.CreateAsync(downstream).ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(downstream, vessel).ConfigureAwait(false);
                    AssertFalse(assigned,
                        "Downstream must defer when upstream is still Pending (regression: must NOT match PullRequestOpen unblock too broadly)");
                }
            });
        }
    }
}
