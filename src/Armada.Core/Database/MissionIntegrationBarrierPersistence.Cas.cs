namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal static partial class MissionIntegrationBarrierPersistence
    {
        internal static async Task<bool> TryUpdateAsync(DbConnection connection, DatabaseTypeEnum provider,
            Mission expected, MissionIntegrationBarrier next, CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (next == null) throw new ArgumentNullException(nameof(next));
            bool initialize = !expected.IntegrationBarrierRawJsonPresent && expected.IntegrationBarrierRawJson == null
                && !expected.IntegrationBarrierInvalid && expected.IntegrationBarrier == null
                && expected.IntegrationBarrierWriteRevision == 0;
            if (expected.IntegrationBarrierInvalid || expected.IntegrationBarrierWriteStamp == null
                || (!initialize && !expected.IntegrationBarrierRawJsonPresent))
                throw new InvalidOperationException("Integration barrier updates require a valid loaded barrier snapshot.");
            if (expected.Status != MissionStatusEnum.Pending || expected.ProcessId.HasValue || expected.StartedUtc.HasValue)
                return false;

            MissionIntegrationBarrier? current = initialize ? null : Deserialize(expected.IntegrationBarrierRawJson!);
            if (!initialize && (current == null || !IsValid(current, expected))) return false;
            ValidateDocument(next, expected);
            if (initialize)
            {
                if (next.Phase != MissionIntegrationBarrierPhaseEnum.Preparing || next.Members.Count != 0)
                    throw new InvalidOperationException("An absent barrier can only be initialized in the preparing phase.");
            }
            else
            {
                ValidateTransition(current!, next);
            }

            string nextJson = Serialize(next, expected);
            string? previousJson = expected.IntegrationBarrierRawJson;
            bool noChange = !initialize && current!.Phase == MissionIntegrationBarrierPhaseEnum.Accepted
                && next.Phase == MissionIntegrationBarrierPhaseEnum.Accepted
                && String.Equals(previousJson, nextJson, StringComparison.Ordinal);
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE missions SET integration_barrier_json=@next, integration_barrier_revision=integration_barrier_revision+@increment WHERE id=@id"
                    + " AND status=@status AND process_id IS NULL AND started_utc IS NULL AND integration_barrier_revision=@revision"
                    + " AND " + EqualsSql("integration_barrier_json", "@previous", provider)
                    + " AND last_update_utc=@stamp"
                    + " AND " + EqualsSql("tenant_id", "@tenant_id", provider)
                    + " AND " + EqualsSql("user_id", "@user_id", provider)
                    + " AND " + EqualsSql("voyage_id", "@voyage_id", provider)
                    + " AND " + EqualsSql("vessel_id", "@vessel_id", provider) + ";";
                Add(command, "@next", nextJson, DbType.String);
                Add(command, "@increment", noChange ? 0 : 1, DbType.Int32);
                Add(command, "@previous", previousJson, DbType.String);
                Add(command, "@id", expected.Id, DbType.String);
                Add(command, "@status", MissionStatusEnum.Pending.ToString(), DbType.String);
                Add(command, "@revision", expected.IntegrationBarrierWriteRevision, DbType.Int64);
                Add(command, "@stamp", expected.IntegrationBarrierWriteStamp);
                Add(command, "@tenant_id", expected.TenantId, DbType.String);
                Add(command, "@user_id", expected.UserId, DbType.String);
                Add(command, "@voyage_id", expected.VoyageId, DbType.String);
                Add(command, "@vessel_id", expected.VesselId, DbType.String);
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = provider == DatabaseTypeEnum.Sqlite
                    ? await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false)
                    : null)
                {
                    bool written = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                    if (!written) return false;
                    expected.IntegrationBarrier = next;
                    expected.IntegrationBarrierRawJson = nextJson;
                    expected.IntegrationBarrierRawJsonPresent = true;
                    expected.IntegrationBarrierInvalid = false;
                    if (!noChange) expected.IntegrationBarrierWriteRevision++;
                    return true;
                }
            }
        }

        private static string EqualsSql(string column, string parameter, DatabaseTypeEnum provider)
        {
            if (provider == DatabaseTypeEnum.Postgresql) parameter = "CAST(" + parameter + " AS text)";
            string comparison = provider switch
            {
                DatabaseTypeEnum.Sqlite => "CAST(" + column + " AS BLOB)=CAST(" + parameter + " AS BLOB)",
                DatabaseTypeEnum.Postgresql => "convert_to(" + column + ", 'UTF8')=convert_to(" + parameter + ", 'UTF8')",
                DatabaseTypeEnum.Mysql => "BINARY " + column + "=BINARY " + parameter,
                DatabaseTypeEnum.SqlServer => "CONVERT(varbinary(max)," + column + ")=CONVERT(varbinary(max)," + parameter + ")",
                _ => throw new NotSupportedException()
            };
            return "((" + column + " IS NULL AND " + parameter + " IS NULL) OR " + comparison + ")";
        }

        private static void Add(DbCommand command, string name, object? value, DbType? type = null)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            if (type.HasValue) parameter.DbType = type.Value;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
