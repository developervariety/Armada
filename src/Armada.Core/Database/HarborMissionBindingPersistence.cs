namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>Atomic assignment fence applied before a Harbor launch leaves the Admiral.</summary>
    internal static class HarborMissionBindingPersistence
    {
        internal static async Task<bool> TryBindAsync(DbConnection connection, DatabaseTypeEnum provider,
            HarborMissionBindingUpdate update, CancellationToken token)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            if (String.IsNullOrWhiteSpace(update.MissionId) || String.IsNullOrWhiteSpace(update.CaptainId)
                || String.IsNullOrWhiteSpace(update.DockId) || String.IsNullOrWhiteSpace(update.JobId)
                || update.AdmiralProcessId <= 0 || update.ProcessStartedUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("A Harbor launch requires a complete UTC attempt identity.", nameof(update));
            await connection.OpenAsync(token).ConfigureAwait(false);
            using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE missions SET harbor_job_id = @job_id, process_id = @process_id, process_started_utc = @started, last_update_utc = @now"
                        + " WHERE id = @mission_id AND status = 'Assigned' AND captain_id = @captain_id AND dock_id = @dock_id"
                        + " AND " + Equal("tenant_id", "@tenant_id", provider) + " AND " + Equal("user_id", "@user_id", provider)
                        + " AND " + Equal("harbor_job_id", "@expected_job_id", provider)
                        + " AND (process_id IS NULL OR process_id = @process_id)"
                        + " AND EXISTS (SELECT 1 FROM harbor_jobs WHERE job_id = @job_id AND mission_id = @mission_id"
                        + " AND captain_id = @captain_id AND dock_id = @dock_id AND state = 'Pending'"
                        + " AND " + Equal("tenant_id", "@tenant_id", provider) + " AND " + Equal("user_id", "@user_id", provider)
                        + " AND admiral_process_id = @process_id AND admiral_process_started_utc = @job_started);";
                    Bind(command, provider, "missions", update);
                    ProductionFactSql.AddUtc(command, "@job_started", update.ProcessStartedUtc, provider);
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        return false;
                    }
                }
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE captains SET process_id = @process_id, process_started_utc = @started, last_update_utc = @now"
                        + " WHERE id = @captain_id AND state = 'Working' AND current_mission_id = @mission_id AND current_dock_id = @dock_id"
                        + " AND " + Equal("tenant_id", "@tenant_id", provider) + " AND " + Equal("user_id", "@user_id", provider)
                        + " AND (process_id IS NULL OR process_id = @process_id);";
                    Bind(command, provider, "captains", update);
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        return false;
                    }
                }
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            }
        }

        private static void Bind(DbCommand command, DatabaseTypeEnum provider, string table, HarborMissionBindingUpdate update)
        {
            StoredValueBinder.For(provider).For(command, table)
                .Text("@mission_id", "id", update.MissionId)
                .Text("@captain_id", "captain_id", update.CaptainId)
                .Text("@dock_id", "dock_id", update.DockId)
                .Text("@job_id", "harbor_job_id", update.JobId)
                .Text("@expected_job_id", "harbor_job_id", update.ExpectedJobId)
                .Text("tenant_id", update.TenantId).Text("user_id", update.UserId)
                .Int("@process_id", "process_id", update.AdmiralProcessId)
                .Utc("@started", "process_started_utc", update.ProcessStartedUtc)
                .Utc("@now", "last_update_utc", DateTime.UtcNow);
        }

        private static string Equal(string column, string parameter, DatabaseTypeEnum provider)
        {
            if (provider == DatabaseTypeEnum.Postgresql) parameter = "CAST(" + parameter + " AS text)";
            string comparison = provider switch
            {
                DatabaseTypeEnum.Sqlite => "CAST(" + column + " AS BLOB) = CAST(" + parameter + " AS BLOB)",
                DatabaseTypeEnum.Postgresql => "convert_to(" + column + ", 'UTF8') = convert_to(" + parameter + ", 'UTF8')",
                DatabaseTypeEnum.Mysql => "BINARY " + column + " = BINARY " + parameter,
                DatabaseTypeEnum.SqlServer => "CONVERT(varbinary(max), " + column + ") = CONVERT(varbinary(max), " + parameter + ")",
                _ => throw new NotSupportedException()
            };
            return "((" + column + " IS NULL AND " + parameter + " IS NULL) OR " + comparison + ")";
        }
    }
}
