namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;

    /// <summary>
    /// Persistence tests for mission assignment state.
    /// </summary>
    public class MissionAssignmentStatePersistenceTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "Mission Assignment State Persistence";

        #endregion

        #region Protected-Methods

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await Mission_NewlyCreated_DefaultsToPendingAssignmentState().ConfigureAwait(false);
            await Mission_RoundTrip_PreservesAssignmentState().ConfigureAwait(false);
            await Mission_LegacyRow_ReadsAsPendingDefault().ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private async Task Mission_NewlyCreated_DefaultsToPendingAssignmentState()
        {
            await RunTest("Mission_NewlyCreated_DefaultsToPendingAssignmentState", async () =>
            {
                Mission mission = new Mission("Assignment default mission");

                AssertEqual(MissionAssignmentStateEnum.Pending, mission.AssignmentState);

                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                    Mission? read = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);

                    AssertNotNull(read);
                    AssertEqual(MissionAssignmentStateEnum.Pending, read!.AssignmentState);
                }
            }).ConfigureAwait(false);
        }

        private async Task Mission_RoundTrip_PreservesAssignmentState()
        {
            await RunTest("Mission_RoundTrip_PreservesAssignmentState", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    foreach (MissionAssignmentStateEnum state in Enum.GetValues<MissionAssignmentStateEnum>())
                    {
                        Mission mission = new Mission("Assignment state " + state);
                        mission.AssignmentState = state;

                        await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                        Mission? read = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);

                        AssertNotNull(read);
                        AssertEqual(state, read!.AssignmentState, "Assignment state should round trip");
                    }
                }
            }).ConfigureAwait(false);
        }

        private async Task Mission_LegacyRow_ReadsAsPendingDefault()
        {
            await RunTest("Mission_LegacyRow_ReadsAsPendingDefault", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"INSERT INTO missions (id, title, status, created_utc, last_update_utc)
                                VALUES (@id, @title, @status, @created_utc, @last_update_utc);";
                            cmd.Parameters.AddWithValue("@id", "msn_assignment_default");
                            cmd.Parameters.AddWithValue("@title", "Legacy assignment state mission");
                            cmd.Parameters.AddWithValue("@status", MissionStatusEnum.Pending.ToString());
                            cmd.Parameters.AddWithValue("@created_utc", "2025-01-01T00:00:00.0000000Z");
                            cmd.Parameters.AddWithValue("@last_update_utc", "2025-01-01T00:00:00.0000000Z");
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    Mission? read = await testDb.Driver.Missions.ReadAsync("msn_assignment_default").ConfigureAwait(false);

                    AssertNotNull(read);
                    AssertEqual(MissionAssignmentStateEnum.Pending, read!.AssignmentState);
                }
            }).ConfigureAwait(false);
        }

        #endregion
    }
}
