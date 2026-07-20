namespace Armada.Test.Unit
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
    /// Migration-definition and SQLite round-trip tests for Mission.CapabilityHint (schema v56).
    /// </summary>
    public class CapabilityHintSchemaTests : TestSuite
    {
        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "CapabilityHint Schema";

        /// <summary>
        /// Run capability hint schema tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MigrationProviderDefinitions_AllContainCapabilityHintMigration", () =>
            {
                AssertCapabilityHintMigration(SqliteTableQueries.GetMigrations(), "SQLite");
                AssertCapabilityHintMigration(PostgresqlTableQueries.GetMigrations(), "PostgreSQL");
                AssertCapabilityHintMigration(SqlServerTableQueries.GetMigrations(), "SQL Server");
                AssertCapabilityHintMigration(GetMysqlMigrations(), "MySQL");

                AssertEqual(1, MysqlTableQueries.MigrationV56Statements.Length, "MySQL v56 should have one statement");
                AssertContains("missions", MysqlTableQueries.MigrationV56Statements[0], "MySQL v56 should target missions");
                AssertContains("capabilityhint", MysqlTableQueries.MigrationV56Statements[0], "MySQL v56 should add capabilityhint");
                AssertContains("LONGTEXT", MysqlTableQueries.MigrationV56Statements[0], "MySQL capabilityhint column should use LONGTEXT");
            });

            await RunTest("MissionCapabilityHint_SetValue_RoundTripsThroughSqlite", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMinimalMissionAsync(testDb).ConfigureAwait(false);
                    mission.CapabilityHint = "reasoning-heavy";

                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "mission should be readable after create");
                    AssertEqual("reasoning-heavy", readBack!.CapabilityHint, "CapabilityHint should round-trip on create");
                }
            });

            await RunTest("MissionCapabilityHint_NullDefault_RoundTripsThroughSqlite", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMinimalMissionAsync(testDb).ConfigureAwait(false);
                    mission.CapabilityHint = null;

                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "mission should be readable after create");
                    AssertNull(readBack!.CapabilityHint, "CapabilityHint should remain null when unset");
                }
            });

            await RunTest("MissionCapabilityHint_Update_ClearAndSet_RoundTripsThroughSqlite", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMinimalMissionAsync(testDb).ConfigureAwait(false);
                    mission.CapabilityHint = "mechanical";

                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? created = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(created, "mission should be readable after create");
                    AssertEqual("mechanical", created!.CapabilityHint, "CapabilityHint should round-trip on create");

                    created.CapabilityHint = "doc-only";
                    await testDb.Driver.Missions.UpdateAsync(created).ConfigureAwait(false);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "mission should be readable after update");
                    AssertEqual("doc-only", updated!.CapabilityHint, "CapabilityHint should round-trip on update");

                    updated.CapabilityHint = null;
                    await testDb.Driver.Missions.UpdateAsync(updated).ConfigureAwait(false);

                    Mission? cleared = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(cleared, "mission should be readable after clearing CapabilityHint");
                    AssertNull(cleared!.CapabilityHint, "CapabilityHint should clear to null on update");
                }
            });

            await RunTest("MigrationV56_PreExistingMission_ReadsCapabilityHintAsNull", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_cap_hint_v56_" + Guid.NewGuid().ToString("N") + ".db");
                string connectionString = "Data Source=" + tempFile;

                try
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    Mission mission;
                    SqliteDatabaseDriver setupDriver = new SqliteDatabaseDriver(connectionString, logging);
                    await setupDriver.InitializeAsync().ConfigureAwait(false);

                    Fleet fleet = new Fleet("CapabilityHint Upgrade Fleet");
                    await setupDriver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("CapabilityHint Upgrade Vessel", "https://github.com/test/capability-hint-upgrade");
                    vessel.FleetId = fleet.Id;
                    await setupDriver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = new Voyage("CapabilityHint Upgrade Voyage", "pre-column mission");
                    await setupDriver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    mission = new Mission("CapabilityHint Upgrade Mission", "existed before v56");
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.CapabilityHint = "audit";
                    await setupDriver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    setupDriver.Dispose();

                    using (SqliteConnection conn = new SqliteConnection(connectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand deleteMigration = conn.CreateCommand())
                        {
                            deleteMigration.CommandText = "DELETE FROM schema_migrations WHERE version = 56;";
                            await deleteMigration.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }

                        using (SqliteCommand dropColumn = conn.CreateCommand())
                        {
                            dropColumn.CommandText = "ALTER TABLE missions DROP COLUMN capabilityhint;";
                            await dropColumn.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging);
                    await driver.InitializeAsync().ConfigureAwait(false);

                    Mission? readBack = await driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    driver.Dispose();

                    AssertNotNull(readBack, "pre-existing mission should remain readable after v56 upgrade");
                    AssertNull(readBack!.CapabilityHint, "CapabilityHint should be null for rows that existed before the column was added");
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            });
        }

        private void AssertCapabilityHintMigration(List<SchemaMigration> migrations, string backendName)
        {
            List<SchemaMigration> capabilityMigrations = new List<SchemaMigration>();
            for (int i = 0; i < migrations.Count; i++)
            {
                if (MigrationReferencesCapabilityHintOnMissions(migrations[i]))
                {
                    capabilityMigrations.Add(migrations[i]);
                }
            }

            AssertEqual(1, capabilityMigrations.Count, backendName + " should define exactly one capabilityhint migration");

            SchemaMigration capabilityMigration = capabilityMigrations[0];

            // Deliberately NOT asserting that capabilityhint sits at the schema head. That invariant
            // could only ever hold until the next migration landed, so it failed the moment
            // workflow_profiles.containerless_unit_test_command was added -- a false alarm about a
            // perfectly valid additive migration. What actually matters is that the migration exists
            // exactly once (checked above), that it advances by one from its predecessor (checked
            // below), and that no two migrations share a version, which is the real corruption risk
            // when several changes add migrations around the same time.
            HashSet<int> seenVersions = new HashSet<int>();
            int previousVersion = 0;
            for (int i = 0; i < migrations.Count; i++)
            {
                AssertTrue(seenVersions.Add(migrations[i].Version),
                    backendName + " defines duplicate schema migration version " + migrations[i].Version);
                AssertTrue(migrations[i].Version > previousVersion,
                    backendName + " schema migrations must be declared in strictly increasing version order");
                previousVersion = migrations[i].Version;
            }

            int priorHead = 0;
            for (int i = 0; i < migrations.Count; i++)
            {
                if (migrations[i].Version < capabilityMigration.Version && migrations[i].Version > priorHead)
                {
                    priorHead = migrations[i].Version;
                }
            }

            AssertTrue(priorHead > 0, backendName + " should have a migration before capabilityhint");
            AssertEqual(priorHead + 1, capabilityMigration.Version, backendName + " schema head should advance by one for capabilityhint");
        }

        private static bool MigrationReferencesCapabilityHintOnMissions(SchemaMigration migration)
        {
            for (int i = 0; i < migration.Statements.Count; i++)
            {
                string statement = migration.Statements[i];
                if (statement.Contains("capabilityhint", StringComparison.OrdinalIgnoreCase)
                    && statement.Contains("missions", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private List<SchemaMigration> GetMysqlMigrations()
        {
            System.Reflection.MethodInfo? mysqlGetMigrations = typeof(MysqlDatabaseDriver).GetMethod(
                "GetMigrations",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            AssertNotNull(mysqlGetMigrations, "MySQL driver must expose its migration registration list internally");
            object? mysqlMigrationsObject = mysqlGetMigrations!.Invoke(null, Array.Empty<object>());
            AssertNotNull(mysqlMigrationsObject, "MySQL driver migration list should not be null");
            return (List<SchemaMigration>)mysqlMigrationsObject!;
        }

        private static async Task<Mission> CreateMinimalMissionAsync(TestDatabase testDb)
        {
            Fleet fleet = new Fleet("CapabilityHint Fleet");
            await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

            Vessel vessel = new Vessel("CapabilityHint Vessel", "https://github.com/test/capability-hint");
            vessel.FleetId = fleet.Id;
            await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Voyage voyage = new Voyage("CapabilityHint Voyage", "schema round trip");
            await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission mission = new Mission("CapabilityHint Mission", "capability hint persistence");
            mission.VoyageId = voyage.Id;
            mission.VesselId = vessel.Id;
            return mission;
        }
    }
}
