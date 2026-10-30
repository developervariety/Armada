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

    /// <summary>Populated upgrade and interrupted restart of the two optional generated-artifact columns.</summary>
    internal sealed class GeneratedArtifactMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        internal GeneratedArtifactMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Postgresql => 150, DatabaseTypeEnum.Sqlite => 145,
                DatabaseTypeEnum.Mysql => 138, DatabaseTypeEnum.SqlServer => 141,
                _ => throw new NotSupportedException()
            };
            await StopAtAsync(version, -1, token).ConfigureAwait(false);
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            string suffix = Guid.NewGuid().ToString("N");
            string vesselId = "generated-legacy-vessel-" + suffix;
            string missionId = "generated-legacy-mission-" + suffix;
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                await InsertLegacyAsync(connection,
                    "INSERT INTO vessels(id,name,repo_url,default_branch,created_utc,last_update_utc) VALUES(@id,'legacy','https://example.test/repo.git','main',@created,@created);",
                    vesselId, token).ConfigureAwait(false);
                await InsertLegacyAsync(connection,
                    "INSERT INTO missions(id,title,description,status,created_utc,last_update_utc) VALUES(@id,'legacy','retained legacy content','Pending',@created,@created);",
                    missionId, token).ConfigureAwait(false);
            }
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                await RefuseMissingColumnAsync(connection, "vessels", "generated_artifacts_json", token).ConfigureAwait(false);
                await RefuseMissingColumnAsync(connection, "missions", "generated_artifact_review_json", token).ConfigureAwait(false);
            }
            await StopAtAsync(version, 0, token).ConfigureAwait(false);
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "Interrupted two-column migration is not recorded complete");
            MigrationScenarioRunner.AssertHistory(before, interrupted);
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Vessel vessel = DatabaseAssert.NotNull(await driver.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false), "Legacy vessel retained");
                Mission mission = DatabaseAssert.NotNull(await driver.Missions.ReadAsync(missionId, token).ConfigureAwait(false), "Legacy mission retained");
                DatabaseAssert.True(vessel.GeneratedArtifacts == null, "An existing vessel reads null policy as ordinary landing");
                DatabaseAssert.True(mission.GeneratedArtifactReview == null, "An existing mission reads no generated candidate binding");
                DatabaseAssert.Equal("retained legacy content", mission.Description, "Legacy mission body unchanged");
                Dictionary<int, string> committed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                DatabaseAssert.Equal(before.Count + 1, committed.Count, "One version stores both nullable columns");
                MigrationScenarioRunner.AssertHistory(before, committed);
                await driver.InitializeAsync(token).ConfigureAwait(false);
                DatabaseAssert.Equal(committed.Count, (await history.ReadHistoryAsync(token).ConfigureAwait(false)).Count, "Restart does not add a second migration");
                using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
                {
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "DELETE FROM schema_migrations WHERE version=" + version + ";";
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
                await driver.InitializeAsync(token).ConfigureAwait(false);
                DatabaseAssert.Equal(committed.Count, (await history.ReadHistoryAsync(token).ConfigureAwait(false)).Count,
                    "Equivalent existing columns can replay without changing prior history");
                MigrationScenarioRunner.AssertHistory(before, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                DatabaseAssert.True((await driver.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false))!.GeneratedArtifacts == null,
                    "Replayed policy migration keeps the existing null default");
                DatabaseAssert.True((await driver.Missions.ReadAsync(missionId, token).ConfigureAwait(false))!.GeneratedArtifactReview == null,
                    "Replayed review migration keeps the existing null default");
                Console.WriteLine("PASS generated-artifact equivalent-column replay");
                await driver.Missions.DeleteAsync(missionId, token).ConfigureAwait(false);
                await driver.Vessels.DeleteAsync(vesselId, token).ConfigureAwait(false);
            }
            Console.WriteLine("PASS generated-artifact migration: legacy null defaults, interrupted first column, restart and unchanged prior history");
        }

        private static async Task RefuseMissingColumnAsync(DbConnection connection, string table, string column, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT " + column + " FROM " + table + ";";
                try { await command.ExecuteScalarAsync(token).ConfigureAwait(false); }
                catch (DbException ex) when (ex.Message.Contains(column, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("RED before migration: " + table + "." + column + " is unavailable");
                    return;
                }
                throw new Exception("The legacy schema unexpectedly already has " + table + "." + column);
            }
        }

        private async Task InsertLegacyAsync(DbConnection connection, string sql, string id, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                DbParameter identifier = command.CreateParameter(); identifier.ParameterName = "@id"; identifier.Value = id; command.Parameters.Add(identifier);
                DbParameter created = command.CreateParameter(); created.ParameterName = "@created";
                created.Value = _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer
                    ? "2020-01-02T03:04:05.0000000Z" : new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                command.Parameters.Add(created);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private async Task StopAtAsync(int version, int ordinal, CancellationToken token)
        {
            LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
            using (DatabaseDriver driver = DatabaseDriverFactory.Create(_Settings, logging))
            {
                driver.MigrationCheckpoint = (current, statement) =>
                { if (current == version && statement == ordinal) throw new StopException(); };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Generated-artifact migration checkpoint was not reached"); }
                catch (StopException)
                { Console.WriteLine("Reached generated-artifact migration checkpoint " + version + "/" + ordinal); }
            }
        }
    }
}
