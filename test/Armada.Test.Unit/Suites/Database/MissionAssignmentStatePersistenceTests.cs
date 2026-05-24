namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.Text.Json;
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
            await Mission_Updated_PersistsAssignmentStateChange().ConfigureAwait(false);
            await Mission_UnknownAssignmentStateValue_FallsBackToPending().ConfigureAwait(false);
            await MissionAssignmentStateEnum_HasExpectedValuesInOrder().ConfigureAwait(false);
            await MissionAssignmentStateEnum_SerializesAsStringName().ConfigureAwait(false);
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

        private async Task Mission_Updated_PersistsAssignmentStateChange()
        {
            await RunTest("Mission_Updated_PersistsAssignmentStateChange", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Mission mission = new Mission("Assignment state update mission");
                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? afterCreate = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(afterCreate);
                    AssertEqual(MissionAssignmentStateEnum.Pending, afterCreate!.AssignmentState);

                    afterCreate.AssignmentState = MissionAssignmentStateEnum.WaitingForIdleCaptain;
                    await db.Missions.UpdateAsync(afterCreate).ConfigureAwait(false);

                    Mission? afterUpdate = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(afterUpdate);
                    AssertEqual(MissionAssignmentStateEnum.WaitingForIdleCaptain, afterUpdate!.AssignmentState);

                    afterUpdate.AssignmentState = MissionAssignmentStateEnum.Failed;
                    await db.Missions.UpdateAsync(afterUpdate).ConfigureAwait(false);

                    Mission? afterFailed = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(afterFailed);
                    AssertEqual(MissionAssignmentStateEnum.Failed, afterFailed!.AssignmentState);
                }
            }).ConfigureAwait(false);
        }

        private async Task Mission_UnknownAssignmentStateValue_FallsBackToPending()
        {
            await RunTest("Mission_UnknownAssignmentStateValue_FallsBackToPending", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Mission mission = new Mission("Unknown assignment state mission");
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                    using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "UPDATE missions SET mission_assignment_state = @value WHERE id = @id;";
                            cmd.Parameters.AddWithValue("@value", "SomeFutureStateNotInEnum");
                            cmd.Parameters.AddWithValue("@id", mission.Id);
                            int affected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                            AssertEqual(1, affected, "raw UPDATE should affect exactly one row");
                        }
                    }

                    Mission? read = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);

                    AssertNotNull(read);
                    AssertEqual(
                        MissionAssignmentStateEnum.Pending,
                        read!.AssignmentState,
                        "unknown DB values must fall back to Pending without throwing");
                }
            }).ConfigureAwait(false);
        }

        private async Task MissionAssignmentStateEnum_HasExpectedValuesInOrder()
        {
            await RunTest("MissionAssignmentStateEnum_HasExpectedValuesInOrder", () =>
            {
                MissionAssignmentStateEnum[] expected = new MissionAssignmentStateEnum[]
                {
                    MissionAssignmentStateEnum.Pending,
                    MissionAssignmentStateEnum.WaitingForDependency,
                    MissionAssignmentStateEnum.WaitingForVesselMutex,
                    MissionAssignmentStateEnum.WaitingForIdleCaptain,
                    MissionAssignmentStateEnum.Provisioning,
                    MissionAssignmentStateEnum.Assigned,
                    MissionAssignmentStateEnum.Failed
                };

                MissionAssignmentStateEnum[] actual = Enum.GetValues<MissionAssignmentStateEnum>();

                AssertEqual(expected.Length, actual.Length, "enum value count is pinned by the assignment-pipeline contract");
                for (int i = 0; i < expected.Length; i++)
                {
                    AssertEqual(expected[i], actual[i], "enum order at index " + i + " is pinned by the assignment-pipeline contract");
                }

                AssertEqual(0, (int)MissionAssignmentStateEnum.Pending, "Pending must remain the default (ordinal 0) so DB default 'Pending' aligns");

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        private async Task MissionAssignmentStateEnum_SerializesAsStringName()
        {
            await RunTest("MissionAssignmentStateEnum_SerializesAsStringName", () =>
            {
                foreach (MissionAssignmentStateEnum state in Enum.GetValues<MissionAssignmentStateEnum>())
                {
                    string json = JsonSerializer.Serialize(state);
                    AssertEqual("\"" + state.ToString() + "\"", json, "states must serialize as their member name, not an integer");

                    MissionAssignmentStateEnum roundTripped = JsonSerializer.Deserialize<MissionAssignmentStateEnum>(json);
                    AssertEqual(state, roundTripped, "JSON round-trip must preserve the state");
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        #endregion
    }
}
