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
    /// Descriptors for the event database methods not covered by the event suite: create, and a
    /// negative case confirming that filtering by an unknown mission id yields no rows. Each case
    /// runs against its own fresh SQLite store.
    /// </summary>
    public sealed class EventDatabaseSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.EventDatabase";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Event Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_async_returns_event", "CreateAsync returns event", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    ArmadaEvent evt = new ArmadaEvent("mission.created", "Mission created");
                    ArmadaEvent result = await db.Events.CreateAsync(evt);

                    AssertNotNull(result);
                    AssertEqual("mission.created", result.EventType);
                }
            }));

            // Audit addition: filtering by an unknown correlation id must return an empty set.
            cases.Add(CaseAsync("enumerate_by_mission_async_unknown_returns_empty", "EnumerateByMissionAsync unknown returns empty", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    ArmadaEvent evt = new ArmadaEvent("mission.updated", "Updated");
                    evt.MissionId = "msn_present";
                    await db.Events.CreateAsync(evt);

                    List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync("msn_absent");
                    AssertEqual(0, events.Count);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Event Database",
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
