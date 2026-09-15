namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Mission input-wait cancel migration proof. A database one version below the migration holds a mission stored
    /// as WaitingForInput with no failure reason, one stored as WaitingForInput with an earlier failure reason and a
    /// completion time, and an InProgress and a Cancelled mission beside them. The migration cancels only the two
    /// waiting missions, keeps their last update time, records the cancel reason ahead of the earlier one, restarts
    /// cleanly after an interruption and leaves every other row unchanged; every mission then reads back.
    /// </summary>
    internal sealed class MissionInputWaitCancelMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        internal MissionInputWaitCancelMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 102, DatabaseTypeEnum.Postgresql => 103,
                DatabaseTypeEnum.Mysql => 94, DatabaseTypeEnum.SqlServer => 97,
                _ => throw new NotSupportedException()
            };

            await StopAtAsync(version, -1, token).ConfigureAwait(false);
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(before.Keys.All(key => key < version), "The cancel is still pending and no later version applied");

            Mission bare;
            Mission prior;
            Mission running;
            Mission cancelled;
            using (DatabaseDriver driver = CreateDriver())
            {
                bare = await CreateMissionAsync(driver, "waiting without reason", MissionStatusEnum.InProgress, null, null, token).ConfigureAwait(false);
                prior = await CreateMissionAsync(driver, "waiting with reason", MissionStatusEnum.InProgress, "Judge verdict: FAIL", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), token).ConfigureAwait(false);
                running = await CreateMissionAsync(driver, "running", MissionStatusEnum.InProgress, null, null, token).ConfigureAwait(false);
                cancelled = await CreateMissionAsync(driver, "cancelled", MissionStatusEnum.Cancelled, "Operator cancelled", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), token).ConfigureAwait(false);
            }

            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                foreach (string id in new[] { bare.Id, prior.Id })
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "UPDATE missions SET status = 'WaitingForInput' WHERE id = '" + id + "';";
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }

            Dictionary<string, string> stampsBefore = await ReadStampsAsync(token).ConfigureAwait(false);

            await StopAtAsync(version, 0, token).ConfigureAwait(false);
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "An interrupted cancel records no version");
            MigrationScenarioRunner.AssertHistory(before, interrupted);
            DatabaseAssert.Equal("WaitingForInput", await ReadStatusAsync(bare.Id, token).ConfigureAwait(false), "An interrupted cancel leaves the stored status");
            DatabaseAssert.Equal("", await ReadReasonAsync(bare.Id, token).ConfigureAwait(false), "An interrupted cancel rolls back the reason it wrote");

            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Dictionary<int, string> committed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                DatabaseAssert.True(committed.ContainsKey(version), "The restarted run commits the cancel version");
                MigrationScenarioRunner.AssertHistory(before, committed);

                await AssertOutcomeAsync(driver, bare, prior, running, cancelled, stampsBefore, token).ConfigureAwait(false);

                await driver.InitializeAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(committed, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                await AssertOutcomeAsync(driver, bare, prior, running, cancelled, stampsBefore, token).ConfigureAwait(false);
            }

            Console.WriteLine("PASS mission input-wait cancel migration: waiting missions cancelled with their update time kept and reason recorded, other missions unchanged, interrupted run restarts");
        }

        private async Task AssertOutcomeAsync(DatabaseDriver driver, Mission bare, Mission prior, Mission running, Mission cancelled, Dictionary<string, string> stampsBefore, CancellationToken token)
        {
            Mission? bareAfter = await driver.Missions.ReadAsync(bare.Id, token).ConfigureAwait(false);
            DatabaseAssert.NotNull(bareAfter, "A waiting mission reads back");
            DatabaseAssert.Equal(MissionStatusEnum.Cancelled, bareAfter!.Status, "A waiting mission is cancelled");
            DatabaseAssert.Equal(MissionInputWaitCancelSchema.CancelReason, bareAfter.FailureReason, "A waiting mission with no reason records the cancel reason");
            DatabaseAssert.True(bareAfter.CompletedUtc.HasValue, "A waiting mission with no completion time takes its last update time");

            Mission? priorAfter = await driver.Missions.ReadAsync(prior.Id, token).ConfigureAwait(false);
            DatabaseAssert.NotNull(priorAfter, "A waiting mission with a reason reads back");
            DatabaseAssert.Equal(MissionStatusEnum.Cancelled, priorAfter!.Status, "A waiting mission with a reason is cancelled");
            DatabaseAssert.Equal(MissionInputWaitCancelSchema.CancelReason + "; previous reason: Judge verdict: FAIL", priorAfter.FailureReason, "The earlier reason follows the cancel reason");
            DatabaseAssert.Equal(prior.CompletedUtc, priorAfter.CompletedUtc, "An existing completion time is kept");

            Mission? runningAfter = await driver.Missions.ReadAsync(running.Id, token).ConfigureAwait(false);
            DatabaseAssert.Equal(MissionStatusEnum.InProgress, runningAfter!.Status, "A running mission is unchanged");
            DatabaseAssert.True(runningAfter.FailureReason == null, "A running mission gets no reason");

            Mission? cancelledAfter = await driver.Missions.ReadAsync(cancelled.Id, token).ConfigureAwait(false);
            DatabaseAssert.Equal(MissionStatusEnum.Cancelled, cancelledAfter!.Status, "A cancelled mission stays cancelled");
            DatabaseAssert.Equal("Operator cancelled", cancelledAfter.FailureReason, "A cancelled mission keeps its reason");

            Dictionary<string, string> stampsAfter = await ReadStampsAsync(token).ConfigureAwait(false);
            foreach (KeyValuePair<string, string> stamp in stampsBefore)
                DatabaseAssert.Equal(stamp.Value, stampsAfter[stamp.Key], "Last update time is unchanged for " + stamp.Key);
        }

        private static async Task<Mission> CreateMissionAsync(DatabaseDriver driver, string title, MissionStatusEnum status, string? reason, DateTime? completedUtc, CancellationToken token)
        {
            Mission mission = new Mission(title);
            mission.TenantId = Constants.DefaultTenantId;
            mission.Status = status;
            mission.FailureReason = reason;
            mission.CompletedUtc = completedUtc;
            return await driver.Missions.CreateAsync(mission, token).ConfigureAwait(false);
        }

        private async Task<Dictionary<string, string>> ReadStampsAsync(CancellationToken token)
        {
            Dictionary<string, string> stamps = new Dictionary<string, string>();
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, last_update_utc FROM missions;";
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            stamps[Convert.ToString(reader.GetValue(0))!] = Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)!;
                    }
                }
            }
            return stamps;
        }

        private async Task<string> ReadStatusAsync(string id, CancellationToken token)
        {
            return await ReadColumnAsync("status", id, token).ConfigureAwait(false);
        }

        private async Task<string> ReadReasonAsync(string id, CancellationToken token)
        {
            return await ReadColumnAsync("failure_reason", id, token).ConfigureAwait(false);
        }

        private async Task<string> ReadColumnAsync(string column, string id, CancellationToken token)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + column + " FROM missions WHERE id = '" + id + "';";
                    object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                    return value == null || value is DBNull ? "" : Convert.ToString(value)!;
                }
            }
        }

        private async Task StopAtAsync(int version, int ordinal, CancellationToken token)
        {
            using (DatabaseDriver driver = CreateDriver())
            {
                driver.MigrationCheckpoint = (current, statement) =>
                {
                    if (current == version && statement == ordinal) throw new StopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Mission input-wait cancel checkpoint was not reached"); }
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
