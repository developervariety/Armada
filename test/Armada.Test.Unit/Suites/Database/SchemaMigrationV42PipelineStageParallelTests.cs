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
    /// Verifies that migration v42 drops the unique constraint on pipeline_stages(pipeline_id, stage_order),
    /// enabling same-order parallel sibling stages (e.g. ReflectionsDualJudge with two Judge stages at Order 2).
    /// </summary>
    public class SchemaMigrationV42PipelineStageParallelTests : TestSuite
    {
        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "Schema Migration V42 Pipeline Stage Parallel";

        /// <summary>
        /// Run migration v42 tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MigrationProviderDefinitions_AllContainVersion42", () =>
            {
                List<SchemaMigration> sqlite = SqliteTableQueries.GetMigrations();
                SchemaMigration? sqlite42 = sqlite.Find(m => m.Version == 42);
                AssertNotNull(sqlite42, "SQLite migrations must include v42");
                AssertContains("idx_pipeline_stages_order", sqlite42!.Statements[0]);

                List<SchemaMigration> pg = PostgresqlTableQueries.GetMigrations();
                SchemaMigration? pg42 = pg.Find(m => m.Version == 42);
                AssertNotNull(pg42, "PostgreSQL migrations must include v42");
                AssertContains("idx_pipeline_stages_order", pg42!.Statements[0]);

                List<SchemaMigration> ss = SqlServerTableQueries.GetMigrations();
                SchemaMigration? ss42 = ss.Find(m => m.Version == 42);
                AssertNotNull(ss42, "SQL Server migrations must include v42");
                AssertContains("idx_pipeline_stages_order", ss42!.Statements[0]);

                AssertEqual(2, MysqlTableQueries.MigrationV42Statements.Length);
                AssertContains("idx_pipeline_stages_order", MysqlTableQueries.MigrationV42Statements[0]);

                System.Reflection.MethodInfo? mysqlGetMigrations = typeof(MysqlDatabaseDriver).GetMethod("GetMigrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                AssertNotNull(mysqlGetMigrations, "MySQL driver must expose its migration registration list internally");
                object? mysqlMigrationsObject = mysqlGetMigrations!.Invoke(null, Array.Empty<object>());
                AssertNotNull(mysqlMigrationsObject, "MySQL driver migration list should not be null");
                List<SchemaMigration> mysql = (List<SchemaMigration>)mysqlMigrationsObject!;
                SchemaMigration? mysql42 = mysql.Find(m => m.Version == 42);
                AssertNotNull(mysql42, "MySQL driver migrations must register v42");
                AssertContains("idx_pipeline_stages_order", mysql42!.Statements[0]);
            });

            await RunTest("MigrationV42_AllowsSameOrderStagesInPipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Pipeline pipeline = new Pipeline("DualJudgeParallelTest");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "MemoryConsolidator") { PreferredModel = "high" },
                        new PipelineStage(2, "Judge") { PreferredModel = "high" },
                        new PipelineStage(2, "Judge") { PreferredModel = "high" }
                    };

                    Pipeline created = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Pipeline? loaded = await testDb.Driver.Pipelines.ReadByNameAsync("DualJudgeParallelTest").ConfigureAwait(false);
                    AssertNotNull(loaded, "Pipeline with same-order stages should be persisted after v42 migration");
                    AssertEqual(3, loaded!.Stages.Count, "All three stages should be persisted including both Order-2 Judge stages");

                    List<PipelineStage> ordered = loaded.Stages.OrderBy(s => s.Order).ToList();
                    AssertEqual(1, ordered[0].Order, "First stage order");
                    AssertEqual("MemoryConsolidator", ordered[0].PersonaName, "First stage persona");
                    AssertEqual(2, ordered[1].Order, "Second stage order");
                    AssertEqual("Judge", ordered[1].PersonaName, "Second stage persona");
                    AssertEqual(2, ordered[2].Order, "Third stage order (sibling)");
                    AssertEqual("Judge", ordered[2].PersonaName, "Third stage persona (sibling)");
                }
            });

            await RunTest("MigrationV42_RerunAfterIndexDropped_DoesNotFail", async () =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "armada_v42_parallel_" + Guid.NewGuid().ToString("N") + ".db");
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
                            cmd.CommandText = "DELETE FROM schema_migrations WHERE version = 42;";
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    SqliteDatabaseDriver driver2 = new SqliteDatabaseDriver(connectionString, logging);
                    await driver2.InitializeAsync().ConfigureAwait(false);
                    int version = await driver2.GetSchemaVersionAsync().ConfigureAwait(false);
                    driver2.Dispose();

                    AssertEqual(45, version, "schema version should return to head (v45) after idempotent rerun of v42");
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            });
        }
    }
}
