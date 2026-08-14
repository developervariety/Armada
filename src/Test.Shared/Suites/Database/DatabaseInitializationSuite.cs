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
    /// Descriptors for database initialization: verifies that a freshly created SQLite store
    /// exposes all entity method accessors and that every table is created empty and queryable.
    /// Both cases are positive; there is no negative surface for a successful initialization.
    /// </summary>
    public sealed class DatabaseInitializationSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Database Initialization suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("initialize_async_creates_all_tables", "InitializeAsync creates all tables", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    AssertNotNull(db.Fleets);
                    AssertNotNull(db.Vessels);
                    AssertNotNull(db.Captains);
                    AssertNotNull(db.Missions);
                    AssertNotNull(db.Voyages);
                    AssertNotNull(db.PlanningSessions);
                    AssertNotNull(db.PlanningSessionMessages);
                    AssertNotNull(db.Docks);
                    AssertNotNull(db.Signals);
                    AssertNotNull(db.Events);
                }
            }));

            cases.Add(CaseAsync("initialize_async_tables_are_queryable", "InitializeAsync tables are queryable", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    List<Fleet> fleets = await db.Fleets.EnumerateAsync();
                    AssertEqual(0, fleets.Count);

                    List<Vessel> vessels = await db.Vessels.EnumerateAsync();
                    AssertEqual(0, vessels.Count);

                    List<Captain> captains = await db.Captains.EnumerateAsync();
                    AssertEqual(0, captains.Count);

                    List<Mission> missions = await db.Missions.EnumerateAsync();
                    AssertEqual(0, missions.Count);

                    List<Voyage> voyages = await db.Voyages.EnumerateAsync();
                    AssertEqual(0, voyages.Count);

                    // Planning sessions are implemented for SQLite-backed deployments only; the server
                    // drivers intentionally throw NotSupportedException for this entity.
                    if (TestDatabaseConfig.IsSqlite)
                    {
                        List<PlanningSession> planningSessions = await db.PlanningSessions.EnumerateAsync();
                        AssertEqual(0, planningSessions.Count);
                    }

                    List<Dock> docks = await db.Docks.EnumerateAsync();
                    AssertEqual(0, docks.Count);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Database.DatabaseInitialization",
                displayName: "Database Initialization",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Database.DatabaseInitialization",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
