namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Fleet database CRUD descriptors: create, read, read-by-name, update, existence, enumeration
    /// (all and paginated), delete, and the not-found read and existence paths. Each case runs
    /// against its own fresh SQLite store.
    /// </summary>
    public sealed class FleetDatabaseSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.FleetDatabase";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Fleet Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("fleet_create", "Fleet_Create", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet fleet = new Fleet("TestFleet");
                    Fleet result = await db.Fleets.CreateAsync(fleet);

                    AssertNotNull(result, "Created fleet");
                    AssertNotNull(result.Id, "Fleet ID");
                    AssertStartsWith("flt_", result.Id, "Fleet ID prefix");
                    AssertEqual("TestFleet", result.Name, "Fleet name");
                    AssertTrue(result.CreatedUtc <= DateTime.UtcNow, "CreatedUtc is set");
                    AssertTrue(result.LastUpdateUtc <= DateTime.UtcNow, "LastUpdateUtc is set");
                }
            }));

            cases.Add(CaseAsync("fleet_read", "Fleet_Read", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet fleet = new Fleet("ReadTest");
                    fleet.Description = "A test fleet";
                    fleet.Active = true;
                    Fleet created = await db.Fleets.CreateAsync(fleet);

                    Fleet? result = await db.Fleets.ReadAsync(created.Id);
                    AssertNotNull(result, "Read fleet");
                    AssertEqual(created.Id, result!.Id, "Fleet ID");
                    AssertEqual("ReadTest", result.Name, "Fleet name");
                    AssertEqual("A test fleet", result.Description, "Fleet description");
                    AssertTrue(result.Active, "Fleet active");
                }
            }));

            cases.Add(CaseAsync("fleet_read_by_name", "Fleet_ReadByName", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet fleet = new Fleet("NameLookup");
                    Fleet created = await db.Fleets.CreateAsync(fleet);

                    Fleet? result = await db.Fleets.ReadByNameAsync("NameLookup");
                    AssertNotNull(result, "Read by name");
                    AssertEqual(created.Id, result!.Id, "Fleet ID matches");
                    AssertEqual("NameLookup", result.Name, "Fleet name");
                }
            }));

            cases.Add(CaseAsync("fleet_update", "Fleet_Update", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet fleet = new Fleet("Original");
                    Fleet created = await db.Fleets.CreateAsync(fleet);
                    DateTime originalLastUpdate = created.LastUpdateUtc;

                    await Task.Delay(50).ConfigureAwait(false);

                    created.Name = "Updated";
                    created.Description = "New description";
                    created.Active = false;
                    Fleet updated = await db.Fleets.UpdateAsync(created);

                    Fleet? result = await db.Fleets.ReadAsync(created.Id);
                    AssertNotNull(result, "Updated fleet");
                    AssertEqual("Updated", result!.Name, "Updated name");
                    AssertEqual("New description", result.Description, "Updated description");
                    AssertFalse(result.Active, "Updated active");
                    AssertTrue(result.LastUpdateUtc >= originalLastUpdate, "LastUpdateUtc changed");
                }
            }));

            cases.Add(CaseAsync("fleet_exists", "Fleet_Exists", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet fleet = new Fleet("ExistTest");
                    Fleet created = await db.Fleets.CreateAsync(fleet);

                    bool exists = await db.Fleets.ExistsAsync(created.Id);
                    AssertTrue(exists, "Existing fleet");

                    bool notExists = await db.Fleets.ExistsAsync("flt_nonexistent");
                    AssertFalse(notExists, "Non-existing fleet");
                }
            }));

            cases.Add(CaseAsync("fleet_enumerate", "Fleet_Enumerate", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet fleet1 = await db.Fleets.CreateAsync(new Fleet("Alpha"));
                    Fleet fleet2 = await db.Fleets.CreateAsync(new Fleet("Beta"));
                    Fleet fleet3 = await db.Fleets.CreateAsync(new Fleet("Charlie"));

                    List<Fleet> results = await db.Fleets.EnumerateAsync();
                    AssertEqual(3, results.Count, "Fleet count");

                    List<string> names = new List<string>();
                    foreach (Fleet f in results)
                    {
                        names.Add(f.Name);
                    }

                    AssertTrue(names.Contains("Alpha"), "Contains Alpha");
                    AssertTrue(names.Contains("Beta"), "Contains Beta");
                    AssertTrue(names.Contains("Charlie"), "Contains Charlie");
                }
            }));

            cases.Add(CaseAsync("fleet_enumerate_paginated", "Fleet_EnumeratePaginated", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    await db.Fleets.CreateAsync(new Fleet("Page1"));
                    await db.Fleets.CreateAsync(new Fleet("Page2"));
                    await db.Fleets.CreateAsync(new Fleet("Page3"));

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 1;
                    query.PageNumber = 1;
                    query.Order = EnumerationOrderEnum.CreatedAscending;

                    EnumerationResult<Fleet> page1 = await db.Fleets.EnumerateAsync(query);
                    AssertEqual(1, page1.Objects.Count, "Page 1 count");
                    AssertEqual(3, (int)page1.TotalRecords, "Total records");
                    AssertEqual(3, page1.TotalPages, "Total pages");
                    AssertEqual(1, page1.PageNumber, "Page number");

                    query.PageNumber = 2;
                    EnumerationResult<Fleet> page2 = await db.Fleets.EnumerateAsync(query);
                    AssertEqual(1, page2.Objects.Count, "Page 2 count");
                    AssertNotEqual(page1.Objects[0].Id, page2.Objects[0].Id, "Different fleet on page 2");

                    query.PageNumber = 3;
                    EnumerationResult<Fleet> page3 = await db.Fleets.EnumerateAsync(query);
                    AssertEqual(1, page3.Objects.Count, "Page 3 count");
                    AssertNotEqual(page1.Objects[0].Id, page3.Objects[0].Id, "Different fleet on page 3");
                    AssertNotEqual(page2.Objects[0].Id, page3.Objects[0].Id, "Different fleet on page 3 vs 2");
                }
            }));

            cases.Add(CaseAsync("fleet_delete", "Fleet_Delete", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet fleet = new Fleet("ToDelete");
                    Fleet created = await db.Fleets.CreateAsync(fleet);

                    await db.Fleets.DeleteAsync(created.Id);

                    Fleet? result = await db.Fleets.ReadAsync(created.Id);
                    AssertNull(result, "Deleted fleet");
                }
            }));

            cases.Add(CaseAsync("fleet_read_not_found", "Fleet_ReadNotFound", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet? result = await db.Fleets.ReadAsync("flt_nonexistent_id_12345");
                    AssertNull(result, "Non-existent fleet");
                }
            }));

            cases.Add(CaseAsync("fleet_exists_not_found", "Fleet_ExistsNotFound", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    bool exists = await db.Fleets.ExistsAsync("flt_nonexistent_id_12345");
                    AssertFalse(exists, "Non-existent fleet");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Fleet Database",
                cases: cases);
        }

        #endregion

        #region Private-Methods

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
    }
}
