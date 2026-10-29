namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Provider-neutral implementation of per-attempt provenance records.
    /// </summary>
    public sealed class MissionAttemptProvenanceMethods : IMissionAttemptProvenanceMethods
    {
        private const string _Table = "mission_attempt_provenance";
        private const string _Columns = "id, tenant_id, user_id, mission_id, voyage_id, vessel_id, captain_id, persona, runtime, model, runtime_version, process_id, started_utc, ended_utc, outcome, exit_code, reason_code";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public MissionAttemptProvenanceMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        /// <inheritdoc />
        public async Task<MissionAttemptProvenance> CreateAsync(MissionAttemptProvenance record, CancellationToken token = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (String.IsNullOrWhiteSpace(record.MissionId)) throw new ArgumentException("Mission identifier is required.", nameof(record));
            record.ReasonCode = ProductionFactSql.BoundCode(record.ReasonCode, MissionAttemptProvenance.MaximumReasonCodeLength);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "INSERT INTO " + _Table + " (" + _Columns + ") VALUES (@id, @tenant_id, @user_id, @mission_id, @voyage_id, @vessel_id, @captain_id, @persona, @runtime, @model, @runtime_version, @process_id, @started_utc, @ended_utc, @outcome, @exit_code, @reason_code);";
                        ProductionFactSql.Add(command, "@id", record.Id);
                        ProductionFactSql.Add(command, "@tenant_id", record.TenantId);
                        ProductionFactSql.Add(command, "@user_id", record.UserId);
                        ProductionFactSql.Add(command, "@mission_id", record.MissionId);
                        ProductionFactSql.Add(command, "@voyage_id", record.VoyageId);
                        ProductionFactSql.Add(command, "@vessel_id", record.VesselId);
                        ProductionFactSql.Add(command, "@captain_id", record.CaptainId);
                        ProductionFactSql.Add(command, "@persona", Bound(record.Persona, 128));
                        ProductionFactSql.Add(command, "@runtime", Bound(record.Runtime, 64));
                        ProductionFactSql.Add(command, "@model", Bound(record.Model, 256));
                        ProductionFactSql.Add(command, "@runtime_version", Bound(record.RuntimeVersion, 128));
                        ProductionFactSql.AddInt32(command, "@process_id", record.ProcessId);
                        ProductionFactSql.AddUtc(command, "@started_utc", record.StartedUtc, _Provider);
                        ProductionFactSql.AddUtc(command, "@ended_utc", record.EndedUtc, _Provider);
                        ProductionFactSql.Add(command, "@outcome", record.Outcome.ToString());
                        ProductionFactSql.AddInt32(command, "@exit_code", record.ExitCode);
                        ProductionFactSql.Add(command, "@reason_code", record.ReasonCode);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
            return record;
        }

        /// <inheritdoc />
        public async Task<MissionAttemptProvenance?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentException("Record identifier is required.", nameof(id));
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
        public Task<int> CloseAsync(string missionId, int processId, MissionAttemptOutcomeEnum outcome, int? exitCode, string? reasonCode, DateTime endedUtc, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentException("Mission identifier is required.", nameof(missionId));
            if (outcome == MissionAttemptOutcomeEnum.Running) throw new ArgumentException("A closed attempt has an end outcome.", nameof(outcome));
            return CloseRunningAsync(missionId, processId, outcome, exitCode, reasonCode, endedUtc, token);
        }

        /// <inheritdoc />
        public Task<int> InterruptRunningAsync(string missionId, string reasonCode, DateTime endedUtc, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentException("Mission identifier is required.", nameof(missionId));
            return CloseRunningAsync(missionId, null, MissionAttemptOutcomeEnum.Interrupted, null, reasonCode, endedUtc, token);
        }

        /// <inheritdoc />
        public async Task<ProductionFactPage<MissionAttemptProvenance>> EnumerateByMissionAsync(string? tenantId, string missionId, int limit = 100, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentException("Mission identifier is required.", nameof(missionId));
            int boundedLimit = Math.Max(1, Math.Min(1000, limit));
            ProductionFactPage<MissionAttemptProvenance> page = new ProductionFactPage<MissionAttemptProvenance>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    string where = " WHERE mission_id = @mission_id AND " + (tenantId == null ? "tenant_id IS NULL" : "tenant_id = @tenant_id");
                    string order = " ORDER BY started_utc DESC, id DESC";
                    command.CommandText = _Provider == DatabaseTypeEnum.SqlServer
                        ? "SELECT TOP (@row_limit) " + _Columns + " FROM " + _Table + where + order + ";"
                        : "SELECT " + _Columns + " FROM " + _Table + where + order + " LIMIT @row_limit;";
                    ProductionFactSql.Add(command, "@mission_id", missionId);
                    if (tenantId != null) ProductionFactSql.Add(command, "@tenant_id", tenantId);
                    ProductionFactSql.AddInt32(command, "@row_limit", boundedLimit + 1);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            if (page.Items.Count >= boundedLimit) { page.Truncated = true; break; }
                            page.Items.Add(FromReader(reader));
                        }
                    }
                }
            }
            page.Items.Reverse();
            return page;
        }

        private async Task<int> CloseRunningAsync(string missionId, int? processId, MissionAttemptOutcomeEnum outcome, int? exitCode, string? reasonCode, DateTime endedUtc, CancellationToken token)
        {
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "UPDATE " + _Table + " SET ended_utc = @ended_utc, outcome = @outcome, exit_code = @exit_code, reason_code = @reason_code"
                            + " WHERE mission_id = @mission_id AND outcome = @running" + (processId.HasValue ? " AND process_id = @process_id" : String.Empty) + ";";
                        ProductionFactSql.AddUtc(command, "@ended_utc", endedUtc, _Provider);
                        ProductionFactSql.Add(command, "@outcome", outcome.ToString());
                        ProductionFactSql.AddInt32(command, "@exit_code", exitCode);
                        ProductionFactSql.Add(command, "@reason_code", ProductionFactSql.BoundCode(reasonCode, MissionAttemptProvenance.MaximumReasonCodeLength));
                        ProductionFactSql.Add(command, "@mission_id", missionId);
                        ProductionFactSql.Add(command, "@running", MissionAttemptOutcomeEnum.Running.ToString());
                        if (processId.HasValue) ProductionFactSql.AddInt32(command, "@process_id", processId);
                        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }

        private static string? Bound(string? value, int maximumLength)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string trimmed = value.Trim();
            return trimmed.Length > maximumLength ? trimmed.Substring(0, maximumLength) : trimmed;
        }

        private static MissionAttemptProvenance FromReader(DbDataReader reader)
        {
            string outcome = Convert.ToString(reader["outcome"])!;
            if (!Enum.TryParse(outcome, false, out MissionAttemptOutcomeEnum parsed))
                throw new InvalidOperationException("Stored mission attempt provenance has an unknown outcome: " + outcome);
            return new MissionAttemptProvenance
            {
                Id = Convert.ToString(reader["id"])!,
                TenantId = ProductionFactSql.ReadText(reader["tenant_id"]),
                UserId = ProductionFactSql.ReadText(reader["user_id"]),
                MissionId = Convert.ToString(reader["mission_id"])!,
                VoyageId = ProductionFactSql.ReadText(reader["voyage_id"]),
                VesselId = ProductionFactSql.ReadText(reader["vessel_id"]),
                CaptainId = ProductionFactSql.ReadText(reader["captain_id"]),
                Persona = ProductionFactSql.ReadText(reader["persona"]),
                Runtime = ProductionFactSql.ReadText(reader["runtime"]),
                Model = ProductionFactSql.ReadText(reader["model"]),
                RuntimeVersion = ProductionFactSql.ReadText(reader["runtime_version"]),
                ProcessId = ProductionFactSql.ReadNullableInt(reader["process_id"]),
                StartedUtc = ProductionFactSql.ReadUtc(reader["started_utc"]),
                EndedUtc = ProductionFactSql.ReadNullableUtc(reader["ended_utc"]),
                Outcome = parsed,
                ExitCode = ProductionFactSql.ReadNullableInt(reader["exit_code"]),
                ReasonCode = ProductionFactSql.ReadText(reader["reason_code"])
            };
        }
    }
}
