namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using MysqlTableQueries = Armada.Core.Database.Mysql.Queries.TableQueries;
    using PostgresqlTableQueries = Armada.Core.Database.Postgresql.Queries.TableQueries;
    using SqliteTableQueries = Armada.Core.Database.Sqlite.Queries.TableQueries;
    using SqlServerTableQueries = Armada.Core.Database.SqlServer.Queries.TableQueries;

    /// <summary>
    /// Verifies the objective AutoDispatchEnabled opt-in flag persists across create/update and is wired into every backend migration.
    /// </summary>
    public class ObjectiveAutoDispatchTests : TestSuite
    {
        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "Objective AutoDispatch";

        /// <summary>
        /// Run objective auto-dispatch persistence tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            // DB round-trip coverage uses the SQLite TestDatabaseHelper; the other three backends are covered by migration-definition assertions below.
            await RunTest("DefaultConstruct_AutoDispatchEnabledIsFalse", () =>
            {
                Objective objective = new Objective { Title = "T" };
                AssertEqual(false, objective.AutoDispatchEnabled, "AutoDispatchEnabled should default to false");
                return Task.CompletedTask;
            });

            await RunTest("CreateWithAutoDispatchTrue_RoundTripsTrue", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective created = await db.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Opted in",
                        AutoDispatchEnabled = true
                    }).ConfigureAwait(false);

                    Objective? loaded = await db.Driver.Objectives.ReadAsync(created.Id).ConfigureAwait(false);

                    AssertNotNull(loaded, "Objective should load");
                    AssertEqual(true, loaded!.AutoDispatchEnabled, "AutoDispatchEnabled=true should round-trip from the database");
                }
            });

            await RunTest("CreateDefault_RoundTripsFalse", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective created = await db.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Not opted in"
                    }).ConfigureAwait(false);

                    Objective? loaded = await db.Driver.Objectives.ReadAsync(created.Id).ConfigureAwait(false);

                    AssertNotNull(loaded, "Objective should load");
                    AssertEqual(false, loaded!.AutoDispatchEnabled, "Default AutoDispatchEnabled should round-trip as false");
                }
            });

            await RunTest("UpdateFlipsTrueToFalse_Persists", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective created = await db.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Flip me",
                        AutoDispatchEnabled = true
                    }).ConfigureAwait(false);

                    created.AutoDispatchEnabled = false;
                    await db.Driver.Objectives.UpdateAsync(created).ConfigureAwait(false);

                    Objective? loaded = await db.Driver.Objectives.ReadAsync(created.Id).ConfigureAwait(false);

                    AssertNotNull(loaded, "Objective should load");
                    AssertEqual(false, loaded!.AutoDispatchEnabled, "Update flipping true->false should persist false");
                }
            });

            await RunTest("MigrationDefinitions_ContainVersion53AcrossAllBackends", () =>
            {
                SchemaMigration? sqlite53 = SqliteTableQueries.GetMigrations().Find(m => m.Version == 53);
                AssertNotNull(sqlite53, "SQLite migrations must include v53");
                AssertContains("auto_dispatch_enabled", sqlite53!.Statements[0]);

                SchemaMigration? postgres53 = PostgresqlTableQueries.GetMigrations().Find(m => m.Version == 53);
                AssertNotNull(postgres53, "PostgreSQL migrations must include v53");
                AssertContains("auto_dispatch_enabled", postgres53!.Statements[0]);

                SchemaMigration? sqlServer53 = SqlServerTableQueries.GetMigrations().Find(m => m.Version == 53);
                AssertNotNull(sqlServer53, "SQL Server migrations must include v53");
                AssertContains("auto_dispatch_enabled", sqlServer53!.Statements[0]);

                AssertEqual(1, MysqlTableQueries.MigrationV53Statements.Length, "MySQL v53 should define exactly one ALTER statement");
                AssertContains("auto_dispatch_enabled", MysqlTableQueries.MigrationV53Statements[0]);
                AssertContains("objectives", MysqlTableQueries.MigrationV53Statements[0]);
                return Task.CompletedTask;
            });
        }
    }
}
