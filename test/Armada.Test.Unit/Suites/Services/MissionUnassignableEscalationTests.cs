namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// A mission no captain can ever serve (no captain of its tenant accepts its persona and tier) must say so:
    /// one named event when that is first seen, and an incident once it has stayed so for a bounded number of
    /// assignment passes. A mission that only waits for a busy captain is capacity, not construction.
    /// </summary>
    public class MissionUnassignableEscalationTests : TestSuite
    {
        private const string TenantId = "ten_unassignable";
        private const string UserId = "usr_unassignable";
        private const string EventType = "mission.unassignable_by_construction";

        /// <inheritdoc />
        public override string Name => "Mission Unassignable Escalation";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A mission no captain can serve emits one event and opens one incident at the threshold", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Scene scene = await CreateSceneAsync(testDb, "[\"Worker\"]", CaptainStateEnum.Idle).ConfigureAwait(false);
                scene.Missions.UnassignableIncidentTickThreshold = 3;

                await scene.Missions.TryAssignAsync(scene.Mission, scene.Vessel).ConfigureAwait(false);
                await scene.Missions.TryAssignAsync(scene.Mission, scene.Vessel).ConfigureAwait(false);
                AssertEqual(1, await CountEventsAsync(testDb, scene.Mission.Id).ConfigureAwait(false), "the condition is named once when first seen, not every tick");
                AssertEqual(0, await CountOpenIncidentsAsync(testDb, scene.Mission.Id).ConfigureAwait(false), "no incident before the threshold");

                await scene.Missions.TryAssignAsync(scene.Mission, scene.Vessel).ConfigureAwait(false);
                await scene.Missions.TryAssignAsync(scene.Mission, scene.Vessel).ConfigureAwait(false);
                AssertEqual(1, await CountEventsAsync(testDb, scene.Mission.Id).ConfigureAwait(false), "still one event");
                AssertEqual(1, await CountOpenIncidentsAsync(testDb, scene.Mission.Id).ConfigureAwait(false), "exactly one incident once the threshold is reached");

                Mission? after = await testDb.Driver.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Pending, after!.Status, "escalation reports the state; it does not change the mission");
            }).ConfigureAwait(false);

            await RunTest("A mission waiting for a busy captain that could serve it is not escalated", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Scene scene = await CreateSceneAsync(testDb, "[\"Judge\"]", CaptainStateEnum.Working).ConfigureAwait(false);
                scene.Missions.UnassignableIncidentTickThreshold = 1;

                await scene.Missions.TryAssignAsync(scene.Mission, scene.Vessel).ConfigureAwait(false);
                await scene.Missions.TryAssignAsync(scene.Mission, scene.Vessel).ConfigureAwait(false);

                AssertEqual(0, await CountEventsAsync(testDb, scene.Mission.Id).ConfigureAwait(false), "capacity waiting is not unassignable by construction");
                AssertEqual(0, await CountOpenIncidentsAsync(testDb, scene.Mission.Id).ConfigureAwait(false), "capacity waiting opens no incident");
            }).ConfigureAwait(false);
        }

        private sealed class Scene
        {
            public MissionService Missions { get; set; } = null!;

            public Vessel Vessel { get; set; } = null!;

            public Mission Mission { get; set; } = null!;
        }

        private static async Task<Scene> CreateSceneAsync(TestDatabase testDb, string allowedPersonas, CaptainStateEnum captainState)
        {
            if (await testDb.Driver.Tenants.ReadAsync(TenantId).ConfigureAwait(false) == null)
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = TenantId, Name = TenantId }).ConfigureAwait(false);
            if (await testDb.Driver.Users.ReadByIdAsync(UserId).ConfigureAwait(false) == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = UserId,
                    TenantId = TenantId,
                    Email = UserId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }

            Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel
            {
                TenantId = TenantId,
                UserId = UserId,
                Name = "unassignable-vessel",
                RepoUrl = "file:///tmp/unassignable.git",
                LocalPath = Path.Combine(Path.GetTempPath(), "unassignable-repos.git"),
                WorkingDirectory = Path.Combine(Path.GetTempPath(), "unassignable-work"),
                DefaultBranch = "main"
            }).ConfigureAwait(false);

            Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Unassignable voyage")
            {
                TenantId = TenantId,
                UserId = UserId,
                Status = VoyageStatusEnum.Open
            }).ConfigureAwait(false);

            Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission
            {
                TenantId = TenantId,
                UserId = UserId,
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Title = "Judge mission",
                Status = MissionStatusEnum.Pending,
                Persona = "Judge"
            }).ConfigureAwait(false);

            await testDb.Driver.Captains.CreateAsync(new Captain("persona-locked-captain")
            {
                TenantId = TenantId,
                State = captainState,
                AllowedPersonas = allowedPersonas
            }).ConfigureAwait(false);

            string unique = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings
            {
                DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_unassignable_docks_" + unique),
                ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_unassignable_repos_" + unique)
            };
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            StubGitService git = new StubGitService();
            DockService docks = new DockService(logging, testDb.Driver, settings, git);
            CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
            MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains, null, git,
                resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

            return new Scene { Missions = missions, Vessel = vessel, Mission = mission };
        }

        private static async Task<int> CountEventsAsync(TestDatabase testDb, string missionId)
        {
            EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(
                new EnumerationQuery { PageNumber = 1, PageSize = 500 }).ConfigureAwait(false);
            return events.Objects.Count(e => e.EventType == EventType && e.MissionId == missionId);
        }

        private static async Task<int> CountOpenIncidentsAsync(TestDatabase testDb, string missionId)
        {
            EnumerationResult<Incident> incidents = await new IncidentService(testDb.Driver).EnumerateAsync(
                McpTestCaller.Operator,
                new IncidentQuery { MissionId = missionId, PageNumber = 1, PageSize = 25 }).ConfigureAwait(false);
            return incidents.Objects.Count(i => i.Status != IncidentStatusEnum.Closed);
        }
    }
}
