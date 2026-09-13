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

    /// <summary>Populated upgrade, incompatible column and interrupted preview migration proof.</summary>
    internal sealed class VesselPreviewMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        internal VesselPreviewMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 83, DatabaseTypeEnum.Postgresql => 84,
                DatabaseTypeEnum.Mysql => 75, DatabaseTypeEnum.SqlServer => 78,
                _ => throw new NotSupportedException()
            };
            using (DatabaseDriver driver = CreateDriver())
            {
                driver.MigrationCheckpoint = (current, ordinal) =>
                {
                    if (current == version && ordinal == -1) throw new StopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Fixture did not stop before preview migration"); }
                catch (StopException) { }
            }
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            string id = "preview-legacy-" + Guid.NewGuid().ToString("N");
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO vessels(id,name,repo_url,default_branch,active,created_utc,last_update_utc) VALUES(@id,@name,@repo,'main',@active,@created,@created);";
                    Add(command, "@id", id); Add(command, "@name", "Legacy 日本語 " + id);
                    Add(command, "@repo", "https://example.invalid/fixture.git");
                    Add(command, "@active", _Settings.Type == DatabaseTypeEnum.Postgresql ? true : 1);
                    Add(command, "@created", _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer
                        ? "2020-01-02T03:04:05.0000000Z" : new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                string boolType = _Settings.Type == DatabaseTypeEnum.Postgresql ? "BOOLEAN" : _Settings.Type == DatabaseTypeEnum.SqlServer ? "BIT" : "INTEGER";
                string falseLiteral = _Settings.Type == DatabaseTypeEnum.Postgresql ? "FALSE" : "0";
                string trueLiteral = _Settings.Type == DatabaseTypeEnum.Postgresql ? "TRUE" : "1";
                string textType = _Settings.Type == DatabaseTypeEnum.SqlServer ? "NVARCHAR(50)" : "VARCHAR(50)";
                foreach (string declaration in new[]
                {
                    textType + " NOT NULL DEFAULT 'wrong'",
                    boolType + " NULL DEFAULT " + falseLiteral,
                    boolType + " NOT NULL DEFAULT " + trueLiteral
                })
                {
                    await ExecuteAsync(connection, "ALTER TABLE vessels ADD require_passing_checks_to_land " + declaration + ";", token).ConfigureAwait(false);
                    bool rejected = false;
                    using (DatabaseDriver driver = CreateDriver())
                    {
                        try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                        catch (InvalidOperationException) { rejected = true; }
                    }
                    DatabaseAssert.True(rejected, "Incompatible preview column rejected: " + declaration);
                    Dictionary<int, string> afterFailure = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                    DatabaseAssert.Equal(before.Count, afterFailure.Count, "Rejected column cannot advance migration history");
                    MigrationScenarioRunner.AssertHistory(before, afterFailure);
                    if (_Settings.Type == DatabaseTypeEnum.SqlServer)
                        await ExecuteAsync(connection, "DECLARE @constraint_name NVARCHAR(128); SELECT @constraint_name=dc.name FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id WHERE dc.parent_object_id=OBJECT_ID('vessels') AND c.name='require_passing_checks_to_land'; IF @constraint_name IS NOT NULL EXEC('ALTER TABLE vessels DROP CONSTRAINT [' + @constraint_name + ']');", token).ConfigureAwait(false);
                    await ExecuteAsync(connection, "ALTER TABLE vessels DROP COLUMN require_passing_checks_to_land;", token).ConfigureAwait(false);
                }
                if (_Settings.Type == DatabaseTypeEnum.Mysql)
                {
                    foreach (string charset in new[] { "latin1", "utf8mb3" })
                    {
                        await ExecuteAsync(connection, "ALTER TABLE vessels ADD release_branch_prefix LONGTEXT CHARACTER SET " + charset + " NOT NULL DEFAULT ('release/');", token).ConfigureAwait(false);
                        bool rejected = false;
                        using (DatabaseDriver driver = CreateDriver())
                        {
                            try { await driver.InitializeAsync(token).ConfigureAwait(false); }
                            catch (InvalidOperationException) { rejected = true; }
                        }
                        DatabaseAssert.True(rejected, "Restricted preview text charset rejected: " + charset);
                        Dictionary<int, string> afterFailure = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                        DatabaseAssert.Equal(before.Count, afterFailure.Count, "Restricted charset cannot advance history");
                        MigrationScenarioRunner.AssertHistory(before, afterFailure);
                        await ExecuteAsync(connection, "ALTER TABLE vessels DROP COLUMN release_branch_prefix;", token).ConfigureAwait(false);
                    }
                    // Earlier compatible statements can persist after MySQL DDL failure.
                    await ExecuteAsync(connection, "ALTER TABLE vessels DROP COLUMN require_passing_checks_to_land;", token).ConfigureAwait(false);
                }
                await ExecuteAsync(connection, "ALTER TABLE vessels ADD require_passing_checks_to_land " + boolType + " NOT NULL DEFAULT " + falseLiteral + ";", token).ConfigureAwait(false);
                await ExecuteAsync(connection, "UPDATE vessels SET require_passing_checks_to_land=" + trueLiteral + ";", token).ConfigureAwait(false);
            }
            using (DatabaseDriver driver = CreateDriver())
            {
                driver.MigrationCheckpoint = (current, ordinal) =>
                {
                    if (current == version && ordinal == 2) throw new StopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Fixture did not interrupt preview migration"); }
                catch (StopException) { }
            }
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "Interrupted preview version is not applied");
            MigrationScenarioRunner.AssertHistory(before, interrupted);
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Vessel legacy = DatabaseAssert.NotNull(await driver.Vessels.ReadAsync(id, token).ConfigureAwait(false), "Legacy vessel retained");
                DatabaseAssert.Equal("Legacy 日本語 " + id, legacy.Name, "Legacy Unicode name retained");
                DatabaseAssert.True(legacy.RequirePassingChecksToLand, "Equivalent pre-existing value retained");
                DatabaseAssert.Equal(0, legacy.ProtectedBranchPatterns.Count, "New pattern default");
                DatabaseAssert.Equal("release/", legacy.ReleaseBranchPrefix, "New release prefix default");
                DatabaseAssert.Equal("hotfix/", legacy.HotfixBranchPrefix, "New hotfix prefix default");
                DatabaseAssert.True(!legacy.RequirePullRequestForProtectedBranches && !legacy.RequireMergeQueueForReleaseBranches, "New boolean defaults");
                await driver.InitializeAsync(token).ConfigureAwait(false);
                Dictionary<int, string> after = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                DatabaseAssert.Equal(before.Count + 1, after.Count, "Only the append-only preview version added");
                MigrationScenarioRunner.AssertHistory(before, after);
                await driver.Vessels.DeleteAsync(id, token).ConfigureAwait(false);
            }
            Console.WriteLine("PASS preview migration: populated upgrade, equivalent values, incompatible type/null/default, interrupted restart and original history");
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

        private static void Add(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
