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

    /// <summary>Admission populated upgrade and interrupted restart proof.</summary>
    internal sealed class MissionAdmissionMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }
        internal MissionAdmissionMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 87, DatabaseTypeEnum.Postgresql => 88,
                DatabaseTypeEnum.Mysql => 79, DatabaseTypeEnum.SqlServer => 82,
                _ => throw new NotSupportedException()
            };
            await StopAtAsync(version, -1, token).ConfigureAwait(false);
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            string id = "admission-legacy-" + Guid.NewGuid().ToString("N");
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO missions(id,title,description,status,created_utc,last_update_utc) VALUES(@id,'legacy','preserved','Pending',@created,@created);";
                    DbParameter identifier = command.CreateParameter(); identifier.ParameterName = "@id"; identifier.Value = id; command.Parameters.Add(identifier);
                    DbParameter created = command.CreateParameter(); created.ParameterName = "@created";
                    created.Value = _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer
                        ? "2020-01-02T03:04:05.0000000Z" : new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                    command.Parameters.Add(created);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            await StopAtAsync(version, 0, token).ConfigureAwait(false);
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "Interrupted admission version not recorded");
            MigrationScenarioRunner.AssertHistory(before, interrupted);
            await StopAtAsync(version, -2, token).ConfigureAwait(false);
            Dictionary<int, string> committed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count + 1, committed.Count, "Only admission version committed at checkpoint");
            MigrationScenarioRunner.AssertHistory(before, committed);
            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Mission mission = DatabaseAssert.NotNull(await driver.Missions.ReadAsync(id, token).ConfigureAwait(false), "Legacy mission retained");
                DatabaseAssert.Equal("preserved", mission.Description, "Legacy content retained");
                DatabaseAssert.True(mission.LastAdmissionObservation == null, "Legacy admission remains unavailable");
                await driver.InitializeAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(committed, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                await driver.Missions.DeleteAsync(id, token).ConfigureAwait(false);
            }
            Console.WriteLine("PASS admission migration: populated upgrade, interrupted restart and unchanged history");
        }

        private async Task StopAtAsync(int version, int ordinal, CancellationToken token)
        {
            using (DatabaseDriver driver = CreateDriver())
            {
                driver.MigrationCheckpoint = (current, statement) =>
                {
                    if (current == version && statement == ordinal) throw new StopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Admission fixture checkpoint was not reached"); }
                catch (StopException) { }
            }
        }

        private DatabaseDriver CreateDriver()
        {
            LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
            return DatabaseDriverFactory.Create(_Settings, logging);
        }

    }
}
