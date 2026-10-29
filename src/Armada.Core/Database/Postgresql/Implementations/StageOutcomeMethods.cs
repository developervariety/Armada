namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// PostgreSQL implementation of stage outcome persistence.
    /// </summary>
    public class StageOutcomeMethods : IStageOutcomeMethods
    {
        #region Private-Members

        private static readonly string _Insert = @"INSERT INTO stage_outcomes
            (id, tenant_id, user_id, mission_id, voyage_id, vessel_id, captain_id, captain_name, persona, attempt, rework_round,
             runtime, model, model_provider, model_endpoint_id, harbor_id, context_tokens, temperature, top_p, tools_enabled,
             mcp_tools_enabled, reasoning_effort, runtime_version, model_digest, prompt_version, armada_version, exit_code,
             runtime_failure_kind, stage_check_status, stage_check_reason, disposition, failure_kind, failure_reason, judge_verdict,
             reviewed_mission_id, next_captain_id, commit_hash, finished_by_outcome_id, finished_by_captain_id, prior_work_kept, contract_repair_kind,
             suspected_cause, cause_source, cause_rule_id, cause_note, classified_by_user_id,
             classified_utc, is_evaluation, started_utc, ended_utc, duration_ms, created_utc, last_update_utc)
            VALUES
            (@id, @tenant_id, @user_id, @mission_id, @voyage_id, @vessel_id, @captain_id, @captain_name, @persona, @attempt, @rework_round,
             @runtime, @model, @model_provider, @model_endpoint_id, @harbor_id, @context_tokens, @temperature, @top_p, @tools_enabled,
             @mcp_tools_enabled, @reasoning_effort, @runtime_version, @model_digest, @prompt_version, @armada_version, @exit_code,
             @runtime_failure_kind, @stage_check_status, @stage_check_reason, @disposition, @failure_kind, @failure_reason, @judge_verdict,
             @reviewed_mission_id, @next_captain_id, @commit_hash, @finished_by_outcome_id, @finished_by_captain_id, @prior_work_kept, @contract_repair_kind,
             @suspected_cause, @cause_source, @cause_rule_id, @cause_note, @classified_by_user_id,
             @classified_utc, @is_evaluation, @started_utc, @ended_utc, @duration_ms, @created_utc, @last_update_utc);";

        private static readonly string _Update = @"UPDATE stage_outcomes SET
            user_id = @user_id, mission_id = @mission_id, voyage_id = @voyage_id, vessel_id = @vessel_id, captain_id = @captain_id,
            captain_name = @captain_name, persona = @persona, attempt = @attempt, rework_round = @rework_round, runtime = @runtime,
            model = @model, model_provider = @model_provider, model_endpoint_id = @model_endpoint_id, harbor_id = @harbor_id,
            context_tokens = @context_tokens, temperature = @temperature, top_p = @top_p, tools_enabled = @tools_enabled,
            mcp_tools_enabled = @mcp_tools_enabled, reasoning_effort = @reasoning_effort, runtime_version = @runtime_version,
            model_digest = @model_digest, prompt_version = @prompt_version, armada_version = @armada_version, exit_code = @exit_code,
            runtime_failure_kind = @runtime_failure_kind, stage_check_status = @stage_check_status, stage_check_reason = @stage_check_reason,
            disposition = @disposition, failure_kind = @failure_kind, failure_reason = @failure_reason, judge_verdict = @judge_verdict,
            reviewed_mission_id = @reviewed_mission_id, next_captain_id = @next_captain_id, commit_hash = @commit_hash,
            finished_by_outcome_id = @finished_by_outcome_id, finished_by_captain_id = @finished_by_captain_id,
            prior_work_kept = @prior_work_kept, contract_repair_kind = @contract_repair_kind, suspected_cause = @suspected_cause,
            cause_source = @cause_source, cause_rule_id = @cause_rule_id, cause_note = @cause_note,
            classified_by_user_id = @classified_by_user_id, classified_utc = @classified_utc, is_evaluation = @is_evaluation,
            started_utc = @started_utc, ended_utc = @ended_utc, duration_ms = @duration_ms, last_update_utc = @last_update_utc
            WHERE id = @id;";

        private readonly string _ConnectionString;
        private readonly SemaphoreSlim? _WriteLock;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">PostgreSQL database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
        public StageOutcomeMethods(PostgresqlDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            if (driver == null) throw new ArgumentNullException(nameof(driver));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            _ConnectionString = driver.ConnectionString;
            _WriteLock = null;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<StageOutcome> CreateAsync(StageOutcome outcome, CancellationToken token = default)
        {
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            outcome.LastUpdateUtc = DateTime.UtcNow;
            await PostgresqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (NpgsqlConnection conn, NpgsqlTransaction tx) =>
            {
                await PostgresqlCommandHelper.ExecuteAsync(conn, tx, _Insert, cmd => Bind(cmd, outcome, true), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return outcome;
        }

        /// <inheritdoc />
        public async Task<StageOutcome?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            List<StageOutcome> rows = await PostgresqlCommandHelper.QueryAsync(_ConnectionString,
                "SELECT * FROM stage_outcomes WHERE id = @id;",
                cmd => PostgresqlCommandHelper.Add(cmd, "@id", id), FromReader, token).ConfigureAwait(false);
            return rows.FirstOrDefault();
        }

        /// <inheritdoc />
        public async Task<StageOutcome> UpdateAsync(StageOutcome outcome, CancellationToken token = default)
        {
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            outcome.LastUpdateUtc = DateTime.UtcNow;
            await PostgresqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (NpgsqlConnection conn, NpgsqlTransaction tx) =>
            {
                await PostgresqlCommandHelper.ExecuteAsync(conn, tx, _Update, cmd => Bind(cmd, outcome, false), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return outcome;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            int deleted = 0;
            await PostgresqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (NpgsqlConnection conn, NpgsqlTransaction tx) =>
            {
                deleted = await PostgresqlCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM stage_outcomes WHERE id = @id;",
                    cmd => PostgresqlCommandHelper.Add(cmd, "@id", id), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return deleted > 0;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<StageOutcome>> EnumerateAsync(StageOutcomeQuery query, CancellationToken token = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            int pageSize = query.PageSize < 1 ? 1 : (query.PageSize > 1000 ? 1000 : query.PageSize);
            int pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
            int offset = (pageNumber - 1) * pageSize;

            List<string> conditions = new List<string>();
            if (query.TenantId != null) conditions.Add("tenant_id = @tenant_id");
            if (query.MissionId != null) conditions.Add("mission_id = @mission_id");
            if (query.VoyageId != null) conditions.Add("voyage_id = @voyage_id");
            if (query.VesselId != null) conditions.Add("vessel_id = @vessel_id");
            if (query.CaptainId != null) conditions.Add("captain_id = @captain_id");
            if (query.Persona != null) conditions.Add("LOWER(persona) = @persona");
            if (query.Model != null) conditions.Add("LOWER(model) = @model");
            if (query.SuspectedCause.HasValue) conditions.Add("suspected_cause = @suspected_cause");
            if (query.CauseSource.HasValue) conditions.Add("cause_source = @cause_source");
            if (query.Disposition.HasValue) conditions.Add("disposition = @disposition");
            if (query.IsEvaluation.HasValue) conditions.Add("is_evaluation = @is_evaluation");
            if (query.FromUtc.HasValue) conditions.Add("ended_utc >= @from_utc");
            if (query.ToUtc.HasValue) conditions.Add("ended_utc < @to_utc");

            Action<NpgsqlCommand> bind = cmd =>
            {
                if (query.TenantId != null) PostgresqlCommandHelper.Add(cmd, "@tenant_id", query.TenantId);
                if (query.MissionId != null) PostgresqlCommandHelper.Add(cmd, "@mission_id", query.MissionId);
                if (query.VoyageId != null) PostgresqlCommandHelper.Add(cmd, "@voyage_id", query.VoyageId);
                if (query.VesselId != null) PostgresqlCommandHelper.Add(cmd, "@vessel_id", query.VesselId);
                if (query.CaptainId != null) PostgresqlCommandHelper.Add(cmd, "@captain_id", query.CaptainId);
                if (query.Persona != null) PostgresqlCommandHelper.Add(cmd, "@persona", query.Persona.ToLowerInvariant());
                if (query.Model != null) PostgresqlCommandHelper.Add(cmd, "@model", query.Model.ToLowerInvariant());
                if (query.SuspectedCause.HasValue) PostgresqlCommandHelper.Add(cmd, "@suspected_cause", query.SuspectedCause.Value.ToString());
                if (query.CauseSource.HasValue) PostgresqlCommandHelper.Add(cmd, "@cause_source", query.CauseSource.Value.ToString());
                if (query.Disposition.HasValue) PostgresqlCommandHelper.Add(cmd, "@disposition", query.Disposition.Value.ToString());
                if (query.IsEvaluation.HasValue) PostgresqlCommandHelper.Add(cmd, "@is_evaluation", query.IsEvaluation.Value);
                if (query.FromUtc.HasValue) PostgresqlCommandHelper.AddDate(cmd, "@from_utc", query.FromUtc.Value);
                if (query.ToUtc.HasValue) PostgresqlCommandHelper.AddDate(cmd, "@to_utc", query.ToUtc.Value);
            };

            string where = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                long total = await PostgresqlCommandHelper.CountAsync(conn, "SELECT COUNT(*) FROM stage_outcomes" + where + ";", bind, token).ConfigureAwait(false);
                List<StageOutcome> rows = await PostgresqlCommandHelper.QueryAsync(conn,
                    "SELECT * FROM stage_outcomes" + where + " ORDER BY ended_utc DESC, id DESC" + PostgresqlCommandHelper.Page(offset, pageSize) + ";",
                    bind, FromReader, token).ConfigureAwait(false);

                EnumerationResult<StageOutcome> result = new EnumerationResult<StageOutcome>();
                result.PageNumber = pageNumber;
                result.PageSize = pageSize;
                result.TotalRecords = total;
                result.TotalPages = (int)Math.Ceiling((double)total / pageSize);
                result.Objects = rows;
                return result;
            }
        }

        /// <inheritdoc />
        public async Task<int> CountByMissionAsync(string missionId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));
            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                long count = await PostgresqlCommandHelper.CountAsync(conn, "SELECT COUNT(*) FROM stage_outcomes WHERE mission_id = @mission_id;",
                    cmd => PostgresqlCommandHelper.Add(cmd, "@mission_id", missionId), token).ConfigureAwait(false);
                return (int)count;
            }
        }

        /// <inheritdoc />
        public async Task<int> DeleteByMissionAsync(string missionId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));
            int deleted = 0;
            await PostgresqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (NpgsqlConnection conn, NpgsqlTransaction tx) =>
            {
                deleted = await PostgresqlCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM stage_outcomes WHERE mission_id = @mission_id;",
                    cmd => PostgresqlCommandHelper.Add(cmd, "@mission_id", missionId), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return deleted;
        }

        #endregion

        #region Private-Methods

        private static void Bind(NpgsqlCommand cmd, StageOutcome outcome, bool includeCreateColumns)
        {
            PostgresqlCommandHelper.Add(cmd, "@id", outcome.Id);
            if (includeCreateColumns)
            {
                PostgresqlCommandHelper.Add(cmd, "@tenant_id", outcome.TenantId);
                PostgresqlCommandHelper.AddDate(cmd, "@created_utc", outcome.CreatedUtc);
            }

            PostgresqlCommandHelper.Add(cmd, "@user_id", outcome.UserId);
            PostgresqlCommandHelper.Add(cmd, "@mission_id", outcome.MissionId);
            PostgresqlCommandHelper.Add(cmd, "@voyage_id", outcome.VoyageId);
            PostgresqlCommandHelper.Add(cmd, "@vessel_id", outcome.VesselId);
            PostgresqlCommandHelper.Add(cmd, "@captain_id", outcome.CaptainId);
            PostgresqlCommandHelper.Add(cmd, "@captain_name", outcome.CaptainName);
            PostgresqlCommandHelper.Add(cmd, "@persona", outcome.Persona);
            PostgresqlCommandHelper.Add(cmd, "@attempt", outcome.Attempt);
            PostgresqlCommandHelper.Add(cmd, "@rework_round", outcome.ReworkRound);
            PostgresqlCommandHelper.Add(cmd, "@runtime", EnumName(outcome.Runtime));
            PostgresqlCommandHelper.Add(cmd, "@model", outcome.Model);
            PostgresqlCommandHelper.Add(cmd, "@model_provider", outcome.ModelProvider);
            PostgresqlCommandHelper.Add(cmd, "@model_endpoint_id", outcome.ModelEndpointId);
            PostgresqlCommandHelper.Add(cmd, "@harbor_id", outcome.HarborId);
            PostgresqlCommandHelper.Add(cmd, "@context_tokens", outcome.ContextTokens);
            PostgresqlCommandHelper.Add(cmd, "@temperature", outcome.Temperature);
            PostgresqlCommandHelper.Add(cmd, "@top_p", outcome.TopP);
            PostgresqlCommandHelper.Add(cmd, "@tools_enabled", outcome.ToolsEnabled);
            PostgresqlCommandHelper.Add(cmd, "@mcp_tools_enabled", outcome.McpToolsEnabled);
            PostgresqlCommandHelper.Add(cmd, "@reasoning_effort", outcome.ReasoningEffort);
            PostgresqlCommandHelper.Add(cmd, "@runtime_version", outcome.RuntimeVersion);
            PostgresqlCommandHelper.Add(cmd, "@model_digest", outcome.ModelDigest);
            PostgresqlCommandHelper.Add(cmd, "@prompt_version", outcome.PromptVersion);
            PostgresqlCommandHelper.Add(cmd, "@armada_version", outcome.ArmadaVersion);
            PostgresqlCommandHelper.Add(cmd, "@exit_code", outcome.ExitCode);
            PostgresqlCommandHelper.Add(cmd, "@runtime_failure_kind", EnumName(outcome.RuntimeFailureKind));
            PostgresqlCommandHelper.Add(cmd, "@stage_check_status", EnumName(outcome.StageCheckStatus));
            PostgresqlCommandHelper.Add(cmd, "@stage_check_reason", EnumName(outcome.StageCheckReason));
            PostgresqlCommandHelper.Add(cmd, "@disposition", outcome.Disposition.ToString());
            PostgresqlCommandHelper.Add(cmd, "@failure_kind", EnumName(outcome.FailureKind));
            PostgresqlCommandHelper.Add(cmd, "@failure_reason", outcome.FailureReason);
            PostgresqlCommandHelper.Add(cmd, "@judge_verdict", outcome.JudgeVerdict);
            PostgresqlCommandHelper.Add(cmd, "@reviewed_mission_id", outcome.ReviewedMissionId);
            PostgresqlCommandHelper.Add(cmd, "@next_captain_id", outcome.NextCaptainId);
            PostgresqlCommandHelper.Add(cmd, "@commit_hash", outcome.CommitHash);
            PostgresqlCommandHelper.Add(cmd, "@finished_by_outcome_id", outcome.FinishedByOutcomeId);
            PostgresqlCommandHelper.Add(cmd, "@finished_by_captain_id", outcome.FinishedByCaptainId);
            PostgresqlCommandHelper.Add(cmd, "@prior_work_kept", outcome.PriorWorkKept);
            PostgresqlCommandHelper.Add(cmd, "@contract_repair_kind", EnumName(outcome.ContractRepairKind));
            PostgresqlCommandHelper.Add(cmd, "@suspected_cause", outcome.SuspectedCause.ToString());
            PostgresqlCommandHelper.Add(cmd, "@cause_source", outcome.CauseSource.ToString());
            PostgresqlCommandHelper.Add(cmd, "@cause_rule_id", outcome.CauseRuleId);
            PostgresqlCommandHelper.Add(cmd, "@cause_note", outcome.CauseNote);
            PostgresqlCommandHelper.Add(cmd, "@classified_by_user_id", outcome.ClassifiedByUserId);
            PostgresqlCommandHelper.AddDate(cmd, "@classified_utc", outcome.ClassifiedUtc);
            PostgresqlCommandHelper.Add(cmd, "@is_evaluation", outcome.IsEvaluation);
            PostgresqlCommandHelper.AddDate(cmd, "@started_utc", outcome.StartedUtc);
            PostgresqlCommandHelper.AddDate(cmd, "@ended_utc", outcome.EndedUtc);
            PostgresqlCommandHelper.Add(cmd, "@duration_ms", outcome.DurationMs);
            PostgresqlCommandHelper.AddDate(cmd, "@last_update_utc", outcome.LastUpdateUtc);
        }

        private static StageOutcome FromReader(NpgsqlDataReader reader)
        {
            StageOutcome outcome = new StageOutcome();
            outcome.Id = reader["id"].ToString()!;
            outcome.TenantId = PostgresqlCommandHelper.ReadString(reader["tenant_id"]);
            outcome.UserId = PostgresqlCommandHelper.ReadString(reader["user_id"]);
            outcome.MissionId = reader["mission_id"]?.ToString() ?? String.Empty;
            outcome.VoyageId = PostgresqlCommandHelper.ReadString(reader["voyage_id"]);
            outcome.VesselId = PostgresqlCommandHelper.ReadString(reader["vessel_id"]);
            outcome.CaptainId = PostgresqlCommandHelper.ReadString(reader["captain_id"]);
            outcome.CaptainName = PostgresqlCommandHelper.ReadString(reader["captain_name"]);
            outcome.Persona = PostgresqlCommandHelper.ReadString(reader["persona"]);
            outcome.Attempt = PostgresqlCommandHelper.ReadInt(reader["attempt"], 1);
            outcome.ReworkRound = PostgresqlCommandHelper.ReadInt(reader["rework_round"], 0);
            outcome.Runtime = ReadNullableEnum<AgentRuntimeEnum>(reader["runtime"]);
            outcome.Model = PostgresqlCommandHelper.ReadString(reader["model"]);
            outcome.ModelProvider = PostgresqlCommandHelper.ReadString(reader["model_provider"]);
            outcome.ModelEndpointId = PostgresqlCommandHelper.ReadString(reader["model_endpoint_id"]);
            outcome.HarborId = PostgresqlCommandHelper.ReadString(reader["harbor_id"]);
            outcome.ContextTokens = PostgresqlCommandHelper.ReadNullableInt(reader["context_tokens"]);
            outcome.Temperature = PostgresqlCommandHelper.ReadNullableDouble(reader["temperature"]);
            outcome.TopP = PostgresqlCommandHelper.ReadNullableDouble(reader["top_p"]);
            outcome.ToolsEnabled = PostgresqlCommandHelper.ReadNullableBool(reader["tools_enabled"]);
            outcome.McpToolsEnabled = PostgresqlCommandHelper.ReadNullableBool(reader["mcp_tools_enabled"]);
            outcome.ReasoningEffort = PostgresqlCommandHelper.ReadString(reader["reasoning_effort"]);
            outcome.RuntimeVersion = PostgresqlCommandHelper.ReadString(reader["runtime_version"]);
            outcome.ModelDigest = PostgresqlCommandHelper.ReadString(reader["model_digest"]);
            outcome.PromptVersion = PostgresqlCommandHelper.ReadString(reader["prompt_version"]);
            outcome.ArmadaVersion = PostgresqlCommandHelper.ReadString(reader["armada_version"]);
            outcome.ExitCode = PostgresqlCommandHelper.ReadNullableInt(reader["exit_code"]);
            outcome.RuntimeFailureKind = ReadNullableEnum<RuntimeFailureKindEnum>(reader["runtime_failure_kind"]);
            outcome.StageCheckStatus = ReadNullableEnum<StageCheckStatusEnum>(reader["stage_check_status"]);
            outcome.StageCheckReason = ReadNullableEnum<StageCheckReasonEnum>(reader["stage_check_reason"]);
            outcome.Disposition = PostgresqlCommandHelper.ReadEnum(reader["disposition"], StageDispositionEnum.Pending);
            outcome.FailureKind = ReadNullableEnum<MissionFailureKindEnum>(reader["failure_kind"]);
            outcome.FailureReason = PostgresqlCommandHelper.ReadString(reader["failure_reason"]);
            outcome.JudgeVerdict = PostgresqlCommandHelper.ReadString(reader["judge_verdict"]);
            outcome.ReviewedMissionId = PostgresqlCommandHelper.ReadString(reader["reviewed_mission_id"]);
            outcome.NextCaptainId = PostgresqlCommandHelper.ReadString(reader["next_captain_id"]);
            outcome.CommitHash = PostgresqlCommandHelper.ReadString(reader["commit_hash"]);
            outcome.FinishedByOutcomeId = PostgresqlCommandHelper.ReadString(reader["finished_by_outcome_id"]);
            outcome.FinishedByCaptainId = PostgresqlCommandHelper.ReadString(reader["finished_by_captain_id"]);
            outcome.PriorWorkKept = PostgresqlCommandHelper.ReadNullableBool(reader["prior_work_kept"]);
            outcome.ContractRepairKind = ReadNullableEnum<ContractRepairKindEnum>(reader["contract_repair_kind"]);
            outcome.SuspectedCause = PostgresqlCommandHelper.ReadEnum(reader["suspected_cause"], FailureCauseEnum.None);
            outcome.CauseSource = PostgresqlCommandHelper.ReadEnum(reader["cause_source"], FailureCauseSourceEnum.None);
            outcome.CauseRuleId = PostgresqlCommandHelper.ReadString(reader["cause_rule_id"]);
            outcome.CauseNote = PostgresqlCommandHelper.ReadString(reader["cause_note"]);
            outcome.ClassifiedByUserId = PostgresqlCommandHelper.ReadString(reader["classified_by_user_id"]);
            outcome.ClassifiedUtc = PostgresqlCommandHelper.ReadNullableDate(reader["classified_utc"]);
            outcome.IsEvaluation = PostgresqlCommandHelper.ReadBool(reader["is_evaluation"], false);
            outcome.StartedUtc = PostgresqlCommandHelper.ReadNullableDate(reader["started_utc"]);
            outcome.EndedUtc = PostgresqlCommandHelper.ReadDate(reader["ended_utc"]);
            outcome.DurationMs = PostgresqlCommandHelper.ReadNullableLong(reader["duration_ms"]);
            outcome.CreatedUtc = PostgresqlCommandHelper.ReadDate(reader["created_utc"]);
            outcome.LastUpdateUtc = PostgresqlCommandHelper.ReadDate(reader["last_update_utc"]);
            return outcome;
        }

        private static string? EnumName<TEnum>(TEnum? value) where TEnum : struct
        {
            return value.HasValue ? value.Value.ToString() : null;
        }

        private static TEnum? ReadNullableEnum<TEnum>(object value) where TEnum : struct
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            if (String.IsNullOrEmpty(str)) return null;
            return Enum.TryParse<TEnum>(str, true, out TEnum parsed) ? parsed : (TEnum?)null;
        }

        #endregion
    }
}
