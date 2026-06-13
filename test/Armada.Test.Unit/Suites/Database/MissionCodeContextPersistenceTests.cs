namespace Armada.Test.Unit.Suites.Database
{
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Persistence tests for the code-context intent columns on Mission (schema v53).
    /// </summary>
    public class MissionCodeContextPersistenceTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "Mission Code Context Persistence";

        #endregion

        #region Protected-Methods

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await Mission_WithAllCodeContextFields_RoundTripsCorrectly().ConfigureAwait(false);
            await Mission_WithNullCodeContextFields_RoundTripsAsNull().ConfigureAwait(false);
            await Mission_Update_PersistsCodeContextModeChange().ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private async Task Mission_WithAllCodeContextFields_RoundTripsCorrectly()
        {
            await RunTest("Mission_WithAllCodeContextFields_RoundTripsCorrectly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Mission mission = new Mission("Code context round-trip mission");
                    mission.CodeContextMode = "force";
                    mission.CodeContextQuery = "authentication middleware";
                    mission.CodeContextTokenBudget = 32000;
                    mission.CodeContextMaxResults = 50;

                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                    Mission? read = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);

                    AssertNotNull(read);
                    AssertEqual("force", read!.CodeContextMode, "CodeContextMode must round-trip");
                    AssertEqual("authentication middleware", read.CodeContextQuery, "CodeContextQuery must round-trip");
                    AssertEqual(32000, read.CodeContextTokenBudget, "CodeContextTokenBudget must round-trip");
                    AssertEqual(50, read.CodeContextMaxResults, "CodeContextMaxResults must round-trip");
                }
            }).ConfigureAwait(false);
        }

        private async Task Mission_WithNullCodeContextFields_RoundTripsAsNull()
        {
            await RunTest("Mission_WithNullCodeContextFields_RoundTripsAsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Mission mission = new Mission("Null code context mission");

                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                    Mission? read = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);

                    AssertNotNull(read);
                    AssertNull(read!.CodeContextMode, "CodeContextMode must be null when not set");
                    AssertNull(read.CodeContextQuery, "CodeContextQuery must be null when not set");
                    AssertNull(read.CodeContextTokenBudget, "CodeContextTokenBudget must be null when not set");
                    AssertNull(read.CodeContextMaxResults, "CodeContextMaxResults must be null when not set");
                }
            }).ConfigureAwait(false);
        }

        private async Task Mission_Update_PersistsCodeContextModeChange()
        {
            await RunTest("Mission_Update_PersistsCodeContextModeChange", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Mission mission = new Mission("Code context update mission");
                    mission.CodeContextMode = "auto";
                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? afterCreate = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(afterCreate);
                    AssertEqual("auto", afterCreate!.CodeContextMode, "CodeContextMode must be auto after create");

                    afterCreate.CodeContextMode = "off";
                    afterCreate.CodeContextQuery = "updated query";
                    afterCreate.CodeContextTokenBudget = 8000;
                    afterCreate.CodeContextMaxResults = 10;
                    await db.Missions.UpdateAsync(afterCreate).ConfigureAwait(false);

                    Mission? afterUpdate = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(afterUpdate);
                    AssertEqual("off", afterUpdate!.CodeContextMode, "CodeContextMode must be off after update");
                    AssertEqual("updated query", afterUpdate.CodeContextQuery, "CodeContextQuery must persist after update");
                    AssertEqual(8000, afterUpdate.CodeContextTokenBudget, "CodeContextTokenBudget must persist after update");
                    AssertEqual(10, afterUpdate.CodeContextMaxResults, "CodeContextMaxResults must persist after update");
                }
            }).ConfigureAwait(false);
        }

        #endregion
    }
}
