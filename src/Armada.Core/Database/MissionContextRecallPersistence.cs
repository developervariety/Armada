namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;

    /// <summary>One transactional recall counter implementation for every database provider.</summary>
    internal static class MissionContextRecallPersistence
    {
        private static readonly string[] _Terminal = Enum.GetValues<MissionStatusEnum>()
            .Where(MissionStateMachine.IsTerminal).Select(status => status.ToString()).ToArray();

        internal static async Task<int?> TryConsumeAsync(DbConnection connection, DatabaseTypeEnum provider,
            string tenantId, string userId, string missionId, int maximum, CancellationToken token)
        {
            if (maximum <= 0) return null;
            await connection.OpenAsync(token).ConfigureAwait(false);
            using (IDisposable? lease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                using (DbCommand update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE missions SET context_recall_calls = context_recall_calls + 1 "
                        + "WHERE id = @id AND COALESCE(NULLIF(TRIM(tenant_id), ''), 'default') = @tenant_id "
                        + "AND COALESCE(NULLIF(TRIM(user_id), ''), 'default') = @user_id AND context_recall_calls < @maximum "
                        + "AND status NOT IN (" + String.Join(",", _Terminal.Select((status, index) => "@terminal" + index)) + ");";
                    StoredParameters parameters = new StoredParameters(StoredValueBinder.For(provider), update, "missions");
                    parameters
                        .Text("id", missionId).Text("tenant_id", tenantId).Text("user_id", userId).Int("maximum", maximum);
                    for (int index = 0; index < _Terminal.Length; index++) parameters.Text("terminal" + index, _Terminal[index]);
                    if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        return null;
                    }
                }
                int used;
                using (DbCommand read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT context_recall_calls FROM missions WHERE id = @id;";
                    new StoredParameters(StoredValueBinder.For(provider), read, "missions").Text("id", missionId);
                    used = Convert.ToInt32(await read.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return used;
            }
        }
    }
}
