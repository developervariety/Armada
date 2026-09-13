namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.SqlServer.Queries;
    using Armada.Core.Settings;
    using Microsoft.Data.SqlClient;
    using SyslogLogging;

    /// <summary>Pre-staged SQL Server correction fixtures in a dedicated empty database.</summary>
    internal sealed class SqlServerCorrectionTests
    {
        private readonly DatabaseSettings _Settings;
        internal SqlServerCorrectionTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            await StopBeforeAsync(59, token).ConfigureAwait(false);
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            using (SqlConnection connection = new SqlConnection(_Settings.GetConnectionString()))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                await ExecuteAsync(connection, "ALTER TABLE missions ADD mission_mode NVARCHAR(64);", token).ConfigureAwait(false);
                await RejectAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(before, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                await ExecuteAsync(connection, "ALTER TABLE missions DROP COLUMN mission_mode; ALTER TABLE missions ADD mission_mode AS CONVERT(NVARCHAR(MAX), NULL);", token).ConfigureAwait(false);
                await RejectAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(before, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                await ExecuteAsync(connection, "ALTER TABLE missions DROP COLUMN mission_mode; ALTER TABLE missions ADD mission_mode NVARCHAR(MAX);", token).ConfigureAwait(false);
                await StopBeforeAsync(68, token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(before, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                SchemaMigration tokenMigration = TableQueries.GetMigrations().Find(migration => migration.Version == 68);
                await ExecuteAsync(connection, tokenMigration.Statements[0], token).ConfigureAwait(false);
                await ExecuteAsync(connection, "ALTER TABLE token_usage ADD model_index_prefix AS CONVERT(NVARCHAR(64),model) PERSISTED;", token).ConfigureAwait(false);
                await RejectAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(before, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                await ExecuteAsync(connection, "ALTER TABLE token_usage DROP COLUMN model_index_prefix; ALTER TABLE token_usage ADD model_index_prefix AS CONVERT(NVARCHAR(450),model) PERSISTED; CREATE INDEX idx_token_usage_model ON token_usage(model_index_prefix) INCLUDE(model);", token).ConfigureAwait(false);
                string fullModel = new string('雪', 900);
                using (SqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO token_usage(id,model,source,created_utc) VALUES ('historical-token',@model,'Mission','2024-02-03T04:05:06.1234560Z');";
                    command.Parameters.AddWithValue("@model", fullModel);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false)) { }
                MigrationScenarioRunner.AssertHistory(before, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                using (SqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT model FROM token_usage WHERE id='historical-token';";
                    DatabaseAssert.Equal(fullModel, (string)await command.ExecuteScalarAsync(token).ConfigureAwait(false), "Complete historical model retained");
                    command.CommandText = "SELECT COUNT(*) FROM schema_repairs WHERE id LIKE 'sqlserver-pending-%';";
                    DatabaseAssert.Equal(2, Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)), "Corrections have separate evidence records");
                }
            }
            Console.WriteLine("PASS SQL Server pending59/68 equivalent/incompatible objects, full model and unchanged history");
        }

        private async Task StopBeforeAsync(int target, CancellationToken token)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            bool stopped = false;
            using (DatabaseDriver driver = DatabaseDriverFactory.Create(_Settings, logging))
            {
                driver.MigrationCheckpoint = (version, ordinal) =>
                {
                    if (version == target && ordinal == -1) throw new FixtureStopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                catch (FixtureStopException) { stopped = true; }
            }
            DatabaseAssert.True(stopped, "Reached pending SQL Server migration " + target);
        }

        private async Task RejectAsync(CancellationToken token)
        {
            try
            {
                using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false)) { }
            }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("Incompatible", StringComparison.Ordinal)) { return; }
            throw new Exception("Incompatible pre-staged correction was accepted");
        }

        private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken token)
        {
            using (SqlCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private sealed class FixtureStopException : Exception { }
    }
}
