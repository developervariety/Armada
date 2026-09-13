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

    /// <summary>Anchor migration rejection, populated upgrade and interrupted restart proof.</summary>
    internal sealed class DockAnchorMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }
        internal DockAnchorMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 85, DatabaseTypeEnum.Postgresql => 86,
                DatabaseTypeEnum.Mysql => 77, DatabaseTypeEnum.SqlServer => 80,
                _ => throw new NotSupportedException()
            };
            await StopAtAsync(version, -1, token).ConfigureAwait(false);
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            string id = "anchor-legacy-" + Guid.NewGuid().ToString("N");
            string vesselId;
            using (DatabaseDriver driver = CreateDriver())
            {
                Vessel vessel = await driver.Vessels.CreateAsync(new Vessel("legacy", "https://example.invalid/repo"), token).ConfigureAwait(false);
                vesselId = vessel.Id;
            }
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO docks(id,vessel_id,branch_name,created_utc,last_update_utc) VALUES(@id,@vessel,@branch,@created,@created);";
                    DbParameter identifier = command.CreateParameter(); identifier.ParameterName = "@id"; identifier.Value = id; command.Parameters.Add(identifier);
                    DbParameter branch = command.CreateParameter(); branch.ParameterName = "@branch"; branch.Value = "legacy 日本語"; command.Parameters.Add(branch);
                    DbParameter vessel = command.CreateParameter(); vessel.ParameterName = "@vessel"; vessel.Value = vesselId; command.Parameters.Add(vessel);
                    DbParameter created = command.CreateParameter(); created.ParameterName = "@created";
                    created.Value = _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer
                        ? "2020-01-02T03:04:05.0000000Z" : new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                    command.Parameters.Add(created);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                string text = _Settings.Type == DatabaseTypeEnum.Mysql ? "LONGTEXT CHARACTER SET utf8mb4" : _Settings.Type == DatabaseTypeEnum.SqlServer ? "NVARCHAR(MAX)" : "TEXT";
                string incompatibleDefault = _Settings.Type == DatabaseTypeEnum.Mysql ? "('{}')" : "'{}'";
                foreach (string declaration in new[] { "INTEGER NULL", text + " NOT NULL DEFAULT " + incompatibleDefault, text + " NULL DEFAULT " + incompatibleDefault })
                {
                    await ExecuteAsync(connection, "ALTER TABLE docks ADD git_anchors_json " + declaration + ";", token).ConfigureAwait(false);
                    await AssertRejectedAsync(before, history, "Incompatible anchors " + declaration, token).ConfigureAwait(false);
                    if (_Settings.Type == DatabaseTypeEnum.SqlServer)
                        await ExecuteAsync(connection, "DECLARE @constraint_name NVARCHAR(128); SELECT @constraint_name=dc.name FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id WHERE dc.parent_object_id=OBJECT_ID('docks') AND c.name='git_anchors_json'; IF @constraint_name IS NOT NULL EXEC('ALTER TABLE docks DROP CONSTRAINT [' + @constraint_name + ']');", token).ConfigureAwait(false);
                    await ExecuteAsync(connection, "ALTER TABLE docks DROP COLUMN git_anchors_json;", token).ConfigureAwait(false);
                }
                if (_Settings.Type == DatabaseTypeEnum.Mysql)
                {
                    foreach (string charset in new[] { "latin1", "utf8mb3" })
                    {
                        await ExecuteAsync(connection, "ALTER TABLE docks ADD git_anchors_json LONGTEXT CHARACTER SET " + charset + " NULL;", token).ConfigureAwait(false);
                        await AssertRejectedAsync(before, history, "Restricted anchor charset " + charset, token).ConfigureAwait(false);
                        await ExecuteAsync(connection, "ALTER TABLE docks DROP COLUMN git_anchors_json;", token).ConfigureAwait(false);
                    }
                }
                await ExecuteAsync(connection, "ALTER TABLE docks ADD git_anchors_json " + text + " NULL DEFAULT NULL;", token).ConfigureAwait(false);
                await ExecuteAsync(connection, "UPDATE docks SET git_anchors_json='invalid optional JSON';", token).ConfigureAwait(false);
            }
            await StopAtAsync(version, 0, token).ConfigureAwait(false);
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "Interrupted anchor version not recorded");
            MigrationScenarioRunner.AssertHistory(before, interrupted);
            await StopAtAsync(version, -2, token).ConfigureAwait(false);
            Dictionary<int, string> committed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count + 1, committed.Count, "Only anchor version committed at checkpoint");
            MigrationScenarioRunner.AssertHistory(before, committed);
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Dock dock = DatabaseAssert.NotNull(await driver.Docks.ReadAsync(id, token).ConfigureAwait(false), "Legacy dock retained");
                DatabaseAssert.Equal("legacy 日本語", dock.BranchName, "Legacy branch retained");
                DatabaseAssert.True(dock.GitAnchorsSnapshot == null, "Malformed optional evidence is unavailable");
                await driver.InitializeAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(committed, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                await driver.Docks.DeleteAsync(id, token).ConfigureAwait(false);
            }
            Console.WriteLine("PASS anchor migration: populated upgrade, equivalent values, incompatible type/null/default, interrupted restart and unchanged history");
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
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Anchor fixture checkpoint was not reached"); }
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
