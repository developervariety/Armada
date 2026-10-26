namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class MissionAttemptFactMethods
    {
        /// <inheritdoc />
        public async Task<ProductionFactPage<MissionAttemptFact>> EnumerateByMissionAsync(string tenantId, string missionId, int limit = 1000, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant identifier is required.", nameof(tenantId));
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentException("Mission identifier is required.", nameof(missionId));
            int boundedLimit = Math.Max(1, Math.Min(1000, limit));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    string order = " ORDER BY created_utc DESC, id DESC";
                    command.CommandText = _Provider == Armada.Core.Enums.DatabaseTypeEnum.SqlServer
                        ? "SELECT TOP (@row_limit) " + _Columns + " FROM mission_attempt_facts WHERE tenant_id=@tenant_id AND mission_id=@mission_id" + order + ";"
                        : "SELECT " + _Columns + " FROM mission_attempt_facts WHERE tenant_id=@tenant_id AND mission_id=@mission_id" + order + " LIMIT @row_limit;";
                    ProductionFactSql.Add(command, "@tenant_id", tenantId);
                    ProductionFactSql.Add(command, "@mission_id", missionId);
                    ProductionFactSql.Add(command, "@row_limit", boundedLimit + 1);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        return await ReadMissionWindowAsync(reader, boundedLimit, token).ConfigureAwait(false);
                    }
                }
            }
        }

        private static async Task<ProductionFactPage<MissionAttemptFact>> ReadMissionWindowAsync(DbDataReader reader, int limit, CancellationToken token)
        {
            ProductionFactPage<MissionAttemptFact> page = new ProductionFactPage<MissionAttemptFact>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (page.Items.Count >= limit) { page.Truncated = true; break; }
                page.Items.Add(FromReader(reader));
            }
            page.Items.Reverse();
            return page;
        }
    }
}
