namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    /// <summary>
    /// Round-trip and migration tests for reflection vessel fields.
    /// </summary>
    public class SchemaMigrationV40ReflectionTests : TestSuite
    {
        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "Schema Migration V40 Reflection";

        /// <summary>
        /// Run reflection schema tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("VesselReflectionFields_CreateReadUpdate_RoundTrip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("ReflectionRoundTrip", "https://github.com/test/reflection");
                    vessel.LastReflectionMissionId = "msn_reflect_12345678";
                    vessel.ReflectionThreshold = 23;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? created = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(created, "vessel should be readable after create");
                    AssertEqual("msn_reflect_12345678", created!.LastReflectionMissionId, "LastReflectionMissionId should round-trip on create");
                    AssertEqual(23, created.ReflectionThreshold!.Value, "ReflectionThreshold should round-trip on create");

                    created.LastReflectionMissionId = "msn_reflect_87654321";
                    created.ReflectionThreshold = 7;
                    await testDb.Driver.Vessels.UpdateAsync(created).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "vessel should be readable after update");
                    AssertEqual("msn_reflect_87654321", updated!.LastReflectionMissionId, "LastReflectionMissionId should round-trip on update");
                    AssertEqual(7, updated.ReflectionThreshold!.Value, "ReflectionThreshold should round-trip on update");
                }
            });

            await RunTest("VesselReflectionFields_NullDefaults_PersistAsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("ReflectionNullDefaults", "https://github.com/test/reflection-null");

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? created = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(created, "vessel should be readable after create");
                    AssertNull(created!.LastReflectionMissionId, "LastReflectionMissionId should default to null");
                    AssertNull(created.ReflectionThreshold, "ReflectionThreshold should default to null");

                    created.LastReflectionMissionId = "msn_reflect_clear";
                    created.ReflectionThreshold = 5;
                    await testDb.Driver.Vessels.UpdateAsync(created).ConfigureAwait(false);

                    created.LastReflectionMissionId = null;
                    created.ReflectionThreshold = null;
                    await testDb.Driver.Vessels.UpdateAsync(created).ConfigureAwait(false);

                    Vessel? cleared = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(cleared, "vessel should be readable after clearing fields");
                    AssertNull(cleared!.LastReflectionMissionId, "LastReflectionMissionId should clear to null");
                    AssertNull(cleared.ReflectionThreshold, "ReflectionThreshold should clear to null");
                }
            });

            await RunTest("MigrationV40_RerunAfterColumnsExist_DoesNotFail", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_v40_reflection_" + Guid.NewGuid().ToString("N") + ".db");
                string connectionString = "Data Source=" + tempFile;

                try
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    SqliteDatabaseDriver driver1 = new SqliteDatabaseDriver(connectionString, logging);
                    await driver1.InitializeAsync().ConfigureAwait(false);
                    driver1.Dispose();

                    using (SqliteConnection conn = new SqliteConnection(connectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "DELETE FROM schema_migrations WHERE version = 40;";
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    SqliteDatabaseDriver driver2 = new SqliteDatabaseDriver(connectionString, logging);
                    await driver2.InitializeAsync().ConfigureAwait(false);
                    int version = await driver2.GetSchemaVersionAsync().ConfigureAwait(false);
                    driver2.Dispose();

                    AssertEqual(40, version, "schema version should return to v40 after idempotent rerun");

                    using (SqliteConnection conn = new SqliteConnection(connectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        AssertTrue(await ColumnExistsAsync(conn, "last_reflection_mission_id").ConfigureAwait(false), "last_reflection_mission_id should exist");
                        AssertTrue(await ColumnExistsAsync(conn, "reflection_threshold").ConfigureAwait(false), "reflection_threshold should exist");
                    }
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            });
        }

        private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string columnName)
        {
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('vessels') WHERE name = @name;";
                cmd.Parameters.AddWithValue("@name", columnName);
                object? result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return result != null && result != DBNull.Value && Convert.ToInt64(result) == 1;
            }
        }
    }
}
