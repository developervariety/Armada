namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Provider-neutral implementation of battery run records.
    /// </summary>
    public sealed class BatteryRunMethods : IBatteryRunMethods
    {
        private const string _Table = "battery_runs";
        private const string _Columns = "id, tenant_id, user_id, captain_id, captain_name, pack_id, pack_version, status, error, started_utc, completed_utc, results_json";
        private const int _MaximumLimit = 200;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public BatteryRunMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        /// <inheritdoc />
        public async Task<BatteryRun> CreateAsync(BatteryRun run, CancellationToken token = default)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (String.IsNullOrWhiteSpace(run.PackId)) throw new ArgumentException("Pack identifier is required.", nameof(run));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "INSERT INTO " + _Table + " (" + _Columns + ") VALUES (@id, @tenant_id, @user_id, @captain_id, @captain_name, @pack_id, @pack_version, @status, @error, @started_utc, @completed_utc, @results_json);";
                        ProductionFactSql.Add(command, "@id", run.Id);
                        ProductionFactSql.Add(command, "@tenant_id", run.TenantId);
                        ProductionFactSql.Add(command, "@user_id", run.UserId);
                        ProductionFactSql.Add(command, "@captain_id", run.CaptainId);
                        ProductionFactSql.Add(command, "@captain_name", run.CaptainName);
                        ProductionFactSql.Add(command, "@pack_id", run.PackId);
                        ProductionFactSql.AddInt32(command, "@pack_version", run.PackVersion);
                        ProductionFactSql.Add(command, "@status", run.Status.ToString());
                        ProductionFactSql.Add(command, "@error", run.Error);
                        ProductionFactSql.AddUtc(command, "@started_utc", run.StartedUtc, _Provider);
                        ProductionFactSql.AddUtc(command, "@completed_utc", run.CompletedUtc, _Provider);
                        ProductionFactSql.Add(command, "@results_json", JsonSerializer.Serialize(run.Results, _Json));
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
            return run;
        }

        /// <inheritdoc />
        public async Task<BatteryRun> UpdateAsync(BatteryRun run, CancellationToken token = default)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "UPDATE " + _Table + " SET status = @status, error = @error, completed_utc = @completed_utc, results_json = @results_json WHERE id = @id;";
                        ProductionFactSql.Add(command, "@id", run.Id);
                        ProductionFactSql.Add(command, "@status", run.Status.ToString());
                        ProductionFactSql.Add(command, "@error", run.Error);
                        ProductionFactSql.AddUtc(command, "@completed_utc", run.CompletedUtc, _Provider);
                        ProductionFactSql.Add(command, "@results_json", JsonSerializer.Serialize(run.Results, _Json));
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
            return run;
        }

        /// <inheritdoc />
        public async Task<BatteryRun?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentException("Run identifier is required.", nameof(id));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _Columns + " FROM " + _Table + " WHERE id = @id;";
                    ProductionFactSql.Add(command, "@id", id);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return FromReader(reader);
                    }
                }
            }
            return null;
        }

        /// <inheritdoc />
        public async Task<List<BatteryRun>> EnumerateAsync(string? captainId, string? packId, int limit = 50, CancellationToken token = default)
        {
            int boundedLimit = Math.Max(1, Math.Min(_MaximumLimit, limit));
            List<BatteryRun> runs = new List<BatteryRun>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    List<string> filters = new List<string>();
                    if (!String.IsNullOrWhiteSpace(captainId)) { filters.Add("captain_id = @captain_id"); ProductionFactSql.Add(command, "@captain_id", captainId); }
                    if (!String.IsNullOrWhiteSpace(packId)) { filters.Add("pack_id = @pack_id"); ProductionFactSql.Add(command, "@pack_id", packId); }
                    string where = filters.Count == 0 ? String.Empty : " WHERE " + String.Join(" AND ", filters);
                    string order = " ORDER BY started_utc DESC, id DESC";
                    command.CommandText = _Provider == DatabaseTypeEnum.SqlServer
                        ? "SELECT TOP (@row_limit) " + _Columns + " FROM " + _Table + where + order + ";"
                        : "SELECT " + _Columns + " FROM " + _Table + where + order + " LIMIT @row_limit;";
                    ProductionFactSql.AddInt32(command, "@row_limit", boundedLimit);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) runs.Add(FromReader(reader));
                    }
                }
            }
            return runs;
        }

        /// <inheritdoc />
        public async Task<int> InterruptRunningAsync(string reason, DateTime endedUtc, CancellationToken token = default)
        {
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "UPDATE " + _Table + " SET status = @status, error = @error, completed_utc = @completed_utc WHERE status = @running;";
                        ProductionFactSql.Add(command, "@status", BatteryRunStatusEnum.Interrupted.ToString());
                        ProductionFactSql.Add(command, "@error", reason);
                        ProductionFactSql.AddUtc(command, "@completed_utc", endedUtc, _Provider);
                        ProductionFactSql.Add(command, "@running", BatteryRunStatusEnum.Running.ToString());
                        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }

        private static BatteryRun FromReader(DbDataReader reader)
        {
            string status = Convert.ToString(reader["status"])!;
            if (!Enum.TryParse(status, false, out BatteryRunStatusEnum parsed))
                throw new InvalidOperationException("Stored battery run has an unknown status: " + status);
            string resultsJson = Convert.ToString(reader["results_json"])!;
            return new BatteryRun
            {
                Id = Convert.ToString(reader["id"])!,
                TenantId = ProductionFactSql.ReadText(reader["tenant_id"]),
                UserId = ProductionFactSql.ReadText(reader["user_id"]),
                CaptainId = ProductionFactSql.ReadText(reader["captain_id"]),
                CaptainName = ProductionFactSql.ReadText(reader["captain_name"]),
                PackId = Convert.ToString(reader["pack_id"])!,
                PackVersion = Convert.ToInt32(reader["pack_version"]),
                Status = parsed,
                Error = ProductionFactSql.ReadText(reader["error"]),
                StartedUtc = ProductionFactSql.ReadUtc(reader["started_utc"]),
                CompletedUtc = ProductionFactSql.ReadNullableUtc(reader["completed_utc"]),
                Results = JsonSerializer.Deserialize<List<BatteryTaskResult>>(resultsJson, _Json) ?? new List<BatteryTaskResult>()
            };
        }
    }
}
