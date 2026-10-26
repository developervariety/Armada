namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Microsoft.Data.Sqlite;

    /// <summary>Provider-neutral persistence for durable fleet action runs.</summary>
    public sealed partial class FleetActionRunMethods : IFleetActionRunMethods
    {
        private const string _Columns = "id,tenant_id,user_id,action_id,request_id,request_fingerprint,state,revision,snapshot_json,state_json,created_utc,last_update_utc";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Create the run store.</summary>
        /// <param name="connectionFactory">Creates a closed provider connection.</param>
        /// <param name="provider">Database provider type.</param>
        public FleetActionRunMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        /// <inheritdoc />
        public async Task<FleetActionRun> CreateOrReadAsync(FleetActionRun run, CancellationToken token = default)
        {
            FleetActionStorage.ValidateRun(run);
            try
            {
                using (DbConnection connection = _ConnectionFactory())
                {
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "INSERT INTO fleet_action_runs (" + _Columns + ") VALUES (@id,@tenant_id,@user_id,@action_id,@request_id,@fingerprint,@state,@revision,@snapshot,@state_json,@created_utc,@last_update_utc);";
                        BindRun(command, run); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
                return run;
            }
            catch (DbException exception) when (IsUniqueViolation(exception))
            {
                FleetActionRun? existing = await ReadByRequestIdAsync(run.TenantId, run.UserId, run.RequestId, token).ConfigureAwait(false);
                if (existing != null) return existing;
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<FleetActionRun?> TryCompareExchangeStateAsync(string tenantId, string userId, string runId, long expectedRevision, FleetActionRunState nextState, CancellationToken token = default)
        {
            FleetActionStorage.ValidateOwner(tenantId, userId);
            if (nextState == null) throw new ArgumentNullException(nameof(nextState));
            string json = System.Text.Json.JsonSerializer.Serialize(nextState, FleetActionStorage.JsonOptions); FleetActionStorage.EnsureJsonSize(json);
            DateTime updatedUtc = DateTime.UtcNow;
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbTransaction transaction = await BeginTransactionAsync(connection, token).ConfigureAwait(false))
                {
                    FleetActionRunSnapshot? snapshot = await ReadSnapshotWithinTransactionAsync(connection, transaction, tenantId, userId, runId, expectedRevision, token).ConfigureAwait(false);
                    if (snapshot == null) { await transaction.RollbackAsync(token).ConfigureAwait(false); return null; }
                    FleetActionStorage.ValidateState(nextState, snapshot);
                    FleetActionRunState? currentState = await ReadStateWithinTransactionAsync(connection, transaction, tenantId, userId, runId, expectedRevision, token).ConfigureAwait(false);
                    if (currentState == null) { await transaction.RollbackAsync(token).ConfigureAwait(false); return null; }
                    FleetActionStorage.ValidateTransition(currentState, nextState);
                    int changed = await WriteStateWithinTransactionAsync(connection, transaction, tenantId, userId, runId, expectedRevision, nextState, json, updatedUtc, token).ConfigureAwait(false);
                    if (changed != 1) { await transaction.RollbackAsync(token).ConfigureAwait(false); return null; }
                    FleetActionRun? updated = await ReadWithinTransactionAsync(connection, transaction, tenantId, userId, runId, token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return updated;
                }
            }
        }

        private void BindRun(DbCommand command, FleetActionRun run)
        {
            FleetActionStorage.Add(command, "@id", run.Id); FleetActionStorage.Add(command, "@tenant_id", run.TenantId); FleetActionStorage.Add(command, "@user_id", run.UserId); FleetActionStorage.Add(command, "@action_id", run.ActionId); FleetActionStorage.Add(command, "@request_id", run.RequestId); FleetActionStorage.Add(command, "@fingerprint", run.RequestFingerprint); FleetActionStorage.Add(command, "@state", run.State.Status.ToString()); FleetActionStorage.Add(command, "@revision", Math.Max(0, run.Revision)); FleetActionStorage.Add(command, "@snapshot", System.Text.Json.JsonSerializer.Serialize(run.Snapshot, FleetActionStorage.JsonOptions)); FleetActionStorage.Add(command, "@state_json", System.Text.Json.JsonSerializer.Serialize(run.State, FleetActionStorage.JsonOptions)); FleetActionStorage.AddTime(command, _Provider, "fleet_action_runs", "created_utc", run.CreatedUtc); FleetActionStorage.AddTime(command, _Provider, "fleet_action_runs", "last_update_utc", run.LastUpdateUtc);
        }

        private async Task<DbTransaction> BeginTransactionAsync(DbConnection connection, CancellationToken token)
        {
            if (_Provider == DatabaseTypeEnum.Sqlite && connection is SqliteConnection sqlite) return sqlite.BeginTransaction(deferred: false);
            return await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        }

        private bool IsUniqueViolation(DbException exception)
        {
            if (_Provider == DatabaseTypeEnum.Postgresql && exception is Npgsql.PostgresException postgres) return postgres.SqlState == "23505";
            if (_Provider == DatabaseTypeEnum.Mysql && exception is MySqlConnector.MySqlException mysql) return mysql.Number == 1062;
            if (_Provider == DatabaseTypeEnum.SqlServer && exception is Microsoft.Data.SqlClient.SqlException sqlServer) return sqlServer.Number == 2601 || sqlServer.Number == 2627;
            if (_Provider == DatabaseTypeEnum.Sqlite && exception is Microsoft.Data.Sqlite.SqliteException sqlite) return sqlite.SqliteErrorCode == 19 && (sqlite.SqliteExtendedErrorCode == 1555 || sqlite.SqliteExtendedErrorCode == 2067);
            return false;
        }
    }
}
