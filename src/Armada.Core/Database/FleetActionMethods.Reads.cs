namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Owner-scoped read operations for fleet action definitions.</summary>
    public sealed partial class FleetActionMethods
    {
        /// <inheritdoc />
        public async Task<FleetAction?> ReadAsync(string tenantId, string userId, string actionId, CancellationToken token = default)
        {
            FleetActionStorage.ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _Columns + " FROM fleet_actions WHERE tenant_id=@tenant_id AND user_id=@user_id AND id=@id;";
                    FleetActionStorage.Add(command, "@tenant_id", tenantId); FleetActionStorage.Add(command, "@user_id", userId); FleetActionStorage.Add(command, "@id", actionId);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) return await reader.ReadAsync(token).ConfigureAwait(false) ? FleetActionStorage.MapAction(reader) : null;
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<FleetAction>> EnumerateAsync(string tenantId, string userId, int limit = 100, CancellationToken token = default)
        {
            FleetActionStorage.ValidateOwner(tenantId, userId); int bounded = Math.Max(1, Math.Min(500, limit)); List<FleetAction> result = new List<FleetAction>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = _Provider == Armada.Core.Enums.DatabaseTypeEnum.SqlServer ? "SELECT TOP (@limit) " + _Columns + " FROM fleet_actions WHERE tenant_id=@tenant_id AND user_id=@user_id ORDER BY last_update_utc DESC,id DESC;" : "SELECT " + _Columns + " FROM fleet_actions WHERE tenant_id=@tenant_id AND user_id=@user_id ORDER BY last_update_utc DESC,id DESC LIMIT @limit;";
                    FleetActionStorage.Add(command, "@tenant_id", tenantId); FleetActionStorage.Add(command, "@user_id", userId); FleetActionStorage.Add(command, "@limit", bounded);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(FleetActionStorage.MapAction(reader));
                }
            }
            return result;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string tenantId, string userId, string actionId, CancellationToken token = default)
        {
            FleetActionStorage.ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM fleet_actions WHERE tenant_id=@tenant_id AND user_id=@user_id AND id=@id;";
                    FleetActionStorage.Add(command, "@tenant_id", tenantId); FleetActionStorage.Add(command, "@user_id", userId); FleetActionStorage.Add(command, "@id", actionId);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }
    }
}
