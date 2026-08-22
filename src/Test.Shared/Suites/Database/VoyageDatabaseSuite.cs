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
    /// Descriptors for the voyage database methods: create, read, update, delete, status-filtered
    /// enumeration, and existence. Includes a negative case for reading a missing voyage. Each case
    /// runs against its own fresh SQLite store.
    /// </summary>
    public sealed class VoyageDatabaseSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.VoyageDatabase";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Voyage Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_async_returns_voyage", "CreateAsync returns voyage", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Voyage voyage = new Voyage("Test Voyage", "Description");
                    Voyage result = await db.Voyages.CreateAsync(voyage);

                    AssertNotNull(result);
                    AssertEqual("Test Voyage", result.Title);
                }
            }));

            cases.Add(CaseAsync("read_async_returns_created_voyage", "ReadAsync returns created voyage", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Voyage voyage = new Voyage("Read Test");
                    await db.Voyages.CreateAsync(voyage);

                    Voyage? result = await db.Voyages.ReadAsync(voyage.Id);
                    AssertNotNull(result);
                    AssertEqual(voyage.Id, result!.Id);
                    AssertEqual(VoyageStatusEnum.Open, result.Status);
                }
            }));

            cases.Add(CaseAsync("update_async_modifies_voyage", "UpdateAsync modifies voyage", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Voyage voyage = new Voyage("Original");
                    await db.Voyages.CreateAsync(voyage);

                    voyage.Title = "Updated";
                    voyage.Status = VoyageStatusEnum.Complete;
                    voyage.CompletedUtc = DateTime.UtcNow;
                    await db.Voyages.UpdateAsync(voyage);

                    Voyage? result = await db.Voyages.ReadAsync(voyage.Id);
                    AssertEqual("Updated", result!.Title);
                    AssertEqual(VoyageStatusEnum.Complete, result.Status);
                    AssertNotNull(result.CompletedUtc);
                }
            }));

            cases.Add(CaseAsync("delete_async_removes_voyage", "DeleteAsync removes voyage", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Voyage voyage = new Voyage("ToDelete");
                    await db.Voyages.CreateAsync(voyage);

                    await db.Voyages.DeleteAsync(voyage.Id);
                    AssertNull(await db.Voyages.ReadAsync(voyage.Id));
                }
            }));

            cases.Add(CaseAsync("enumerate_by_status_async_filters_correctly", "EnumerateByStatusAsync filters correctly", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Voyage v1 = new Voyage("Open 1");
                    Voyage v2 = new Voyage("Complete");
                    v2.Status = VoyageStatusEnum.Complete;
                    Voyage v3 = new Voyage("Open 2");

                    await db.Voyages.CreateAsync(v1);
                    await db.Voyages.CreateAsync(v2);
                    await db.Voyages.CreateAsync(v3);

                    List<Voyage> open = await db.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.Open);
                    AssertEqual(2, open.Count);

                    List<Voyage> complete = await db.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.Complete);
                    AssertEqual(1, complete.Count);
                }
            }));

            cases.Add(CaseAsync("exists_async_works_correctly", "ExistsAsync works correctly", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Voyage voyage = new Voyage("Exists Test");
                    await db.Voyages.CreateAsync(voyage);

                    AssertTrue(await db.Voyages.ExistsAsync(voyage.Id));
                    AssertFalse(await db.Voyages.ExistsAsync("vyg_nonexistent"));
                }
            }));

            // Audit addition: read of a missing voyage id must return null (not-found path).
            cases.Add(CaseAsync("read_async_missing_returns_null", "ReadAsync missing returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Voyage? result = await db.Voyages.ReadAsync("vyg_nonexistent");
                    AssertNull(result);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Voyage Database",
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
