namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Revision-locked reads used by run state compare-and-set operations.</summary>
    public sealed partial class FleetActionRunMethods
    {
        private static Task<FleetActionRunSnapshot?> ReadSnapshotWithinTransactionAsync(DbConnection connection, DbTransaction transaction, string tenantId, string userId, string runId, long revision, CancellationToken token)
        {
            return ReadJsonWithinTransactionAsync<FleetActionRunSnapshot>(connection, transaction, tenantId, userId, runId, revision, "snapshot_json", token);
        }

        private static Task<FleetActionRunState?> ReadStateWithinTransactionAsync(DbConnection connection, DbTransaction transaction, string tenantId, string userId, string runId, long revision, CancellationToken token)
        {
            return ReadJsonWithinTransactionAsync<FleetActionRunState>(connection, transaction, tenantId, userId, runId, revision, "state_json", token);
        }

        private async Task<int> WriteStateWithinTransactionAsync(DbConnection connection, DbTransaction transaction, string tenantId, string userId, string runId, long revision, FleetActionRunState state, string json, DateTime updatedUtc, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "UPDATE fleet_action_runs SET state=@state,state_json=@state_json,revision=revision+1,started_utc=@started_utc,completed_utc=@completed_utc,last_update_utc=@last_update_utc WHERE tenant_id=@tenant_id AND user_id=@user_id AND id=@id AND revision=@revision;";
                FleetActionStorage.Add(command, "@state", state.Status.ToString()); FleetActionStorage.Add(command, "@state_json", json); FleetActionStorage.Add(command, "@revision", revision); FleetActionStorage.Add(command, "@tenant_id", tenantId); FleetActionStorage.Add(command, "@user_id", userId); FleetActionStorage.Add(command, "@id", runId);
                FleetActionStorage.AddTime(command, _Provider, "fleet_action_runs", "started_utc", state.StartedUtc); FleetActionStorage.AddTime(command, _Provider, "fleet_action_runs", "completed_utc", state.CompletedUtc); FleetActionStorage.AddTime(command, _Provider, "fleet_action_runs", "last_update_utc", updatedUtc);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private static async Task<T?> ReadJsonWithinTransactionAsync<T>(DbConnection connection, DbTransaction transaction, string tenantId, string userId, string runId, long revision, string column, CancellationToken token) where T : class
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "SELECT " + column + " FROM fleet_action_runs WHERE tenant_id=@tenant_id AND user_id=@user_id AND id=@id AND revision=@revision;";
                FleetActionStorage.Add(command, "@tenant_id", tenantId); FleetActionStorage.Add(command, "@user_id", userId); FleetActionStorage.Add(command, "@id", runId); FleetActionStorage.Add(command, "@revision", revision);
                object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (value == null || value == DBNull.Value) return null;
                return JsonSerializer.Deserialize<T>(Convert.ToString(value)!, FleetActionStorage.JsonOptions);
            }
        }
    }
}
