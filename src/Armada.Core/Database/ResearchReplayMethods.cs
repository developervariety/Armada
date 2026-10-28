namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Microsoft.Data.Sqlite;

    /// <summary>Provider-shared atomic Research replay with append-only prior-attempt snapshots.</summary>
    public sealed partial class ResearchReplayMethods : IResearchReplayMethods
    {
        private readonly StoredDialect _Sql;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions();
        private const int MaximumSnapshotBytes = 16 * 1024 * 1024;

        internal ResearchReplayMethods(StoredDialect sql) { _Sql = sql; }

        /// <inheritdoc />
        public async Task<ResearchReplayPlan?> ReadReceiptAsync(string tenantId, string userId, string voyageId,
            ResearchReplayRequest request, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(request.RequestId)) return null;
            using (DbConnection connection = await _Sql.OpenAsync(token).ConfigureAwait(false))
            {
                using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    return await ReadCommittedRequestAsync(connection, transaction, tenantId, userId, voyageId, request, token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<ResearchReplayPlan> ExecuteAsync(string tenantId, string userId, string voyageId,
            ResearchReplayRequest request, CancellationToken token = default)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return await ExecuteOnceAsync(tenantId, userId, voyageId, request, token).ConfigureAwait(false); }
                catch (DbException ex) when (IsSerializationConflict(ex))
                {
                    if (attempt >= 2) throw new InvalidOperationException("Research replay conflicted with another writer; retry the same request ID.", ex);
                    await Task.Delay(25 * (attempt + 1), token).ConfigureAwait(false);
                }
            }
        }

        private static bool IsSerializationConflict(DbException exception)
        {
            if (exception is Npgsql.PostgresException postgres) return postgres.SqlState == "40001" || postgres.SqlState == "40P01";
            if (exception is MySqlConnector.MySqlException mysql) return mysql.Number == 1213 || mysql.Number == 1205;
            if (exception is Microsoft.Data.SqlClient.SqlException sqlServer) return sqlServer.Number == 1205 || sqlServer.Number == 3960;
            if (exception is SqliteException sqlite) return sqlite.SqliteErrorCode == 5 || sqlite.SqliteErrorCode == 6;
            return false;
        }

        private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
        { DbCommand command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
        private static void Add(DbCommand command, string name, string? value) => StoredValueBinder.Value(command, name, value);
        private void Time(DbCommand command, string parameter, string table, string column, DateTime? value)
            => _Sql.Binder.AddTimestamp(command, parameter, table, column, value);

        internal sealed class Snapshot
        {
            public int Version { get; set; } = 1;
            public Voyage Voyage { get; set; } = new Voyage();
            public List<Mission> Missions { get; set; } = new List<Mission>();
            public string? CurrentConfigurationDigest { get; set; }
            public List<ResearchReplayInstruction> Instructions { get; set; } = new List<ResearchReplayInstruction>();
            public SortedDictionary<string, List<SortedDictionary<string, object?>>> Rows { get; set; }
                = new SortedDictionary<string, List<SortedDictionary<string, object?>>>(StringComparer.Ordinal);
        }
    }
}
