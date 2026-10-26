namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Owner-scoped and worker read operations for fleet action runs.</summary>
    public sealed partial class FleetActionRunMethods
    {
        /// <inheritdoc />
        public Task<FleetActionRun?> ReadByRequestIdAsync(string tenantId, string userId, string requestId, CancellationToken token = default)
        {
            FleetActionStorage.ValidateOwner(tenantId, userId);
            return SelectOneAsync("tenant_id=@tenant_id AND user_id=@user_id AND request_id=@request_id", command => { FleetActionStorage.Add(command, "@tenant_id", tenantId); FleetActionStorage.Add(command, "@user_id", userId); FleetActionStorage.Add(command, "@request_id", requestId); }, token);
        }

        /// <inheritdoc />
        public Task<FleetActionRun?> ReadAsync(string tenantId, string userId, string runId, CancellationToken token = default)
        {
            FleetActionStorage.ValidateOwner(tenantId, userId);
            return SelectOneAsync("tenant_id=@tenant_id AND user_id=@user_id AND id=@id", command => { FleetActionStorage.Add(command, "@tenant_id", tenantId); FleetActionStorage.Add(command, "@user_id", userId); FleetActionStorage.Add(command, "@id", runId); }, token);
        }

        /// <inheritdoc />
        public Task<List<FleetActionRun>> EnumerateAsync(string tenantId, string userId, int limit = 100, CancellationToken token = default)
        {
            FleetActionStorage.ValidateOwner(tenantId, userId);
            return SelectListAsync("tenant_id=@tenant_id AND user_id=@user_id", command => { FleetActionStorage.Add(command, "@tenant_id", tenantId); FleetActionStorage.Add(command, "@user_id", userId); }, Math.Max(1, Math.Min(500, limit)), false, token);
        }

        /// <inheritdoc />
        public Task<List<FleetActionRun>> EnumerateActiveAsync(int limit = 100, string? afterId = null, CancellationToken token = default)
        {
            if (afterId != null && (String.IsNullOrWhiteSpace(afterId) || afterId.Length > 128)) throw new ArgumentException("Active run cursor is invalid or too long.", nameof(afterId));
            string where = "state IN ('Pending','Running')";
            if (afterId != null) where += " AND id>@after_id";
            return SelectListAsync(where, command => { if (afterId != null) FleetActionStorage.Add(command, "@after_id", afterId); }, Math.Max(1, Math.Min(500, limit)), true, token);
        }

        private async Task<FleetActionRun?> SelectOneAsync(string where, Action<DbCommand> bind, CancellationToken token)
        {
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _Columns + " FROM fleet_action_runs WHERE " + where + ";"; bind(command);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) return await reader.ReadAsync(token).ConfigureAwait(false) ? FleetActionStorage.MapRun(reader) : null;
                }
            }
        }

        private async Task<List<FleetActionRun>> SelectListAsync(string where, Action<DbCommand> bind, int limit, bool active, CancellationToken token)
        {
            List<FleetActionRun> result = new List<FleetActionRun>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    string prefix = _Provider == DatabaseTypeEnum.SqlServer ? "SELECT TOP (@limit) " : "SELECT ";
                    string suffix = _Provider == DatabaseTypeEnum.SqlServer ? String.Empty : " LIMIT @limit";
                    string order = active ? "id ASC" : "created_utc DESC,id DESC";
                    command.CommandText = prefix + _Columns + " FROM fleet_action_runs WHERE " + where + " ORDER BY " + order + suffix + ";";
                    bind(command); FleetActionStorage.Add(command, "@limit", limit);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(FleetActionStorage.MapRun(reader));
                }
            }
            return result;
        }

        private async Task<FleetActionRun?> ReadWithinTransactionAsync(DbConnection connection, DbTransaction transaction, string tenantId, string userId, string runId, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "SELECT " + _Columns + " FROM fleet_action_runs WHERE tenant_id=@tenant_id AND user_id=@user_id AND id=@id;";
                FleetActionStorage.Add(command, "@tenant_id", tenantId); FleetActionStorage.Add(command, "@user_id", userId); FleetActionStorage.Add(command, "@id", runId);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) return await reader.ReadAsync(token).ConfigureAwait(false) ? FleetActionStorage.MapRun(reader) : null;
            }
        }
    }
}
