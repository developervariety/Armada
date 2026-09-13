namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>Metadata migration rejection, populated upgrade and interrupted restart proof.</summary>
    internal sealed class BackendMetadataMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }
        internal BackendMetadataMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 84, DatabaseTypeEnum.Postgresql => 85,
                DatabaseTypeEnum.Mysql => 76, DatabaseTypeEnum.SqlServer => 79,
                _ => throw new NotSupportedException()
            };
            await StopAtAsync(version, -1, token).ConfigureAwait(false);
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            string id = "metadata-legacy-" + Guid.NewGuid().ToString("N");
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO captains(id,name,runtime,state,recovery_attempts,created_utc,last_update_utc) VALUES(@id,@name,'Custom','Idle',0,@created,@created);";
                    DbParameter identifier = command.CreateParameter(); identifier.ParameterName = "@id"; identifier.Value = id; command.Parameters.Add(identifier);
                    DbParameter name = command.CreateParameter(); name.ParameterName = "@name"; name.Value = "Legacy 日本語 " + id; command.Parameters.Add(name);
                    DbParameter created = command.CreateParameter(); created.ParameterName = "@created";
                    created.Value = _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer
                        ? "2020-01-02T03:04:05.0000000Z" : new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                    command.Parameters.Add(created);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                string text = _Settings.Type == DatabaseTypeEnum.Mysql ? "VARCHAR(32)" : _Settings.Type == DatabaseTypeEnum.SqlServer ? "NVARCHAR(32)" : "TEXT";
                foreach (string declaration in new[] { "INTEGER NULL", text + " NOT NULL DEFAULT 'Premium'", text + " NULL DEFAULT 'Premium'" })
                {
                    await ExecuteAsync(connection, "ALTER TABLE captains ADD tier " + declaration + ";", token).ConfigureAwait(false);
                    await AssertRejectedAsync(before, history, "Incompatible tier " + declaration, token).ConfigureAwait(false);
                    if (_Settings.Type == DatabaseTypeEnum.SqlServer)
                        await ExecuteAsync(connection, "DECLARE @constraint_name NVARCHAR(128); SELECT @constraint_name=dc.name FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id WHERE dc.parent_object_id=OBJECT_ID('captains') AND c.name='tier'; IF @constraint_name IS NOT NULL EXEC('ALTER TABLE captains DROP CONSTRAINT [' + @constraint_name + ']');", token).ConfigureAwait(false);
                    await ExecuteAsync(connection, "ALTER TABLE captains DROP COLUMN tier;", token).ConfigureAwait(false);
                }
                await ExecuteAsync(connection, "ALTER TABLE captains ADD tier " + text + " NULL DEFAULT NULL;", token).ConfigureAwait(false);
                await ExecuteAsync(connection, "UPDATE captains SET tier='Premium';", token).ConfigureAwait(false);
                if (_Settings.Type == DatabaseTypeEnum.Mysql)
                {
                    foreach (string charset in new[] { "latin1", "utf8mb3" })
                    {
                        await ExecuteAsync(connection, "ALTER TABLE voyages ADD source_planning_session_id VARCHAR(450) CHARACTER SET " + charset + " NULL;", token).ConfigureAwait(false);
                        await AssertRejectedAsync(before, history, "Restricted provenance charset " + charset, token).ConfigureAwait(false);
                        await ExecuteAsync(connection, "ALTER TABLE voyages DROP COLUMN source_planning_session_id;", token).ConfigureAwait(false);
                    }
                }
            }
            await StopAtAsync(version, 1, token).ConfigureAwait(false);
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "Interrupted metadata version not recorded");
            MigrationScenarioRunner.AssertHistory(before, interrupted);
            await StopAtAsync(version, -2, token).ConfigureAwait(false);
            Dictionary<int, string> committed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count + 1, committed.Count, "Only metadata version committed at checkpoint");
            MigrationScenarioRunner.AssertHistory(before, committed);
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Captain captain = DatabaseAssert.NotNull(await driver.Captains.ReadAsync(id, token).ConfigureAwait(false), "Legacy captain retained");
                DatabaseAssert.Equal("Legacy 日本語 " + id, captain.Name, "Legacy name retained");
                DatabaseAssert.Equal<CaptainTierEnum?>(CaptainTierEnum.Premium, captain.Tier, "Equivalent tier retained");
                await driver.InitializeAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(committed, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                await driver.Captains.DeleteAsync(id, token).ConfigureAwait(false);
            }
            Console.WriteLine("PASS metadata migration: populated upgrade, equivalent values, incompatible type/null/default, interrupted restart and unchanged history");
        }

        private async Task AssertRejectedAsync(Dictionary<int, string> before, MigrationScenarioRunner history, string label, CancellationToken token)
        {
            bool rejected = false;
            using (DatabaseDriver driver = CreateDriver())
            {
                try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                catch (InvalidOperationException) { rejected = true; }
            }
            DatabaseAssert.True(rejected, label);
            Dictionary<int, string> after = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, after.Count, "Rejected schema cannot advance history");
            MigrationScenarioRunner.AssertHistory(before, after);
        }

        private async Task StopAtAsync(int version, int ordinal, CancellationToken token)
        {
            using (DatabaseDriver driver = CreateDriver())
            {
                driver.MigrationCheckpoint = (current, statement) =>
                {
                    if (current == version && statement == ordinal) throw new StopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Metadata fixture checkpoint was not reached"); }
                catch (StopException) { }
            }
        }

        private DatabaseDriver CreateDriver()
        {
            LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
            return DatabaseDriverFactory.Create(_Settings, logging);
        }

        private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}
