namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// Tests for the Ask Armada status answers that count missions by status.
    /// </summary>
    public class AskArmadaServiceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Ask Armada Service";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("The failure and mission answers count every status correctly, whatever the rows carry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_ask_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_ask_repos_" + Guid.NewGuid().ToString("N"));
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    IMissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captains, missions, new VoyageService(logging, testDb.Driver), docks, git: git);
                    AskArmadaService ask = new AskArmadaService(testDb.Driver, admiral, logging);

                    // One failed row carries a large diff, the shape that made a full-row read expensive.
                    await CreateAsync(testDb, MissionStatusEnum.Failed, new string('d', 1024 * 1024)).ConfigureAwait(false);
                    await CreateAsync(testDb, MissionStatusEnum.Failed, null).ConfigureAwait(false);
                    await CreateAsync(testDb, MissionStatusEnum.Failed, null).ConfigureAwait(false);
                    await CreateAsync(testDb, MissionStatusEnum.LandingFailed, null).ConfigureAwait(false);
                    await CreateAsync(testDb, MissionStatusEnum.Pending, null).ConfigureAwait(false);
                    await CreateAsync(testDb, MissionStatusEnum.Pending, null).ConfigureAwait(false);
                    await CreateAsync(testDb, MissionStatusEnum.InProgress, null).ConfigureAwait(false);
                    await CreateAsync(testDb, MissionStatusEnum.Review, null).ConfigureAwait(false);

                    AskResponse failures = await ask.AskAsync("what failed today?").ConfigureAwait(false);
                    AssertEqual("3 failed mission(s) and 1 landing-failed mission(s).", failures.Reply);

                    AskResponse work = await ask.AskAsync("how many missions are there?").ConfigureAwait(false);
                    AssertEqual("Missions: 2 pending, 1 in progress, 1 awaiting review.", work.Reply);
                }
            });
        }

        private static async Task CreateAsync(TestDatabase testDb, MissionStatusEnum status, string? diff)
        {
            Mission mission = new Mission("ask count " + status + " " + Guid.NewGuid().ToString("N"));
            mission.Status = status;
            mission.DiffSnapshot = diff;
            await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }
    }
}
