namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core.Database;
    using Armada.Core.Database.Mysql;
    using MysqlTableQueries = Armada.Core.Database.Mysql.Queries.TableQueries;
    using PostgresqlTableQueries = Armada.Core.Database.Postgresql.Queries.TableQueries;
    using Armada.Core.Database.Sqlite;
    using SqliteTableQueries = Armada.Core.Database.Sqlite.Queries.TableQueries;
    using SqlServerTableQueries = Armada.Core.Database.SqlServer.Queries.TableQueries;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    /// <summary>
    /// Round-trip and migration tests for architect_max_missions_per_voyage vessel persistence (schema v54).
    /// </summary>
    public class VesselArchitectCapColumnTests : TestSuite
    {
        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "Vessel Architect Cap Column";

        /// <summary>
        /// Run architect cap column schema and round-trip tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MigrationProviderDefinitions_AllContainVersion54", () =>
            {
                List<SchemaMigration> sqlite = SqliteTableQueries.GetMigrations();
                SchemaMigration? sqlite54 = sqlite.Find(m => m.Version == 54);
                AssertNotNull(sqlite54, "SQLite migrations must include v54");
                AssertContains("architect_max_missions_per_voyage", sqlite54!.Statements[0]);

                List<SchemaMigration> pg = PostgresqlTableQueries.GetMigrations();
                SchemaMigration? pg54 = pg.Find(m => m.Version == 54);
                AssertNotNull(pg54, "PostgreSQL migrations must include v54");
                AssertContains("architect_max_missions_per_voyage", pg54!.Statements[0]);

                List<SchemaMigration> ss = SqlServerTableQueries.GetMigrations();
                SchemaMigration? ss54 = ss.Find(m => m.Version == 54);
                AssertNotNull(ss54, "SQL Server migrations must include v54");
                AssertContains("architect_max_missions_per_voyage", ss54!.Statements[0]);

                AssertEqual(1, MysqlTableQueries.MigrationV54Statements.Length);
                AssertContains("architect_max_missions_per_voyage", MysqlTableQueries.MigrationV54Statements[0]);

                System.Reflection.MethodInfo? mysqlGetMigrations = typeof(MysqlDatabaseDriver).GetMethod("GetMigrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                AssertNotNull(mysqlGetMigrations, "MySQL driver must expose its migration registration list internally");
                object? mysqlMigrationsObject = mysqlGetMigrations!.Invoke(null, Array.Empty<object>());
                AssertNotNull(mysqlMigrationsObject, "MySQL driver migration list should not be null");
                List<SchemaMigration> mysql = (List<SchemaMigration>)mysqlMigrationsObject!;
                SchemaMigration? mysql54 = mysql.Find(m => m.Version == 54);
                AssertNotNull(mysql54, "MySQL driver migrations must register v54");
                AssertContains("architect_max_missions_per_voyage", mysql54!.Statements[0]);
            });

            await RunTest("VesselArchitectMaxMissionsPerVoyage_CreateReadUpdate_RoundTrip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("ArchitectCapRoundTrip", "https://github.com/test/architect-cap");
                    vessel.ArchitectMaxMissionsPerVoyage = 5;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? created = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(created, "vessel should be readable after create");
                    AssertEqual(5, created!.ArchitectMaxMissionsPerVoyage!.Value, "ArchitectMaxMissionsPerVoyage should round-trip on create");

                    created.ArchitectMaxMissionsPerVoyage = 12;
                    await testDb.Driver.Vessels.UpdateAsync(created).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "vessel should be readable after update");
                    AssertEqual(12, updated!.ArchitectMaxMissionsPerVoyage!.Value, "ArchitectMaxMissionsPerVoyage should round-trip on update");
                }
            });

            await RunTest("VesselArchitectMaxMissionsPerVoyage_NullDefaultsAndClear_RoundTrip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("ArchitectCapNullDefaults", "https://github.com/test/architect-cap-null");

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? created = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(created, "vessel should be readable after create");
                    AssertNull(created!.ArchitectMaxMissionsPerVoyage, "ArchitectMaxMissionsPerVoyage should default to null");

                    created.ArchitectMaxMissionsPerVoyage = 3;
                    await testDb.Driver.Vessels.UpdateAsync(created).ConfigureAwait(false);

                    created.ArchitectMaxMissionsPerVoyage = null;
                    await testDb.Driver.Vessels.UpdateAsync(created).ConfigureAwait(false);

                    Vessel? cleared = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(cleared, "vessel should be readable after clearing architect cap");
                    AssertNull(cleared!.ArchitectMaxMissionsPerVoyage, "ArchitectMaxMissionsPerVoyage should clear to null");
                }
            });

            await RunTest("MigrationV54_FreshInit_SchemaVersionAtLeastV54_AndColumnExists", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_v54_fresh_" + Guid.NewGuid().ToString("N") + ".db");
                string connectionString = "Data Source=" + tempFile;

                try
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging);
                    await driver.InitializeAsync().ConfigureAwait(false);
                    int version = await driver.GetSchemaVersionAsync().ConfigureAwait(false);
                    driver.Dispose();

                    AssertTrue(version >= 54, "schema version should be at least 54 after fresh init (was " + version + ")");

                    using (SqliteConnection conn = new SqliteConnection(connectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        AssertTrue(await ColumnExistsAsync(conn, "architect_max_missions_per_voyage").ConfigureAwait(false), "architect_max_missions_per_voyage should exist after fresh init");
                        AssertEqual("INTEGER", await ColumnTypeAsync(conn, "architect_max_missions_per_voyage").ConfigureAwait(false), "architect_max_missions_per_voyage should be INTEGER");
                    }
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            });

            await RunTest("VesselArchitectMaxMissionsPerVoyage_Enumerate_ReturnsValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vesselA = new Vessel("ArchitectCapEnumA", "https://github.com/test/architect-cap-a");
                    vesselA.ArchitectMaxMissionsPerVoyage = 7;
                    Vessel vesselB = new Vessel("ArchitectCapEnumB", "https://github.com/test/architect-cap-b");
                    vesselB.ArchitectMaxMissionsPerVoyage = 20;

                    await testDb.Driver.Vessels.CreateAsync(vesselA).ConfigureAwait(false);
                    await testDb.Driver.Vessels.CreateAsync(vesselB).ConfigureAwait(false);

                    List<Vessel> all = await testDb.Driver.Vessels.EnumerateAsync().ConfigureAwait(false);
                    Vessel? loadedA = all.Find(v => v.Id == vesselA.Id);
                    Vessel? loadedB = all.Find(v => v.Id == vesselB.Id);

                    AssertNotNull(loadedA, "vesselA should appear in EnumerateAsync");
                    AssertEqual(7, loadedA!.ArchitectMaxMissionsPerVoyage!.Value, "vesselA architect cap should hydrate from enumerate");

                    AssertNotNull(loadedB, "vesselB should appear in EnumerateAsync");
                    AssertEqual(20, loadedB!.ArchitectMaxMissionsPerVoyage!.Value, "vesselB architect cap should hydrate from enumerate");
                }
            });

            await RunTest("VesselArchitectMaxMissionsPerVoyage_IndependentOfReflectionThreshold", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("ArchitectCapIndependent", "https://github.com/test/architect-cap-independent");
                    vessel.ReflectionThreshold = 5;
                    vessel.ArchitectMaxMissionsPerVoyage = 15;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? loaded = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(loaded, "vessel should load");
                    AssertEqual(5, loaded!.ReflectionThreshold!.Value, "ReflectionThreshold should remain distinct");
                    AssertEqual(15, loaded.ArchitectMaxMissionsPerVoyage!.Value, "ArchitectMaxMissionsPerVoyage should persist alongside reflection threshold");
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
