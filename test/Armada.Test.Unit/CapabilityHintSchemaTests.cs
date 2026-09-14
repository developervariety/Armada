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

                    // Stop before v56 so the mission row is written by a schema without the column.
                    bool stopped = false;
                    SqliteDatabaseDriver setupDriver = new SqliteDatabaseDriver(connectionString, logging);
                    setupDriver.MigrationCheckpoint = (migrationVersion, ordinal) =>
                    {
                        if (migrationVersion == 56 && ordinal == -1) throw new OperationCanceledException("stop before v56");
                    };
                    try { await setupDriver.InitializeAsync().ConfigureAwait(false); }
                    catch (OperationCanceledException) { stopped = true; }
                    setupDriver.Dispose();
                    AssertTrue(stopped, "fixture stopped before v56");

                    string missionId = "msn_capability_hint_v55_" + Guid.NewGuid().ToString("N");
                    using (SqliteConnection conn = new SqliteConnection(connectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand column = conn.CreateCommand())
                        {
                            column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('missions') WHERE name = 'capabilityhint';";
                            AssertEqual(0L, Convert.ToInt64(await column.ExecuteScalarAsync().ConfigureAwait(false)), "missions has no capabilityhint column before v56");
                        }

                        using (SqliteCommand insert = conn.CreateCommand())
                        {
                            insert.CommandText = "INSERT INTO missions (id, title, description, status, created_utc, last_update_utc) VALUES (@id, 'CapabilityHint Upgrade Mission', 'existed before v56', 'Pending', '2020-01-02T03:04:05.0000000Z', '2020-01-02T03:04:05.0000000Z');";
                            insert.Parameters.AddWithValue("@id", missionId);
                            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    SqliteDatabaseDriver driver = new SqliteDatabaseDriver(connectionString, logging);
                    await driver.InitializeAsync().ConfigureAwait(false);

                    Mission? readBack = await driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
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
