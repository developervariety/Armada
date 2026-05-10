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
                            cmd.CommandText = "DELETE FROM schema_migrations WHERE version IN (40, 41);";
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    SqliteDatabaseDriver driver2 = new SqliteDatabaseDriver(connectionString, logging);
                    await driver2.InitializeAsync().ConfigureAwait(false);
                    int version = await driver2.GetSchemaVersionAsync().ConfigureAwait(false);
                    driver2.Dispose();

                    AssertEqual(42, version, "schema version should return to latest after idempotent rerun of reflection columns");

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

            await RunTest("VesselReflectionFields_OnlyMissionIdSet_ThresholdRemainsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("ReflectionMissionIdOnly", "https://github.com/test/reflect-id-only");
                    vessel.LastReflectionMissionId = "msn_reflect_solo";

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? loaded = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(loaded, "vessel should be readable after create");
                    AssertEqual("msn_reflect_solo", loaded!.LastReflectionMissionId, "LastReflectionMissionId should round-trip alone");
                    AssertNull(loaded.ReflectionThreshold, "ReflectionThreshold should remain null when only mission id is set");
                }
            });

            await RunTest("VesselReflectionFields_OnlyThresholdSet_MissionIdRemainsNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("ReflectionThresholdOnly", "https://github.com/test/reflect-threshold-only");
                    vessel.ReflectionThreshold = 11;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? loaded = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(loaded, "vessel should be readable after create");
                    AssertNull(loaded!.LastReflectionMissionId, "LastReflectionMissionId should remain null when only threshold is set");
                    AssertEqual(11, loaded.ReflectionThreshold!.Value, "ReflectionThreshold should round-trip alone");
                }
            });

            await RunTest("VesselReflectionFields_BoundaryThresholds_RoundTrip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel zeroVessel = new Vessel("ReflectionThresholdZero", "https://github.com/test/reflect-zero");
                    zeroVessel.ReflectionThreshold = 0;
                    await testDb.Driver.Vessels.CreateAsync(zeroVessel).ConfigureAwait(false);

                    Vessel maxVessel = new Vessel("ReflectionThresholdMax", "https://github.com/test/reflect-max");
                    maxVessel.ReflectionThreshold = int.MaxValue;
                    await testDb.Driver.Vessels.CreateAsync(maxVessel).ConfigureAwait(false);

                    Vessel? zeroLoaded = await testDb.Driver.Vessels.ReadAsync(zeroVessel.Id).ConfigureAwait(false);
                    Vessel? maxLoaded = await testDb.Driver.Vessels.ReadAsync(maxVessel.Id).ConfigureAwait(false);

                    AssertNotNull(zeroLoaded, "zero-threshold vessel should be readable");
                    AssertEqual(0, zeroLoaded!.ReflectionThreshold!.Value, "ReflectionThreshold zero should round-trip");

                    AssertNotNull(maxLoaded, "max-threshold vessel should be readable");
                    AssertEqual(int.MaxValue, maxLoaded!.ReflectionThreshold!.Value, "ReflectionThreshold int.MaxValue should round-trip");
                }
            });

            await RunTest("VesselReflectionFields_Enumerate_ReturnsBothFields", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vesselA = new Vessel("ReflectionEnumA", "https://github.com/test/reflect-enum-a");
                    vesselA.LastReflectionMissionId = "msn_reflect_a";
                    vesselA.ReflectionThreshold = 3;
                    Vessel vesselB = new Vessel("ReflectionEnumB", "https://github.com/test/reflect-enum-b");
                    vesselB.LastReflectionMissionId = "msn_reflect_b";
                    vesselB.ReflectionThreshold = 9;

                    await testDb.Driver.Vessels.CreateAsync(vesselA).ConfigureAwait(false);
                    await testDb.Driver.Vessels.CreateAsync(vesselB).ConfigureAwait(false);

                    List<Vessel> all = await testDb.Driver.Vessels.EnumerateAsync().ConfigureAwait(false);
                    Vessel? loadedA = all.Find(v => v.Id == vesselA.Id);
                    Vessel? loadedB = all.Find(v => v.Id == vesselB.Id);

                    AssertNotNull(loadedA, "vesselA should appear in EnumerateAsync");
                    AssertEqual("msn_reflect_a", loadedA!.LastReflectionMissionId, "vesselA mission id should hydrate from enumerate");
                    AssertEqual(3, loadedA.ReflectionThreshold!.Value, "vesselA threshold should hydrate from enumerate");

                    AssertNotNull(loadedB, "vesselB should appear in EnumerateAsync");
                    AssertEqual("msn_reflect_b", loadedB!.LastReflectionMissionId, "vesselB mission id should hydrate from enumerate");
                    AssertEqual(9, loadedB.ReflectionThreshold!.Value, "vesselB threshold should hydrate from enumerate");
                }
            });

            await RunTest("MigrationV40_FreshInit_SchemaVersionAtLeastV40_AndColumnsExist", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_v40_fresh_" + Guid.NewGuid().ToString("N") + ".db");
                string connectionString = "Data Source=" + tempFile;

                try
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging);
                    await driver.InitializeAsync().ConfigureAwait(false);
                    int version = await driver.GetSchemaVersionAsync().ConfigureAwait(false);
                    driver.Dispose();

                    AssertTrue(version >= 40, "schema version should be at least 40 after fresh init (was " + version + ")");

                    using (SqliteConnection conn = new SqliteConnection(connectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        AssertTrue(await ColumnExistsAsync(conn, "last_reflection_mission_id").ConfigureAwait(false), "last_reflection_mission_id should exist after fresh init");
                        AssertTrue(await ColumnExistsAsync(conn, "reflection_threshold").ConfigureAwait(false), "reflection_threshold should exist after fresh init");

                        AssertEqual("TEXT", await ColumnTypeAsync(conn, "last_reflection_mission_id").ConfigureAwait(false), "last_reflection_mission_id should be TEXT");
                        AssertEqual("INTEGER", await ColumnTypeAsync(conn, "reflection_threshold").ConfigureAwait(false), "reflection_threshold should be INTEGER");
                    }
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            });
        }

        private static async Task<string?> ColumnTypeAsync(SqliteConnection conn, string columnName)
        {
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT type FROM pragma_table_info('vessels') WHERE name = @name;";
                cmd.Parameters.AddWithValue("@name", columnName);
                object? result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (result == null || result == DBNull.Value) return null;
                return result.ToString();
            }
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
