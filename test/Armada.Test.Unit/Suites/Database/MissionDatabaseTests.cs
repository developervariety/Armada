namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Comprehensive CRUD and enumeration tests for Mission database operations.
    /// </summary>
    public class MissionDatabaseTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "Mission Database";

        #endregion

        #region Private-Members

        private static DateTime _BaseTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run all mission database tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await Mission_CountByStatus_IgnoresPayloadHydration();
            await Mission_EnumerateSummaries_OmitsHeavyColumnsOnEveryScope();
            await Mission_CountByVoyageStatus_CountsOnlyThatVoyage();
        }

        #endregion

        #region Private-Methods

        private async Task<MissionTestPrerequisites> CreatePrerequisitesAsync(SqliteDatabaseDriver db)
        {
            Fleet fleet = new Fleet("Test Fleet");
            fleet.CreatedUtc = _BaseTime;
            await db.Fleets.CreateAsync(fleet).ConfigureAwait(false);

            Vessel vessel = new Vessel("Test Vessel", "https://github.com/test/repo");
            vessel.FleetId = fleet.Id;
            vessel.CreatedUtc = _BaseTime;
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Voyage voyage = new Voyage("Test Voyage", "Test voyage description");
            voyage.CreatedUtc = _BaseTime;
            await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            return new MissionTestPrerequisites(fleet, vessel, voyage);
        }

        private async Task Mission_EnumerateSummaries_OmitsHeavyColumnsOnEveryScope()
        {
            // Mission lists read the summary projection so a page of missions never loads each
            // row's description, diff snapshot and agent output. Every scope overload must return
            // the light fields and leave the heavy ones unset, while a full read still has them.
            await RunTest("Mission_EnumerateSummaries_OmitsHeavyColumnsOnEveryScope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    MissionTestPrerequisites prereqs = await CreatePrerequisitesAsync(db);

                    string heavyText = new string('x', 64 * 1024);
                    Mission heavy = new Mission("Heavy summary mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        VesselId = prereqs.Vessel.Id,
                        VoyageId = prereqs.Voyage.Id,
                        Status = MissionStatusEnum.WorkProduced,
                        Persona = "Worker",
                        BranchName = "armada/summary-branch",
                        Description = heavyText,
                        DiffSnapshot = heavyText,
                        AgentOutput = heavyText
                    };
                    heavy = await db.Missions.CreateAsync(heavy);

                    Mission? full = await db.Missions.ReadAsync(heavy.Id);
                    AssertEqual(heavyText.Length, full!.Description!.Length, "a full read must still return the description");
                    AssertEqual(heavyText.Length, full.DiffSnapshot!.Length, "a full read must still return the diff snapshot");
                    AssertEqual(heavyText.Length, full.AgentOutput!.Length, "a full read must still return the agent output");

                    EnumerationQuery query = new EnumerationQuery { VoyageId = prereqs.Voyage.Id };
                    List<EnumerationResult<Mission>> scopes = new List<EnumerationResult<Mission>>
                    {
                        await db.Missions.EnumerateSummariesAsync(query),
                        await db.Missions.EnumerateSummariesAsync(Armada.Core.Constants.DefaultTenantId, query),
                        await db.Missions.EnumerateSummariesAsync(Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, query)
                    };

                    string[] scopeNames = { "unscoped", "tenant", "tenant and user" };
                    for (int i = 0; i < scopes.Count; i++)
                    {
                        AssertEqual(1, scopes[i].Objects.Count, scopeNames[i] + " summary read should return the mission");
                        Mission summary = scopes[i].Objects[0];
                        AssertEqual(heavy.Id, summary.Id, scopeNames[i] + " summary id");
                        AssertEqual("Heavy summary mission", summary.Title, scopeNames[i] + " summary title");
                        AssertEqual(MissionStatusEnum.WorkProduced, summary.Status, scopeNames[i] + " summary status");
                        AssertEqual("Worker", summary.Persona, scopeNames[i] + " summary persona");
                        AssertEqual("armada/summary-branch", summary.BranchName, scopeNames[i] + " summary branch");
                        AssertNull(summary.Description, scopeNames[i] + " summary must not load the description");
                        AssertNull(summary.DiffSnapshot, scopeNames[i] + " summary must not load the diff snapshot");
                        AssertNull(summary.AgentOutput, scopeNames[i] + " summary must not load the agent output");
                    }
                }
            });
        }

        private async Task Mission_CountByVoyageStatus_CountsOnlyThatVoyage()
        {
            // Voyage progress reads grouped status counts instead of loading every mission row.
            await RunTest("Mission_CountByVoyageStatus_CountsOnlyThatVoyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    MissionTestPrerequisites prereqs = await CreatePrerequisitesAsync(db);
                    Voyage otherVoyage = await db.Voyages.CreateAsync(new Voyage("Other voyage", "not counted"));

                    string heavyText = new string('x', 64 * 1024);
                    MissionStatusEnum[] voyageStatuses =
                    {
                        MissionStatusEnum.Complete,
                        MissionStatusEnum.Complete,
                        MissionStatusEnum.InProgress,
                        MissionStatusEnum.Failed
                    };
                    foreach (MissionStatusEnum status in voyageStatuses)
                    {
                        await db.Missions.CreateAsync(new Mission("Counted " + status)
                        {
                            VesselId = prereqs.Vessel.Id,
                            VoyageId = prereqs.Voyage.Id,
                            Status = status,
                            AgentOutput = heavyText
                        });
                    }

                    await db.Missions.CreateAsync(new Mission("Other voyage mission")
                    {
                        VesselId = prereqs.Vessel.Id,
                        VoyageId = otherVoyage.Id,
                        Status = MissionStatusEnum.Complete
                    });

                    Dictionary<MissionStatusEnum, int> counts = await db.Missions.CountByVoyageStatusAsync(prereqs.Voyage.Id);

                    AssertEqual(3, counts.Count, "only the statuses present on the voyage should appear");
                    AssertEqual(2, counts[MissionStatusEnum.Complete], "complete missions of this voyage only");
                    AssertEqual(1, counts[MissionStatusEnum.InProgress]);
                    AssertEqual(1, counts[MissionStatusEnum.Failed]);

                    Dictionary<MissionStatusEnum, int> empty = await db.Missions.CountByVoyageStatusAsync("vyg_nonexistent");
                    AssertEqual(0, empty.Count, "a voyage with no missions has no counts");
                }
            });
        }

        private async Task Mission_CountByStatus_IgnoresPayloadHydration()
        {
            await RunTest("Mission_CountByStatus_IgnoresPayloadHydration", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    MissionTestPrerequisites prereqs = await CreatePrerequisitesAsync(db);
                    Vessel vessel = prereqs.Vessel;

                    string heavyText = new string('x', 64 * 1024);

                    Mission inProgress = new Mission("InProgress count check")
                    {
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.InProgress,
                        Description = heavyText,
                        DiffSnapshot = heavyText,
                        AgentOutput = heavyText
                    };
                    Mission workProduced = new Mission("WorkProduced count check")
                    {
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.WorkProduced,
                        AgentOutput = heavyText
                    };
                    Mission landingFailed1 = new Mission("LandingFailed 1")
                    {
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.LandingFailed
                    };
                    Mission landingFailed2 = new Mission("LandingFailed 2")
                    {
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.LandingFailed
                    };

                    await db.Missions.CreateAsync(inProgress);
                    await db.Missions.CreateAsync(workProduced);
                    await db.Missions.CreateAsync(landingFailed1);
                    await db.Missions.CreateAsync(landingFailed2);

                    Dictionary<MissionStatusEnum, int> counts = await db.Missions.CountByStatusAsync();

                    AssertEqual(1, counts[MissionStatusEnum.InProgress]);
                    AssertEqual(1, counts[MissionStatusEnum.WorkProduced]);
                    AssertEqual(2, counts[MissionStatusEnum.LandingFailed]);
                    AssertFalse(counts.ContainsKey(MissionStatusEnum.Pending), "Pending should not appear when there are no pending rows");
                }
            });
        }

        #endregion
    }
}
