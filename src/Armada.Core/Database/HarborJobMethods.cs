namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
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

        private const string _Columns = "job_id, runner_id, launch_key, tenant_id, user_id, enrollment_generation, session_generation, state, process_id, exit_code, failure_reason, next_output_sequence, mission_id, captain_id, revision, created_utc, last_update_utc, completed_utc";
        private const string _ActiveStates = "('Pending', 'Running', 'Stopping')";
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
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO harbor_jobs (" + _Columns + ") VALUES (@job_id, @runner_id, @launch_key, @tenant_id, @user_id, @enrollment_generation, @session_generation, @state, @process_id, @exit_code, @failure_reason, @next_output_sequence, @mission_id, @captain_id, @revision, @created_utc, @last_update_utc, @completed_utc);";
                    Bind(command, record);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryUpdateAsync(HarborJobRecord record, CancellationToken token = default)
        {
            Validate(record);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE harbor_jobs SET session_generation = @session_generation, state = @state,
                        process_id = @process_id, exit_code = @exit_code, failure_reason = @failure_reason,
                        next_output_sequence = @next_output_sequence, revision = @revision,
                        last_update_utc = @last_update_utc, completed_utc = @completed_utc
                        WHERE job_id = @job_id AND revision < @revision;";
                    Bind(command, record);
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
                    if (query.ActiveOnly) filters.Add("state IN " + _ActiveStates);
                    string where = filters.Count > 0 ? " WHERE " + String.Join(" AND ", filters) : String.Empty;
                    string order = " ORDER BY created_utc DESC, job_id ASC";
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
        public async Task<int> FailActiveAsync(string reason, DateTime nowUtc, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A named reason is required.", nameof(reason));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE harbor_jobs SET state = 'Lost', failure_reason = @failure_reason, revision = revision + 1, last_update_utc = @now_utc, completed_utc = @now_utc WHERE state IN " + _ActiveStates + ";";
                    ProductionFactSql.Add(command, "@failure_reason", reason.Trim());
                    ProductionFactSql.AddUtc(command, "@now_utc", nowUtc, _Provider);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
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
                Revision = Convert.ToInt64(reader["revision"], CultureInfo.InvariantCulture),
                CreatedUtc = ProductionFactSql.ReadUtc(reader["created_utc"]),
                LastUpdateUtc = ProductionFactSql.ReadUtc(reader["last_update_utc"]),
                CompletedUtc = ProductionFactSql.ReadNullableUtc(reader["completed_utc"])
            };
        }

        #endregion
    }
}
