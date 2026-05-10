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
    /// Round-trip and migration tests for reorganize_threshold vessel persistence (schema v41).
    /// </summary>
    public class SchemaMigrationV41ReorganizeTests : TestSuite
    {
        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "Schema Migration V41 Reorganize";

        /// <summary>
        /// Run reorganize threshold schema tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MigrationProviderDefinitions_AllContainVersion41", () =>
            {
                List<SchemaMigration> sqlite = SqliteTableQueries.GetMigrations();
                SchemaMigration? sqlite41 = sqlite.Find(m => m.Version == 41);
                AssertNotNull(sqlite41, "SQLite migrations must include v41");
                AssertContains("reorganize_threshold", sqlite41!.Statements[0]);

                List<SchemaMigration> pg = PostgresqlTableQueries.GetMigrations();
                SchemaMigration? pg41 = pg.Find(m => m.Version == 41);
                AssertNotNull(pg41, "PostgreSQL migrations must include v41");
                AssertContains("reorganize_threshold", pg41!.Statements[0]);

                List<SchemaMigration> ss = SqlServerTableQueries.GetMigrations();
                SchemaMigration? ss41 = ss.Find(m => m.Version == 41);
                AssertNotNull(ss41, "SQL Server migrations must include v41");
                AssertContains("reorganize_threshold", ss41!.Statements[0]);

                AssertEqual(1, MysqlTableQueries.MigrationV41Statements.Length);
                AssertContains("reorganize_threshold", MysqlTableQueries.MigrationV41Statements[0]);

                System.Reflection.MethodInfo? mysqlGetMigrations = typeof(MysqlDatabaseDriver).GetMethod("GetMigrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                AssertNotNull(mysqlGetMigrations, "MySQL driver must expose its migration registration list internally");
                object? mysqlMigrationsObject = mysqlGetMigrations!.Invoke(null, Array.Empty<object>());
                AssertNotNull(mysqlMigrationsObject, "MySQL driver migration list should not be null");
                List<SchemaMigration> mysql = (List<SchemaMigration>)mysqlMigrationsObject!;
                SchemaMigration? mysql41 = mysql.Find(m => m.Version == 41);
                AssertNotNull(mysql41, "MySQL driver migrations must register v41");
                AssertContains("reorganize_threshold", mysql41!.Statements[0]);
            });

            await RunTest("VesselReorganizeThreshold_CreateReadUpdate_RoundTrip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("ReorganizeRoundTrip", "https://github.com/test/reorganize");
                    vessel.ReorganizeThreshold = 19;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? created = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(created, "vessel should be readable after create");
                    AssertEqual(19, created!.ReorganizeThreshold!.Value, "ReorganizeThreshold should round-trip on create");

                    created.ReorganizeThreshold = 4;
                    await testDb.Driver.Vessels.UpdateAsync(created).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "vessel should be readable after update");
                    AssertEqual(4, updated!.ReorganizeThreshold!.Value, "ReorganizeThreshold should round-trip on update");
                }
            });

            await RunTest("VesselReorganizeThreshold_NullDefaultsAndClear_RoundTrip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("ReorganizeNullDefaults", "https://github.com/test/reorganize-null");

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? created = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(created, "vessel should be readable after create");
                    AssertNull(created!.ReorganizeThreshold, "ReorganizeThreshold should default to null");

                    created.ReorganizeThreshold = 12;
                    await testDb.Driver.Vessels.UpdateAsync(created).ConfigureAwait(false);

                    created.ReorganizeThreshold = null;
                    await testDb.Driver.Vessels.UpdateAsync(created).ConfigureAwait(false);

                    Vessel? cleared = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(cleared, "vessel should be readable after clearing reorganize threshold");
                    AssertNull(cleared!.ReorganizeThreshold, "ReorganizeThreshold should clear to null");
                }
            });

            await RunTest("MigrationV41_RerunAfterColumnExists_DoesNotFail", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_v41_reorganize_" + Guid.NewGuid().ToString("N") + ".db");
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
                            cmd.CommandText = "DELETE FROM schema_migrations WHERE version = 41;";
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    SqliteDatabaseDriver driver2 = new SqliteDatabaseDriver(connectionString, logging);
                    await driver2.InitializeAsync().ConfigureAwait(false);
                    int version = await driver2.GetSchemaVersionAsync().ConfigureAwait(false);
                    driver2.Dispose();

                    AssertEqual(44, version, "schema version should return to head (v44) after idempotent rerun");

                    using (SqliteConnection conn = new SqliteConnection(connectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        AssertTrue(await ColumnExistsAsync(conn, "reorganize_threshold").ConfigureAwait(false), "reorganize_threshold should exist");
                    }
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            });

            await RunTest("VesselReorganizeThreshold_Enumerate_ReturnsValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vesselA = new Vessel("ReorganizeEnumA", "https://github.com/test/reorganize-a");
                    vesselA.ReorganizeThreshold = 6;
                    Vessel vesselB = new Vessel("ReorganizeEnumB", "https://github.com/test/reorganize-b");
                    vesselB.ReorganizeThreshold = 100;

                    await testDb.Driver.Vessels.CreateAsync(vesselA).ConfigureAwait(false);
                    await testDb.Driver.Vessels.CreateAsync(vesselB).ConfigureAwait(false);

                    List<Vessel> all = await testDb.Driver.Vessels.EnumerateAsync().ConfigureAwait(false);
                    Vessel? loadedA = all.Find(v => v.Id == vesselA.Id);
                    Vessel? loadedB = all.Find(v => v.Id == vesselB.Id);

                    AssertNotNull(loadedA, "vesselA should appear in EnumerateAsync");
                    AssertEqual(6, loadedA!.ReorganizeThreshold!.Value, "vesselA reorganize threshold should hydrate from enumerate");

                    AssertNotNull(loadedB, "vesselB should appear in EnumerateAsync");
                    AssertEqual(100, loadedB!.ReorganizeThreshold!.Value, "vesselB reorganize threshold should hydrate from enumerate");
                }
            });

            await RunTest("VesselReorganizeThreshold_IndependentOfReflectionThreshold", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("ReorganizeIndependent", "https://github.com/test/reorganize-independent");
                    vessel.ReflectionThreshold = 5;
                    vessel.ReorganizeThreshold = 88;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? loaded = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(loaded, "vessel should load");
                    AssertEqual(5, loaded!.ReflectionThreshold!.Value, "ReflectionThreshold should remain distinct");
                    AssertEqual(88, loaded.ReorganizeThreshold!.Value, "ReorganizeThreshold should persist alongside reflection threshold");
                }
            });

            await RunTest("MigrationV41_FreshInit_SchemaVersionAtLeastV41_AndColumnExists", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_v41_fresh_" + Guid.NewGuid().ToString("N") + ".db");
                string connectionString = "Data Source=" + tempFile;

                try
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging);
                    await driver.InitializeAsync().ConfigureAwait(false);
                    int version = await driver.GetSchemaVersionAsync().ConfigureAwait(false);
                    driver.Dispose();

                    AssertTrue(version >= 41, "schema version should be at least 41 after fresh init (was " + version + ")");

                    using (SqliteConnection conn = new SqliteConnection(connectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        AssertTrue(await ColumnExistsAsync(conn, "reorganize_threshold").ConfigureAwait(false), "reorganize_threshold should exist after fresh init");
                        AssertEqual("INTEGER", await ColumnTypeAsync(conn, "reorganize_threshold").ConfigureAwait(false), "reorganize_threshold should be INTEGER");
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
