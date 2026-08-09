namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors verifying the SQLite driver tolerates concurrent access: many parallel creates,
    /// many parallel reads of the same row, and interleaved create/enumerate operations all
    /// completing without loss or corruption. These are positive concurrency-tolerance cases; there
    /// is no invalid-input or rejection path to exercise, so the suite carries no negative cases.
    /// </summary>
    public sealed class ConcurrentAccessSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Concurrent Access suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("concurrent_creates_all_succeed", "Concurrent creates all succeed", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    int count = 20;
                    Task<Fleet>[] tasks = new Task<Fleet>[count];

                    for (int i = 0; i < count; i++)
                    {
                        int idx = i;
                        tasks[i] = db.Fleets.CreateAsync(new Fleet("Concurrent Fleet " + idx));
                    }

                    Fleet[] results = await Task.WhenAll(tasks);

                    AssertEqual(count, results.Length);
                    List<Fleet> allFleets = await db.Fleets.EnumerateAsync();
                    AssertEqual(count, allFleets.Count);
                }
            }));

            cases.Add(CaseAsync("concurrent_reads_all_succeed", "Concurrent reads all succeed", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Fleet fleet = new Fleet("Read Target");
                    await db.Fleets.CreateAsync(fleet);

                    int count = 20;
                    Task<Fleet?>[] tasks = new Task<Fleet?>[count];

                    for (int i = 0; i < count; i++)
                    {
                        tasks[i] = db.Fleets.ReadAsync(fleet.Id);
                    }

                    Fleet?[] results = await Task.WhenAll(tasks);

                    foreach (Fleet? result in results)
                    {
                        AssertNotNull(result);
                        AssertEqual("Read Target", result!.Name);
                    }
                }
            }));

            cases.Add(CaseAsync("concurrent_mixed_operations_all_succeed", "Concurrent mixed operations all succeed", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    for (int i = 0; i < 5; i++)
                    {
                        await db.Fleets.CreateAsync(new Fleet("Initial " + i));
                    }

                    List<Task> tasks = new List<Task>();
                    for (int i = 0; i < 10; i++)
                    {
                        int idx = i;
                        tasks.Add(db.Fleets.CreateAsync(new Fleet("Mixed " + idx)));
                        tasks.Add(db.Fleets.EnumerateAsync());
                    }

                    await Task.WhenAll(tasks);

                    List<Fleet> allFleets = await db.Fleets.EnumerateAsync();
                    AssertEqual(15, allFleets.Count);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Database.ConcurrentAccess",
                displayName: "Concurrent Access",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Database.ConcurrentAccess",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
