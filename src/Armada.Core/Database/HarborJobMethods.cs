namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;

    /// <summary>
    /// Provider-neutral implementation of durable Harbor job records. Only the migration DDL differs per provider.
    /// </summary>
    public sealed class HarborJobMethods : IHarborJobMethods
    {
        #region Private-Members

        private const string _Columns = "job_id, runner_id, launch_key, tenant_id, user_id, enrollment_generation, session_generation, state, process_id, exit_code, failure_reason, next_output_sequence, mission_id, captain_id, dock_id, admiral_process_id, admiral_process_started_utc, runner_process_started_utc, stop_requested, stop_confirmed, completion_delivered, revision, created_utc, last_update_utc, completed_utc";
        private const string _ActiveStates = "('Pending', 'Running', 'Stopping', 'Uncertain')";
        private const int _MaxCompletionIntentBytes = 64 * 1024;
        private const int _MaxCompletionIntentPayloadBytes = 32 * 1024;
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public HarborJobMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task CreateAsync(HarborJobRecord record, CancellationToken token = default)
        {
            Validate(record);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "INSERT INTO harbor_jobs (" + _Columns + ") VALUES (@job_id, @runner_id, @launch_key, @tenant_id, @user_id, @enrollment_generation, @session_generation, @state, @process_id, @exit_code, @failure_reason, @next_output_sequence, @mission_id, @captain_id, @dock_id, @admiral_process_id, @admiral_process_started_utc, @runner_process_started_utc, @stop_requested, @stop_confirmed, @completion_delivered, @revision, @created_utc, @last_update_utc, @completed_utc);";
                        Bind(command, record);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <inheritdoc />
        public Task<bool> TryUpdateAsync(HarborJobRecord record, CancellationToken token = default)
        {
            return UpdateCoreAsync(record, null, token);
        }

        /// <inheritdoc />
        public Task<bool> TryUpdateAtRevisionAsync(HarborJobRecord record, long expectedRevision, CancellationToken token = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Revision != expectedRevision + 1) throw new ArgumentException("An exact update must advance one revision.", nameof(record));
            return UpdateCoreAsync(record, expectedRevision, token);
        }

        private async Task<bool> UpdateCoreAsync(HarborJobRecord record, long? expectedRevision, CancellationToken token)
        {
            Validate(record);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = @"UPDATE harbor_jobs SET session_generation = @session_generation, state = @state,
                            process_id = @process_id, runner_process_started_utc = @runner_process_started_utc, stop_requested = @stop_requested,
                            stop_confirmed = @stop_confirmed, completion_delivered = @completion_delivered, exit_code = @exit_code, failure_reason = @failure_reason,
                            next_output_sequence = @next_output_sequence, revision = @revision,
                            last_update_utc = @last_update_utc, completed_utc = @completed_utc
                            WHERE job_id = @job_id AND " + (expectedRevision.HasValue ? "revision = @expected_revision;" : "revision < @revision;");
                        Bind(command, record);
                        if (expectedRevision.HasValue) ProductionFactSql.Add(command, "@expected_revision", expectedRevision.Value);
                        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                    }
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryAcknowledgeCompletionDeliveredAsync(string jobId, long expectedRevision, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job identifier is required.", nameof(jobId));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE harbor_jobs SET completion_delivered = 1, revision = revision + 1 WHERE job_id = @job_id AND revision = @revision AND stop_confirmed = 1 AND completion_delivered = 0 AND state IN ('Exited', 'Failed', 'Lost');";
                    ProductionFactSql.Add(command, "@job_id", jobId);
                    ProductionFactSql.Add(command, "@revision", expectedRevision);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        /// <inheritdoc />
        public async Task<HarborJobRecord?> ReadAsync(string jobId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job identifier is required.", nameof(jobId));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _Columns + " FROM harbor_jobs WHERE job_id = @job_id;";
                    ProductionFactSql.Add(command, "@job_id", jobId.Trim());
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return FromReader(reader);
                    }
                }
            }
            return null;
        }

        /// <inheritdoc />
        public async Task<List<HarborJobRecord>> EnumerateAsync(HarborJobQuery query, CancellationToken token = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            List<HarborJobRecord> records = new List<HarborJobRecord>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    List<string> filters = new List<string>();
                    if (query.TenantId != null) { filters.Add("tenant_id = @tenant_id"); ProductionFactSql.Add(command, "@tenant_id", query.TenantId); }
                    if (query.UserId != null) { filters.Add("user_id = @user_id"); ProductionFactSql.Add(command, "@user_id", query.UserId); }
                    if (query.RunnerId != null) { filters.Add("runner_id = @runner_id"); ProductionFactSql.Add(command, "@runner_id", query.RunnerId); }
                    if (query.MissionId != null) { filters.Add("mission_id = @mission_id"); ProductionFactSql.Add(command, "@mission_id", query.MissionId); }
                    if (query.DockId != null) { filters.Add("dock_id = @dock_id"); ProductionFactSql.Add(command, "@dock_id", query.DockId); }
                    if (query.JobIdAfter != null) { filters.Add("job_id > @job_id_after"); ProductionFactSql.Add(command, "@job_id_after", query.JobIdAfter); }
                    if (query.UnconfirmedOnly) filters.Add("stop_confirmed = 0");
                    if (query.RecoveryOnly)
                        filters.Add("(state IN " + _ActiveStates + " OR stop_confirmed = 0 OR (mission_id IS NOT NULL AND completion_delivered = 0))");
                    if (query.UnboundMissionOnly) filters.Add("(mission_id IS NOT NULL AND dock_id IS NULL)");
                    if (query.ActiveOnly) filters.Add("state IN " + _ActiveStates);
                    string where = filters.Count > 0 ? " WHERE " + String.Join(" AND ", filters) : String.Empty;
                    string order = query.RecoveryOnly || query.JobIdAfter != null ? " ORDER BY job_id ASC" : " ORDER BY created_utc DESC, job_id ASC";
                    command.CommandText = _Provider == DatabaseTypeEnum.SqlServer
                        ? "SELECT TOP (@row_limit) " + _Columns + " FROM harbor_jobs" + where + order + ";"
                        : "SELECT " + _Columns + " FROM harbor_jobs" + where + order + " LIMIT @row_limit;";
                    ProductionFactSql.Add(command, "@row_limit", query.Limit);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) records.Add(FromReader(reader));
                    }
                }
            }
            return records;
        }

        /// <inheritdoc />
        public async Task<bool> TryCreateCompletionIntentAsync(HarborCompletionIntent intent, CancellationToken token = default)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            if (intent.Completed) throw new ArgumentException("A new Harbor completion intent cannot already be complete.", nameof(intent));
            string payload = SerializeCompletionIntent(intent);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO harbor_completion_intents (job_id, payload, completed) VALUES (@job_id, @payload, 0);";
                    ProductionFactSql.Add(command, "@job_id", intent.JobId);
                    ProductionFactSql.Add(command, "@payload", payload);
                    try
                    {
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        return true;
                    }
                    catch (DbException exception) when (IsCompletionIntentDuplicate(exception))
                    {
                        return false;
                    }
                }
            }
        }

        /// <inheritdoc />
        public async Task<HarborCompletionIntent?> ReadCompletionIntentAsync(string jobId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job identifier is required.", nameof(jobId));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT payload, completed FROM harbor_completion_intents WHERE job_id = @job_id;";
                    ProductionFactSql.Add(command, "@job_id", jobId.Trim());
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
                        string payload = Convert.ToString(reader["payload"], CultureInfo.InvariantCulture) ?? String.Empty;
                        HarborCompletionIntent? intent = JsonSerializer.Deserialize<HarborCompletionIntent>(payload);
                        if (intent == null) throw new InvalidOperationException("Stored Harbor completion intent is empty.");
                        ValidateCompletionIntent(intent);
                        if (!String.Equals(intent.JobId, jobId.Trim(), StringComparison.Ordinal))
                            throw new InvalidOperationException("Stored Harbor completion intent job identifier does not match its row.");
                        intent.Completed = ProductionFactSql.ReadBool(reader["completed"]);
                        return intent;
                    }
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryCompleteCompletionIntentAsync(string jobId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job identifier is required.", nameof(jobId));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE harbor_completion_intents SET completed = 1 WHERE job_id = @job_id AND completed = 0;";
                    ProductionFactSql.Add(command, "@job_id", jobId.Trim());
                    int updated = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    if (updated == 1) return true;
                }
            }
            HarborCompletionIntent? existing = await ReadCompletionIntentAsync(jobId, token).ConfigureAwait(false);
            return existing != null && existing.Completed;
        }

        /// <inheritdoc />
        public async Task<int> FailActiveAsync(string reason, DateTime nowUtc, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A named reason is required.", nameof(reason));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "UPDATE harbor_jobs SET state = 'Lost', failure_reason = @failure_reason, revision = revision + 1, last_update_utc = @now_utc, completed_utc = @now_utc WHERE state IN " + _ActiveStates + ";";
                        ProductionFactSql.Add(command, "@failure_reason", reason.Trim());
                        ProductionFactSql.AddUtc(command, "@now_utc", nowUtc, _Provider);
                        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }

        #endregion

        #region Private-Methods

        private static void Validate(HarborJobRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (String.IsNullOrWhiteSpace(record.JobId)) throw new ArgumentException("Job identifier is required.", nameof(record));
            if (String.IsNullOrWhiteSpace(record.RunnerId)) throw new ArgumentException("Runner identifier is required.", nameof(record));
            if (String.IsNullOrWhiteSpace(record.TenantId) || String.IsNullOrWhiteSpace(record.UserId))
                throw new ArgumentException("A job always records its runner's enrolled tenant and user.", nameof(record));
        }

        private static string SerializeCompletionIntent(HarborCompletionIntent intent)
        {
            ValidateCompletionIntent(intent);
            string payload = JsonSerializer.Serialize(intent);
            if (Encoding.UTF8.GetByteCount(payload) > _MaxCompletionIntentBytes)
                throw new ArgumentException("Harbor completion intent exceeds 64 KiB.", nameof(intent));
            return payload;
        }

        private static void ValidateCompletionIntent(HarborCompletionIntent intent)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            if (String.IsNullOrWhiteSpace(intent.JobId) || intent.JobId.Length > 450
                || !String.Equals(intent.JobId, intent.JobId.Trim(), StringComparison.Ordinal))
                throw new ArgumentException("A bounded Harbor job identifier is required.", nameof(intent));
            if (!Enum.IsDefined(intent.Outcome)) throw new ArgumentException("Harbor completion outcome is invalid.", nameof(intent));
            if (!Enum.IsDefined(intent.CaptainState)) throw new ArgumentException("Harbor completion captain state is invalid.", nameof(intent));
            if (intent.RecoveryAttempts.HasValue && intent.RecoveryAttempts.Value < 0)
                throw new ArgumentException("Harbor completion recovery attempts cannot be negative.", nameof(intent));
            if (String.IsNullOrWhiteSpace(intent.FailureReason) || intent.FailureReason.Length > 8192)
                throw new ArgumentException("A bounded Harbor completion failure reason is required.", nameof(intent));
            if (String.IsNullOrWhiteSpace(intent.ReasonCode) || intent.ReasonCode.Length > 256)
                throw new ArgumentException("A bounded Harbor completion reason code is required.", nameof(intent));
            if (String.IsNullOrWhiteSpace(intent.FactId) || intent.FactId.Length > 450
                || String.IsNullOrWhiteSpace(intent.EventId) || intent.EventId.Length > 450
                || String.IsNullOrWhiteSpace(intent.SignalId) || intent.SignalId.Length > 450)
                throw new ArgumentException("Stable Harbor completion audit identifiers are required.", nameof(intent));
            if (intent.QuarantineReason != null && intent.QuarantineReason.Length > 1024)
                throw new ArgumentException("Harbor completion quarantine reason exceeds its bound.", nameof(intent));
            if (intent.QuarantineUntilUtc.HasValue && intent.QuarantineUntilUtc.Value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Harbor completion quarantine time must be UTC.", nameof(intent));
            if (String.IsNullOrWhiteSpace(intent.Payload)
                || Encoding.UTF8.GetByteCount(intent.Payload) > _MaxCompletionIntentPayloadBytes)
                throw new ArgumentException("Harbor completion intent payload must be valid JSON within 32 KiB.", nameof(intent));
            try
            {
                if (JsonNode.Parse(intent.Payload) is not JsonObject)
                    throw new ArgumentException("Harbor completion intent payload must be a JSON object.", nameof(intent));
            }
            catch (JsonException exception)
            {
                throw new ArgumentException("Harbor completion intent payload is invalid JSON.", nameof(intent), exception);
            }
        }

        private bool IsCompletionIntentDuplicate(DbException exception)
        {
            if (_Provider == DatabaseTypeEnum.Mysql && exception is MySqlConnector.MySqlException mysqlException)
                return mysqlException.Number == 1062;
            if (_Provider == DatabaseTypeEnum.Postgresql && exception is Npgsql.PostgresException postgresException)
                return postgresException.SqlState == "23505";
            if (_Provider == DatabaseTypeEnum.SqlServer && exception is Microsoft.Data.SqlClient.SqlException sqlException)
                return sqlException.Number == 2601 || sqlException.Number == 2627;
            if (_Provider == DatabaseTypeEnum.Sqlite && exception is Microsoft.Data.Sqlite.SqliteException sqliteException)
                return sqliteException.SqliteErrorCode == 19;
            return false;
        }

        private void Bind(DbCommand command, HarborJobRecord record)
        {
            ProductionFactSql.Add(command, "@job_id", record.JobId);
            ProductionFactSql.Add(command, "@runner_id", record.RunnerId);
            ProductionFactSql.Add(command, "@launch_key", record.LaunchKey);
            ProductionFactSql.Add(command, "@tenant_id", record.TenantId);
            ProductionFactSql.Add(command, "@user_id", record.UserId);
            ProductionFactSql.Add(command, "@enrollment_generation", record.EnrollmentGeneration);
            ProductionFactSql.Add(command, "@session_generation", record.SessionGeneration);
            ProductionFactSql.Add(command, "@state", record.State.ToString());
            AddNullableInt(command, "@process_id", record.ProcessId);
            AddNullableInt(command, "@exit_code", record.ExitCode);
            ProductionFactSql.Add(command, "@failure_reason", record.FailureReason);
            ProductionFactSql.Add(command, "@next_output_sequence", record.NextOutputSequence);
            ProductionFactSql.Add(command, "@mission_id", record.MissionId);
            ProductionFactSql.Add(command, "@captain_id", record.CaptainId);
            ProductionFactSql.Add(command, "@dock_id", record.DockId);
            AddNullableInt(command, "@admiral_process_id", record.AdmiralProcessId);
            ProductionFactSql.AddUtc(command, "@admiral_process_started_utc", record.AdmiralProcessStartedUtc, _Provider);
            ProductionFactSql.AddUtc(command, "@runner_process_started_utc", record.RunnerProcessStartedUtc, _Provider);
            ProductionFactSql.Add(command, "@stop_requested", record.StopRequested ? 1 : 0);
            ProductionFactSql.Add(command, "@stop_confirmed", record.StopConfirmed ? 1 : 0);
            ProductionFactSql.Add(command, "@completion_delivered", record.CompletionDelivered ? 1 : 0);
            ProductionFactSql.Add(command, "@revision", record.Revision);
            ProductionFactSql.AddUtc(command, "@created_utc", record.CreatedUtc, _Provider);
            ProductionFactSql.AddUtc(command, "@last_update_utc", record.LastUpdateUtc, _Provider);
            ProductionFactSql.AddUtc(command, "@completed_utc", record.CompletedUtc, _Provider);
        }

        private static void AddNullableInt(DbCommand command, string name, int? value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = System.Data.DbType.Int32;
            parameter.Value = value.HasValue ? (object)value.Value : DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static HarborJobRecord FromReader(DbDataReader reader)
        {
            string state = Convert.ToString(reader["state"], CultureInfo.InvariantCulture)!;
            if (!Enum.TryParse(state, false, out HarborJobStateEnum parsed))
                throw new InvalidOperationException("Stored Harbor job has an unknown state: " + state);
            return new HarborJobRecord
            {
                JobId = Convert.ToString(reader["job_id"], CultureInfo.InvariantCulture)!,
                RunnerId = Convert.ToString(reader["runner_id"], CultureInfo.InvariantCulture)!,
                LaunchKey = Convert.ToString(reader["launch_key"], CultureInfo.InvariantCulture)!,
                TenantId = Convert.ToString(reader["tenant_id"], CultureInfo.InvariantCulture)!,
                UserId = Convert.ToString(reader["user_id"], CultureInfo.InvariantCulture)!,
                EnrollmentGeneration = Convert.ToInt64(reader["enrollment_generation"], CultureInfo.InvariantCulture),
                SessionGeneration = Convert.ToInt64(reader["session_generation"], CultureInfo.InvariantCulture),
                State = parsed,
                ProcessId = reader["process_id"] == DBNull.Value ? null : Convert.ToInt32(reader["process_id"], CultureInfo.InvariantCulture),
                ExitCode = reader["exit_code"] == DBNull.Value ? null : Convert.ToInt32(reader["exit_code"], CultureInfo.InvariantCulture),
                FailureReason = ProductionFactSql.ReadText(reader["failure_reason"]),
                NextOutputSequence = Convert.ToInt64(reader["next_output_sequence"], CultureInfo.InvariantCulture),
                MissionId = ProductionFactSql.ReadText(reader["mission_id"]),
                CaptainId = ProductionFactSql.ReadText(reader["captain_id"]),
                DockId = ProductionFactSql.ReadText(reader["dock_id"]),
                AdmiralProcessId = reader["admiral_process_id"] == DBNull.Value ? null : Convert.ToInt32(reader["admiral_process_id"], CultureInfo.InvariantCulture),
                AdmiralProcessStartedUtc = ProductionFactSql.ReadNullableUtc(reader["admiral_process_started_utc"]),
                RunnerProcessStartedUtc = ProductionFactSql.ReadNullableUtc(reader["runner_process_started_utc"]),
                StopRequested = ProductionFactSql.ReadBool(reader["stop_requested"]),
                StopConfirmed = ProductionFactSql.ReadBool(reader["stop_confirmed"]),
                CompletionDelivered = ProductionFactSql.ReadBool(reader["completion_delivered"]),
                Revision = Convert.ToInt64(reader["revision"], CultureInfo.InvariantCulture),
                CreatedUtc = ProductionFactSql.ReadUtc(reader["created_utc"]),
                LastUpdateUtc = ProductionFactSql.ReadUtc(reader["last_update_utc"]),
                CompletedUtc = ProductionFactSql.ReadNullableUtc(reader["completed_utc"])
            };
        }

        #endregion
    }
}
