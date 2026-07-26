namespace Armada.Test.Unit
{
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Verifies the real-signal completion gate in UpdateVoyageTerminalStatusAsync: a voyage may only
    /// reach Complete when its Checks are green. A failed Check overrides a Judge PASS (voyage Fails);
    /// a pending Check holds completion; a voyage with no Checks is unaffected (backward compatible).
    /// </summary>
    public sealed class JudgeChecksGateTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "JudgeChecksGate";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_gate_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_gate_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        /// <summary>Seeds a voyage whose missions are all terminal (Worker WorkProduced + Judge Complete,
        /// so the Judge "passed") and returns the service + the still-InProgress voyage.</summary>
        private async Task<(MissionService svc, Voyage voyage)> SeedJudgePassedVoyageAsync(TestDatabase testDb)
        {
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = CreateSettings();
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
            MissionService svc = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git);

            Vessel vessel = new Vessel("gate-vessel", "https://github.com/test/repo.git");
            vessel.DefaultBranch = "main";
            vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Voyage voyage = new Voyage("gate-voyage");
            voyage.Status = VoyageStatusEnum.InProgress;
            voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission worker = new Mission("[Worker] Implement", "worker description");
            worker.VesselId = vessel.Id;
            worker.VoyageId = voyage.Id;
            worker.Persona = "Worker";
            worker.Status = MissionStatusEnum.WorkProduced;
            await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

            Mission judge = new Mission("[Judge] Review", "judge description");
            judge.VesselId = vessel.Id;
            judge.VoyageId = voyage.Id;
            judge.Persona = "Judge";
            judge.Status = MissionStatusEnum.Complete;
            await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

            return (svc, voyage);
        }

        private async Task AddCheckAsync(TestDatabase testDb, string voyageId, CheckRunStatusEnum status)
        {
            CheckRun run = new CheckRun
            {
                VoyageId = voyageId,
                Label = "Build",
                Type = CheckRunTypeEnum.Build,
                Source = CheckRunSourceEnum.Armada,
                Status = status,
                Command = "dotnet build",
                WorkingDirectory = "C:/temp",
                ExitCode = status == CheckRunStatusEnum.Passed ? 0 : 1,
                Output = status == CheckRunStatusEnum.Passed ? "Build succeeded." : "Build failed.",
                Summary = "check"
            };
            await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("AllChecksGreen_VoyageCompletes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedJudgePassedVoyageAsync(testDb).ConfigureAwait(false);
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Passed).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, after!.Status, "green Checks -> voyage Complete");
                }
            }).ConfigureAwait(false);

            await RunTest("FailedCheck_OverridesJudgePass_VoyageFails", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedJudgePassedVoyageAsync(testDb).ConfigureAwait(false);
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Failed).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Failed, after!.Status, "a failed Check overrides the Judge PASS -> voyage Failed");
                }
            }).ConfigureAwait(false);

            await RunTest("PendingCheck_HoldsCompletion", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedJudgePassedVoyageAsync(testDb).ConfigureAwait(false);
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Pending).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.InProgress, after!.Status, "a pending Check holds completion -> voyage not Complete");
                }
            }).ConfigureAwait(false);

            await RunTest("NoChecks_VoyageCompletes_BackwardCompatible", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedJudgePassedVoyageAsync(testDb).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, after!.Status, "no Checks -> voyage Complete (backward compatible)");
                }
            }).ConfigureAwait(false);
        }
    }
}
