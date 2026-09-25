namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    public class SchemaMigrationTests : TestSuite
    {
        public override string Name => "Schema Migration";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Skipped migration rule lists unapplied known versions below the applied maximum", () =>
            {
                List<SchemaMigration> known = new List<SchemaMigration>
                {
                    new SchemaMigration(1, "one", "SELECT 1;"),
                    new SchemaMigration(2, "two", "SELECT 1;"),
                    new SchemaMigration(3, "three", "SELECT 1;"),
                    new SchemaMigration(5, "five", "SELECT 1;"),
                    new SchemaMigration(6, "six", "SELECT 1;")
                };
                AssertEqual("2,5", String.Join(",", AppliedMigrationLedger.FindSkippedVersions(new[] { 6, 1, 3 }, known)),
                    "known versions below the maximum without a ledger row, ascending; retired number 4 is not a gap");
            });

            await RunTest("Skipped migration rule accepts pending, retired and unknown ledger versions", () =>
            {
                List<SchemaMigration> known = new List<SchemaMigration>
                {
                    new SchemaMigration(1, "one", "SELECT 1;"),
                    new SchemaMigration(2, "two", "SELECT 1;"),
                    new SchemaMigration(4, "four", "SELECT 1;")
                };
                AssertEqual(0, AppliedMigrationLedger.FindSkippedVersions(new int[0], known).Count, "empty ledger is a fresh install");
                AssertEqual(0, AppliedMigrationLedger.FindSkippedVersions(new[] { 1 }, known).Count, "versions above the maximum are pending");
                AssertEqual(0, AppliedMigrationLedger.FindSkippedVersions(new[] { 1, 2, 4 }, known).Count, "retired number 3 is not a gap");
                AssertEqual(0, AppliedMigrationLedger.FindSkippedVersions(new[] { 1, 2, 4, 9 }, known).Count, "a ledger version the code does not know is not a gap");
                AssertEqual("4", String.Join(",", AppliedMigrationLedger.FindSkippedVersions(new[] { 1, 2, 9 }, known)), "a known version below an unknown maximum is a gap");
            });

            await RunTest("InitializeAsync refuses a ledger that skipped a lower known version", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_skipped_version_" + Guid.NewGuid().ToString("N") + ".db");
                string connectionString = "Data Source=" + tempFile;
                try
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    using (SqliteDatabaseDriver installer = new SqliteDatabaseDriver(connectionString, logging))
                    {
                        await installer.InitializeAsync();
                    }

                    List<SchemaMigration> migrations = Armada.Core.Database.Sqlite.Queries.TableQueries.GetMigrations();
                    int maximum = migrations[migrations.Count - 1].Version;
                    int belowMaximum = migrations[migrations.Count - 2].Version;
                    int middle = migrations[migrations.Count / 2].Version;
                    long installedCount;
                    using (SqliteConnection conn = new SqliteConnection(connectionString))
                    {
                        await conn.OpenAsync();
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "DELETE FROM schema_migrations WHERE version IN (" + middle + "," + belowMaximum + ");";
                            AssertEqual(2, await cmd.ExecuteNonQueryAsync(), "two ledger rows removed");
                        }
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
                            installedCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                        }
                    }

                    SkippedMigrationVersionsException? refusal = null;
                    using (SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging))
                    {
                        try { await driver.InitializeAsync(); }
                        catch (SkippedMigrationVersionsException ex) { refusal = ex; }
                    }

                    AssertNotNull(refusal, "startup must refuse instead of silently leaving v" + middle + " and v" + belowMaximum + " unapplied");
                    AssertEqual(middle + "," + belowMaximum, String.Join(",", refusal!.MissingVersions), "refusal lists the skipped versions ascending");
                    AssertEqual(maximum, refusal.AppliedMaximum, "refusal names the applied maximum");
                    AssertStartsWith("skipped_migration_versions:", refusal.Message, "refusal carries its stable name");
                    AssertContains("v" + middle + ", v" + belowMaximum, refusal.Message, "refusal message names the versions");

                    using (SqliteConnection conn = new SqliteConnection(connectionString))
                    {
                        await conn.OpenAsync();
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
                            AssertEqual(installedCount, Convert.ToInt64(await cmd.ExecuteScalarAsync()), "refused startup records no version");
                        }
                    }
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    try { File.Delete(tempFile); } catch (IOException ex) { Console.WriteLine("temp database not removed: " + ex.Message); }
                }
            });

        }
    }
}
