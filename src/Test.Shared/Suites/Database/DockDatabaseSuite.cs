namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// CRUD and query descriptors for the dock database methods: create, read, update, existence,
    /// enumeration (all, paginated, by vessel), available-dock discovery (found and none), delete,
    /// and not-found paths. Each case builds a fresh SQLite store seeded with a fleet and vessel.
    /// </summary>
    public sealed class DockDatabaseSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.DockDatabase";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Dock Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("dock_create", "Dock_Create", TestTags.Positive, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;
                    Dock dock = new Dock(setup.Vessel.Id);
                    dock.WorktreePath = "/tmp/worktree";
                    dock.BranchName = "armada/test";

                    Dock result = await db.Docks.CreateAsync(dock);

                    AssertNotNull(result);
                    AssertEqual(dock.Id, result.Id);
                    AssertEqual(setup.Vessel.Id, result.VesselId);
                    AssertEqual("/tmp/worktree", result.WorktreePath);
                    AssertEqual("armada/test", result.BranchName);
                    AssertTrue(result.Active);
                }
            }));

            cases.Add(CaseAsync("dock_read", "Dock_Read", TestTags.Positive, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;
                    Dock dock = new Dock(setup.Vessel.Id);
                    dock.WorktreePath = "/tmp/worktree";
                    dock.BranchName = "armada/test";
                    await db.Docks.CreateAsync(dock);

                    Dock? result = await db.Docks.ReadAsync(dock.Id);

                    AssertNotNull(result);
                    AssertEqual(dock.Id, result!.Id);
                    AssertEqual(setup.Vessel.Id, result.VesselId);
                    AssertEqual("/tmp/worktree", result.WorktreePath);
                    AssertEqual("armada/test", result.BranchName);
                    AssertTrue(result.Active);
                }
            }));

            cases.Add(CaseAsync("dock_update", "Dock_Update", TestTags.Positive, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;
                    Dock dock = new Dock(setup.Vessel.Id);
                    dock.WorktreePath = "/tmp/worktree";
                    dock.BranchName = "armada/test";
                    await db.Docks.CreateAsync(dock);

                    dock.WorktreePath = "/tmp/worktree-updated";
                    dock.BranchName = "armada/updated";
                    dock.Active = false;
                    Dock updated = await db.Docks.UpdateAsync(dock);

                    AssertNotNull(updated);
                    AssertEqual("/tmp/worktree-updated", updated.WorktreePath);
                    AssertEqual("armada/updated", updated.BranchName);
                    AssertFalse(updated.Active);

                    Dock? readBack = await db.Docks.ReadAsync(dock.Id);
                    AssertNotNull(readBack);
                    AssertEqual("/tmp/worktree-updated", readBack!.WorktreePath);
                    AssertEqual("armada/updated", readBack.BranchName);
                    AssertFalse(readBack.Active);
                }
            }));

            cases.Add(CaseAsync("dock_exists", "Dock_Exists", TestTags.Positive, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;
                    Dock dock = new Dock(setup.Vessel.Id);
                    await db.Docks.CreateAsync(dock);

                    bool exists = await db.Docks.ExistsAsync(dock.Id);
                    AssertTrue(exists);
                }
            }));

            cases.Add(CaseAsync("dock_enumerate", "Dock_Enumerate", TestTags.Positive, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;
                    Dock d1 = new Dock(setup.Vessel.Id);
                    Dock d2 = new Dock(setup.Vessel.Id);
                    Dock d3 = new Dock(setup.Vessel.Id);
                    await db.Docks.CreateAsync(d1);
                    await db.Docks.CreateAsync(d2);
                    await db.Docks.CreateAsync(d3);

                    List<Dock> docks = await db.Docks.EnumerateAsync();

                    AssertNotNull(docks);
                    AssertEqual(3, docks.Count);
                }
            }));

            cases.Add(CaseAsync("dock_enumerate_paginated", "Dock_EnumeratePaginated", TestTags.Positive, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;
                    for (int i = 0; i < 5; i++)
                    {
                        Dock dock = new Dock(setup.Vessel.Id);
                        await db.Docks.CreateAsync(dock);
                    }

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 1;

                    EnumerationResult<Dock> result = await db.Docks.EnumerateAsync(query);

                    AssertNotNull(result);
                    AssertTrue(result.Success);
                    AssertEqual(2, result.Objects.Count);
                    AssertEqual(5, (int)result.TotalRecords);
                    AssertEqual(3, result.TotalPages);
                    AssertEqual(1, result.PageNumber);
                    AssertEqual(2, result.PageSize);

                    EnumerationQuery queryPage2 = new EnumerationQuery();
                    queryPage2.PageSize = 2;
                    queryPage2.PageNumber = 2;

                    EnumerationResult<Dock> page2 = await db.Docks.EnumerateAsync(queryPage2);

                    AssertEqual(2, page2.Objects.Count);
                    AssertEqual(2, page2.PageNumber);
                }
            }));

            cases.Add(CaseAsync("dock_enumerate_by_vessel", "Dock_EnumerateByVessel", TestTags.Positive, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;

                    Vessel otherVessel = new Vessel("OtherVessel", "https://github.com/test/other");
                    otherVessel.FleetId = setup.Vessel.FleetId;
                    await db.Vessels.CreateAsync(otherVessel);

                    Dock d1 = new Dock(setup.Vessel.Id);
                    Dock d2 = new Dock(setup.Vessel.Id);
                    Dock d3 = new Dock(otherVessel.Id);
                    await db.Docks.CreateAsync(d1);
                    await db.Docks.CreateAsync(d2);
                    await db.Docks.CreateAsync(d3);

                    List<Dock> docks = await db.Docks.EnumerateByVesselAsync(setup.Vessel.Id);

                    AssertEqual(2, docks.Count);
                    foreach (Dock d in docks)
                    {
                        AssertEqual(setup.Vessel.Id, d.VesselId);
                    }
                }
            }));

            cases.Add(CaseAsync("dock_find_available_found", "Dock_FindAvailable_Found", TestTags.Positive, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;

                    Dock available = new Dock(setup.Vessel.Id);
                    available.Active = true;
                    await db.Docks.CreateAsync(available);

                    Captain captain = new Captain("test-captain");
                    await db.Captains.CreateAsync(captain);
                    Dock assigned = new Dock(setup.Vessel.Id);
                    assigned.CaptainId = captain.Id;
                    await db.Docks.CreateAsync(assigned);

                    Dock? found = await db.Docks.FindAvailableAsync(setup.Vessel.Id);

                    AssertNotNull(found);
                    AssertEqual(available.Id, found!.Id);
                    AssertNull(found.CaptainId);
                    AssertTrue(found.Active);
                }
            }));

            cases.Add(CaseAsync("dock_find_available_none", "Dock_FindAvailable_None", TestTags.Negative, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;

                    Captain captain = new Captain("test-captain");
                    await db.Captains.CreateAsync(captain);

                    Dock assigned1 = new Dock(setup.Vessel.Id);
                    assigned1.CaptainId = captain.Id;
                    await db.Docks.CreateAsync(assigned1);

                    Dock assigned2 = new Dock(setup.Vessel.Id);
                    assigned2.CaptainId = captain.Id;
                    await db.Docks.CreateAsync(assigned2);

                    Dock? found = await db.Docks.FindAvailableAsync(setup.Vessel.Id);

                    AssertNull(found);
                }
            }));

            cases.Add(CaseAsync("dock_delete", "Dock_Delete", TestTags.Positive, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;
                    Dock dock = new Dock(setup.Vessel.Id);
                    await db.Docks.CreateAsync(dock);

                    await db.Docks.DeleteAsync(dock.Id);

                    Dock? result = await db.Docks.ReadAsync(dock.Id);
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("dock_read_not_found", "Dock_ReadNotFound", TestTags.Negative, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;

                    Dock? result = await db.Docks.ReadAsync("dck_nonexistent");

                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("dock_exists_not_found", "Dock_ExistsNotFound", TestTags.Negative, async () =>
            {
                TestDatabaseSetup setup = await SetupWithVesselAsync();
                using (setup.Database)
                {
                    DatabaseDriver db = setup.Database.Driver;

                    bool exists = await db.Docks.ExistsAsync("dck_nonexistent");

                    AssertFalse(exists);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Dock Database",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<TestDatabaseSetup> SetupWithVesselAsync()
        {
            TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync();
            DatabaseDriver db = testDb.Driver;

            Fleet fleet = new Fleet("TestFleet");
            await db.Fleets.CreateAsync(fleet);

            Vessel vessel = new Vessel("TestVessel", "https://github.com/test/repo");
            vessel.FleetId = fleet.Id;
            await db.Vessels.CreateAsync(vessel);

            return new TestDatabaseSetup(testDb, vessel);
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion

        #region Private-Classes

        /// <summary>
        /// Helper to hold test database and vessel references.
        /// </summary>
        private sealed class TestDatabaseSetup
        {
            /// <summary>
            /// The test database instance.
            /// </summary>
            public TestDatabase Database { get; }

            /// <summary>
            /// The vessel created for test setup.
            /// </summary>
            public Vessel Vessel { get; }

            /// <summary>
            /// Instantiate.
            /// </summary>
            /// <param name="database">Test database.</param>
            /// <param name="vessel">Vessel.</param>
            public TestDatabaseSetup(TestDatabase database, Vessel vessel)
            {
                Database = database ?? throw new ArgumentNullException(nameof(database));
                Vessel = vessel ?? throw new ArgumentNullException(nameof(vessel));
            }
        }

        #endregion
    }
}
