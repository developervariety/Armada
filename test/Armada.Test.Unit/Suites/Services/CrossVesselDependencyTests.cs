namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
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
    /// Verifies cross-vessel DependsOnMissionId behaviour in MissionService.TryAssignAsync:
    /// a downstream mission whose dep lives on a different vessel must wait for Complete
    /// (not WorkProduced), must not inherit the upstream's branch, and must not run the
    /// same-vessel handoff check. Same-vessel pipeline behaviour stays unchanged.
    /// </summary>
    public class CrossVesselDependencyTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Cross-Vessel DependsOnMissionId";

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
            Vessel vessel = new Vessel(name, "https://github.com/test/" + name + ".git");
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

        private MissionService BuildMissionService(TestDatabase testDb, LoggingModule logging, ArmadaSettings settings, out StubGitService git)
        {
            git = new StubGitService();
            DockService docks = new DockService(logging, testDb.Driver, settings, git);
            CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
            captains.OnLaunchAgent = (_, _, _) => Task.FromResult(54321);
            return new MissionService(logging, testDb.Driver, settings, docks, captains);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CrossVessel_WorkProducedDep_DefersAssignment", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = BuildMissionService(testDb, logging, settings, out _);

                    Vessel upstreamVessel = await CreateVesselAsync(testDb, "upstream-cross").ConfigureAwait(false);
                    Vessel downstreamVessel = await CreateVesselAsync(testDb, "downstream-cross").ConfigureAwait(false);
                    await CreateIdleCaptainAsync(testDb, "captain-cross-1").ConfigureAwait(false);

                    // Upstream mission stays at WorkProduced (the merge queue hasn't landed it yet).
                    Mission upstream = new Mission("upstream", "did the work");
                    upstream.VesselId = upstreamVessel.Id;
                    upstream.Status = MissionStatusEnum.WorkProduced;
                    upstream.BranchName = "armada/captain/upstream";
                    upstream = await testDb.Driver.Missions.CreateAsync(upstream).ConfigureAwait(false);

                    // Downstream depends on upstream but lives on a different vessel.
                    Mission downstream = new Mission("downstream", "needs upstream");
                    downstream.VesselId = downstreamVessel.Id;
                    downstream.DependsOnMissionId = upstream.Id;
                    downstream.Status = MissionStatusEnum.Pending;
                    downstream = await testDb.Driver.Missions.CreateAsync(downstream).ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(downstream, downstreamVessel).ConfigureAwait(false);
                    AssertFalse(assigned, "Cross-vessel dep at WorkProduced must defer the downstream assignment");

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, readBack!.Status, "Downstream stays Pending");
                    AssertNull(readBack.CaptainId, "No captain should be assigned");
                    AssertNull(readBack.BranchName, "No branch should be inherited or created");
                }
            });

            await RunTest("CrossVessel_CompleteDep_AssignsWithFreshBranchOnDownstreamVessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = BuildMissionService(testDb, logging, settings, out StubGitService git);

                    Vessel upstreamVessel = await CreateVesselAsync(testDb, "upstream-complete").ConfigureAwait(false);
                    Vessel downstreamVessel = await CreateVesselAsync(testDb, "downstream-complete").ConfigureAwait(false);
                    Captain captain = await CreateIdleCaptainAsync(testDb, "captain-cross-2").ConfigureAwait(false);

                    Mission upstream = new Mission("upstream-c", "shipped");
                    upstream.VesselId = upstreamVessel.Id;
                    upstream.Status = MissionStatusEnum.Complete;
                    upstream.BranchName = "armada/captain/upstream-c";
                    upstream = await testDb.Driver.Missions.CreateAsync(upstream).ConfigureAwait(false);

                    Mission downstream = new Mission("downstream-c", "needs upstream");
                    downstream.VesselId = downstreamVessel.Id;
                    downstream.DependsOnMissionId = upstream.Id;
                    downstream.Status = MissionStatusEnum.Pending;
                    // Pre-populate with the upstream's branch name to verify the cross-vessel
                    // path does NOT inherit it.
                    downstream.BranchName = "armada/captain/upstream-c";
                    downstream = await testDb.Driver.Missions.CreateAsync(downstream).ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(downstream, downstreamVessel).ConfigureAwait(false);
                    AssertTrue(assigned, "Cross-vessel dep at Complete should let the downstream assign");

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                    AssertTrue(readBack!.Status == MissionStatusEnum.Assigned || readBack.Status == MissionStatusEnum.InProgress,
                        "Downstream should be Assigned or InProgress after launch, got: " + readBack.Status);
                    AssertEqual(captain.Id, readBack.CaptainId, "Idle captain should pick up the downstream");
                    AssertNotNull(readBack.BranchName, "Downstream should have a branch");
                    AssertFalse(readBack.BranchName == "armada/captain/upstream-c",
                        "Cross-vessel downstream must NOT inherit the upstream's branch (different repos cannot share branches)");
                    AssertContains(readBack.Id, readBack.BranchName!,
                        "Fresh branch should embed the downstream mission id");
                }
            });

            await RunTest("SameVessel_WorkProducedDep_StillRequiresHandoff", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = BuildMissionService(testDb, logging, settings, out _);

                    Vessel vessel = await CreateVesselAsync(testDb, "same-vessel").ConfigureAwait(false);
                    await CreateIdleCaptainAsync(testDb, "captain-same-1").ConfigureAwait(false);

                    Mission upstream = new Mission("upstream", "did work");
                    upstream.VesselId = vessel.Id;
                    upstream.Status = MissionStatusEnum.WorkProduced;
                    upstream.BranchName = "armada/captain/feat-x";
                    upstream = await testDb.Driver.Missions.CreateAsync(upstream).ConfigureAwait(false);

                    Mission downstream = new Mission("downstream", "judge");
                    downstream.VesselId = vessel.Id;
                    downstream.DependsOnMissionId = upstream.Id;
                    downstream.Status = MissionStatusEnum.Pending;
                    // BranchName not yet populated -> handoff not prepared.
                    downstream = await testDb.Driver.Missions.CreateAsync(downstream).ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(downstream, vessel).ConfigureAwait(false);
                    AssertFalse(assigned,
                        "Same-vessel WorkProduced dep without handoff prep must still defer (existing behaviour)");
                }
            });

            await RunTest("SameVessel_WorkProducedDep_BranchHandoffReady_AssignsAndInheritsBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = BuildMissionService(testDb, logging, settings, out _);

                    Vessel vessel = await CreateVesselAsync(testDb, "same-vessel-handoff").ConfigureAwait(false);
                    Captain captain = await CreateIdleCaptainAsync(testDb, "captain-same-2").ConfigureAwait(false);

                    Mission upstream = new Mission("upstream-h", "worker shipped");
                    upstream.VesselId = vessel.Id;
                    upstream.Status = MissionStatusEnum.WorkProduced;
                    upstream.BranchName = "armada/captain-same-2/feat-y";
                    upstream = await testDb.Driver.Missions.CreateAsync(upstream).ConfigureAwait(false);

                    Mission downstream = new Mission("downstream-h", "judge stage");
                    downstream.VesselId = vessel.Id;
                    downstream.DependsOnMissionId = upstream.Id;
                    downstream.Persona = "Judge";
                    downstream.Status = MissionStatusEnum.Pending;
                    // Handoff prep: same branch already populated on the downstream.
                    downstream.BranchName = "armada/captain-same-2/feat-y";
                    downstream = await testDb.Driver.Missions.CreateAsync(downstream).ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(downstream, vessel).ConfigureAwait(false);
                    AssertTrue(assigned, "Same-vessel handoff-prepared downstream should assign at WorkProduced");

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                    AssertTrue(readBack!.Status == MissionStatusEnum.Assigned || readBack.Status == MissionStatusEnum.InProgress,
                        "Downstream should be Assigned or InProgress after launch, got: " + readBack.Status);
                    AssertEqual(captain.Id, readBack.CaptainId);
                    AssertEqual("armada/captain-same-2/feat-y", readBack.BranchName,
                        "Same-vessel pipeline downstream must inherit the upstream branch (existing behaviour)");
                }
            });
        }
    }
}
