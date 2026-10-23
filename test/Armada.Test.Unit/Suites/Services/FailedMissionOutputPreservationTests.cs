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
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;

    /// <summary>
    /// Covers the terminal process-exit failure path in <see cref="AdmiralService"/>: a stage that exits
    /// non-zero with partial output must keep that captured output on its mission, so the output artifact
    /// reports the real content instead of an empty <c>output_unavailable</c> page that forces a downstream
    /// stage or rescue onto the weaker stored description.
    /// </summary>
    public sealed class FailedMissionOutputPreservationTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Failed Mission Output Preservation";

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
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_failout_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_failout_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_failout_logs_" + id);
            settings.MinIdleCaptains = 0;
            return settings;
        }

        // Builds an AdmiralService whose MissionService carries a captured-output hook, so the terminal
        // process-exit failure path can drain the partial stdout the same way the completion path does.
        private AdmiralService CreateAdmiralService(SqliteDatabaseDriver db, ArmadaSettings settings, StubGitService git, Func<string, string?> capturedOutput)
        {
            LoggingModule logging = CreateLogging();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
            IMissionService missionService = new MissionService(logging, db, settings, dockService, captainService, null, git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            missionService.OnGetMissionOutput = capturedOutput;
            IVoyageService voyageService = new VoyageService(logging, db);
            return new AdmiralService(logging, db, settings, captainService, missionService, voyageService, dockService);
        }

        private async Task<Vessel> CreateVesselAsync(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            Vessel vessel = new Vessel("failout-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/failout.git");
            vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
            vessel.WorkingDirectory = Path.Combine(settings.ReposDirectory, vessel.Name + "-work");
            vessel.DefaultBranch = "main";
            return await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            // A Worker that produced some stdout and then exited non-zero used to finalize with no output:
            // AgentOutput stayed null and the output artifact reported a zero-character page with
            // truncation output_unavailable, so a downstream stage or rescue lost the partial work. Drive a
            // real non-zero process exit with a captured partial stream and prove the output is preserved.
            await RunTest("NonZeroExit_WithCapturedOutput_PreservesOutputInArtifact", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    string captured = "partial build log before the stage crashed\n"
                        + "  compiling module one\n  compiling module two\n"
                        + "fatal: unexpected end of stream";
                    AdmiralService admiral = CreateAdmiralService(testDb.Driver, settings, git, _ => captured);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    // No VoyageId: the failure does not halt a voyage, isolating the finalize behavior.
                    // No mission log file: the failure reason is the generic non-zero exit, a genuine
                    // terminal Failed rather than a transient requeue.
                    Mission mission = new Mission("Process-exit terminal mission");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Worker";
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.BranchName = "armada/worker/failout-1";
                    mission.ProcessId = 53211;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("failout-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 53211;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(53211, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Mission should still exist after process exit handling.");
                    AssertEqual(MissionStatusEnum.Failed, updated!.Status,
                        "A non-zero process exit with a generic failure reason must mark the mission terminal Failed.");
                    AssertFalse(String.IsNullOrEmpty(updated.AgentOutput),
                        "A failed stage that produced output must keep that captured output on the mission, not finalize empty.");
                    AssertContains("fatal: unexpected end of stream", updated.AgentOutput!);

                    MissionOutputArtifactPage page = MissionOutputArtifact.Build(updated);
                    AssertTrue(page.Finalized, "A terminal Failed mission's output artifact is finalized.");
                    AssertTrue(page.TotalLength > 0, "The artifact must report the real character count of the captured output, not zero.");
                    AssertContains("fatal: unexpected end of stream", page.Content);
                    AssertFalse(String.Equals(page.TruncationReason, "output_unavailable", StringComparison.Ordinal),
                        "output_unavailable must mean genuinely no output; a failed stage with real output must not read as empty.");
                }
            });

            // output_unavailable still means genuinely-no-output: a failed stage that produced nothing keeps
            // the empty-with-reason page, distinct from a finalized mission with real captured output.
            await RunTest("NonZeroExit_NoCapturedOutput_StillReportsOutputUnavailable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    AdmiralService admiral = CreateAdmiralService(testDb.Driver, settings, git, _ => null);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission mission = new Mission("Process-exit terminal mission, no output");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Worker";
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.BranchName = "armada/worker/failout-2";
                    mission.ProcessId = 53212;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("failout-captain-2");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 53212;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(53212, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Mission should still exist after process exit handling.");
                    AssertEqual(MissionStatusEnum.Failed, updated!.Status, "A non-zero process exit must mark the mission terminal Failed.");

                    MissionOutputArtifactPage page = MissionOutputArtifact.Build(updated);
                    AssertEqual(0, page.TotalLength, "A failed stage with no captured output has no content.");
                    AssertEqual("output_unavailable", page.TruncationReason,
                        "A finalized failed stage that genuinely captured nothing still reads as output_unavailable.");
                }
            });
        }
    }
}
