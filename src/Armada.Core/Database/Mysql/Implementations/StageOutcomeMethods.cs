namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL implementation of stage outcome persistence.
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
        /// <param name="connectionString">MySQL connection string.</param>
        /// <exception cref="ArgumentNullException">Thrown when connectionString is null.</exception>
        public StageOutcomeMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _WriteLock = null;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<StageOutcome> CreateAsync(StageOutcome outcome, CancellationToken token = default)
        {
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            outcome.LastUpdateUtc = DateTime.UtcNow;
            await MysqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (MySqlConnection conn, MySqlTransaction tx) =>
            {
                await MysqlCommandHelper.ExecuteAsync(conn, tx, _Insert, cmd => Bind(cmd, outcome, true), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return outcome;
        }

        /// <inheritdoc />
        public async Task<StageOutcome?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            List<StageOutcome> rows = await MysqlCommandHelper.QueryAsync(_ConnectionString,
                "SELECT * FROM stage_outcomes WHERE id = @id;",
                cmd => MysqlCommandHelper.Add(cmd, "@id", id), FromReader, token).ConfigureAwait(false);
            return rows.FirstOrDefault();
        }

        /// <inheritdoc />
        public async Task<StageOutcome> UpdateAsync(StageOutcome outcome, CancellationToken token = default)
        {
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            outcome.LastUpdateUtc = DateTime.UtcNow;
            await MysqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (MySqlConnection conn, MySqlTransaction tx) =>
            {
                await MysqlCommandHelper.ExecuteAsync(conn, tx, _Update, cmd => Bind(cmd, outcome, false), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return outcome;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            int deleted = 0;
            await MysqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (MySqlConnection conn, MySqlTransaction tx) =>
            {
                deleted = await MysqlCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM stage_outcomes WHERE id = @id;",
                    cmd => MysqlCommandHelper.Add(cmd, "@id", id), token).ConfigureAwait(false);
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

            Action<MySqlCommand> bind = cmd =>
            {
                if (query.TenantId != null) MysqlCommandHelper.Add(cmd, "@tenant_id", query.TenantId);
                if (query.MissionId != null) MysqlCommandHelper.Add(cmd, "@mission_id", query.MissionId);
                if (query.VoyageId != null) MysqlCommandHelper.Add(cmd, "@voyage_id", query.VoyageId);
                if (query.VesselId != null) MysqlCommandHelper.Add(cmd, "@vessel_id", query.VesselId);
                if (query.CaptainId != null) MysqlCommandHelper.Add(cmd, "@captain_id", query.CaptainId);
                if (query.Persona != null) MysqlCommandHelper.Add(cmd, "@persona", query.Persona.ToLowerInvariant());
                if (query.Model != null) MysqlCommandHelper.Add(cmd, "@model", query.Model.ToLowerInvariant());
                if (query.SuspectedCause.HasValue) MysqlCommandHelper.Add(cmd, "@suspected_cause", query.SuspectedCause.Value.ToString());
                if (query.CauseSource.HasValue) MysqlCommandHelper.Add(cmd, "@cause_source", query.CauseSource.Value.ToString());
                if (query.Disposition.HasValue) MysqlCommandHelper.Add(cmd, "@disposition", query.Disposition.Value.ToString());
                if (query.IsEvaluation.HasValue) MysqlCommandHelper.Add(cmd, "@is_evaluation", query.IsEvaluation.Value);
                if (query.FromUtc.HasValue) MysqlCommandHelper.AddDate(cmd, "@from_utc", query.FromUtc.Value);
                if (query.ToUtc.HasValue) MysqlCommandHelper.AddDate(cmd, "@to_utc", query.ToUtc.Value);
            };

            string where = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                long total = await MysqlCommandHelper.CountAsync(conn, "SELECT COUNT(*) FROM stage_outcomes" + where + ";", bind, token).ConfigureAwait(false);
                List<StageOutcome> rows = await MysqlCommandHelper.QueryAsync(conn,
                    "SELECT * FROM stage_outcomes" + where + " ORDER BY ended_utc DESC, id DESC" + MysqlCommandHelper.Page(offset, pageSize) + ";",
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
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                long count = await MysqlCommandHelper.CountAsync(conn, "SELECT COUNT(*) FROM stage_outcomes WHERE mission_id = @mission_id;",
                    cmd => MysqlCommandHelper.Add(cmd, "@mission_id", missionId), token).ConfigureAwait(false);
                return (int)count;
            }
        }

        /// <inheritdoc />
        public async Task<int> DeleteByMissionAsync(string missionId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));
            int deleted = 0;
            await MysqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (MySqlConnection conn, MySqlTransaction tx) =>
            {
                deleted = await MysqlCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM stage_outcomes WHERE mission_id = @mission_id;",
                    cmd => MysqlCommandHelper.Add(cmd, "@mission_id", missionId), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return deleted;
        }

        #endregion

        #region Private-Methods

        private static void Bind(MySqlCommand cmd, StageOutcome outcome, bool includeCreateColumns)
        {
            MysqlCommandHelper.Add(cmd, "@id", outcome.Id);
            if (includeCreateColumns)
            {
                MysqlCommandHelper.Add(cmd, "@tenant_id", outcome.TenantId);
                MysqlCommandHelper.AddDate(cmd, "@created_utc", outcome.CreatedUtc);
            }

            MysqlCommandHelper.Add(cmd, "@user_id", outcome.UserId);
            MysqlCommandHelper.Add(cmd, "@mission_id", outcome.MissionId);
            MysqlCommandHelper.Add(cmd, "@voyage_id", outcome.VoyageId);
            MysqlCommandHelper.Add(cmd, "@vessel_id", outcome.VesselId);
            MysqlCommandHelper.Add(cmd, "@captain_id", outcome.CaptainId);
            MysqlCommandHelper.Add(cmd, "@captain_name", outcome.CaptainName);
            MysqlCommandHelper.Add(cmd, "@persona", outcome.Persona);
            MysqlCommandHelper.Add(cmd, "@attempt", outcome.Attempt);
            MysqlCommandHelper.Add(cmd, "@rework_round", outcome.ReworkRound);
            MysqlCommandHelper.Add(cmd, "@runtime", EnumName(outcome.Runtime));
            MysqlCommandHelper.Add(cmd, "@model", outcome.Model);
            MysqlCommandHelper.Add(cmd, "@model_provider", outcome.ModelProvider);
            MysqlCommandHelper.Add(cmd, "@model_endpoint_id", outcome.ModelEndpointId);
            MysqlCommandHelper.Add(cmd, "@harbor_id", outcome.HarborId);
            MysqlCommandHelper.Add(cmd, "@context_tokens", outcome.ContextTokens);
            MysqlCommandHelper.Add(cmd, "@temperature", outcome.Temperature);
            MysqlCommandHelper.Add(cmd, "@top_p", outcome.TopP);
            MysqlCommandHelper.Add(cmd, "@tools_enabled", outcome.ToolsEnabled);
            MysqlCommandHelper.Add(cmd, "@mcp_tools_enabled", outcome.McpToolsEnabled);
            MysqlCommandHelper.Add(cmd, "@reasoning_effort", outcome.ReasoningEffort);
            MysqlCommandHelper.Add(cmd, "@runtime_version", outcome.RuntimeVersion);
            MysqlCommandHelper.Add(cmd, "@model_digest", outcome.ModelDigest);
            MysqlCommandHelper.Add(cmd, "@prompt_version", outcome.PromptVersion);
            MysqlCommandHelper.Add(cmd, "@armada_version", outcome.ArmadaVersion);
            MysqlCommandHelper.Add(cmd, "@exit_code", outcome.ExitCode);
            MysqlCommandHelper.Add(cmd, "@runtime_failure_kind", EnumName(outcome.RuntimeFailureKind));
            MysqlCommandHelper.Add(cmd, "@stage_check_status", EnumName(outcome.StageCheckStatus));
            MysqlCommandHelper.Add(cmd, "@stage_check_reason", EnumName(outcome.StageCheckReason));
            MysqlCommandHelper.Add(cmd, "@disposition", outcome.Disposition.ToString());
            MysqlCommandHelper.Add(cmd, "@failure_kind", EnumName(outcome.FailureKind));
            MysqlCommandHelper.Add(cmd, "@failure_reason", outcome.FailureReason);
            MysqlCommandHelper.Add(cmd, "@judge_verdict", outcome.JudgeVerdict);
            MysqlCommandHelper.Add(cmd, "@reviewed_mission_id", outcome.ReviewedMissionId);
            MysqlCommandHelper.Add(cmd, "@next_captain_id", outcome.NextCaptainId);
            MysqlCommandHelper.Add(cmd, "@commit_hash", outcome.CommitHash);
            MysqlCommandHelper.Add(cmd, "@finished_by_outcome_id", outcome.FinishedByOutcomeId);
            MysqlCommandHelper.Add(cmd, "@finished_by_captain_id", outcome.FinishedByCaptainId);
            MysqlCommandHelper.Add(cmd, "@prior_work_kept", outcome.PriorWorkKept);
            MysqlCommandHelper.Add(cmd, "@contract_repair_kind", EnumName(outcome.ContractRepairKind));
            MysqlCommandHelper.Add(cmd, "@suspected_cause", outcome.SuspectedCause.ToString());
            MysqlCommandHelper.Add(cmd, "@cause_source", outcome.CauseSource.ToString());
            MysqlCommandHelper.Add(cmd, "@cause_rule_id", outcome.CauseRuleId);
            MysqlCommandHelper.Add(cmd, "@cause_note", outcome.CauseNote);
            MysqlCommandHelper.Add(cmd, "@classified_by_user_id", outcome.ClassifiedByUserId);
            MysqlCommandHelper.AddDate(cmd, "@classified_utc", outcome.ClassifiedUtc);
            MysqlCommandHelper.Add(cmd, "@is_evaluation", outcome.IsEvaluation);
            MysqlCommandHelper.AddDate(cmd, "@started_utc", outcome.StartedUtc);
            MysqlCommandHelper.AddDate(cmd, "@ended_utc", outcome.EndedUtc);
            MysqlCommandHelper.Add(cmd, "@duration_ms", outcome.DurationMs);
            MysqlCommandHelper.AddDate(cmd, "@last_update_utc", outcome.LastUpdateUtc);
        }

        private static StageOutcome FromReader(MySqlDataReader reader)
        {
            StageOutcome outcome = new StageOutcome();
            outcome.Id = reader["id"].ToString()!;
            outcome.TenantId = MysqlCommandHelper.ReadString(reader["tenant_id"]);
            outcome.UserId = MysqlCommandHelper.ReadString(reader["user_id"]);
            outcome.MissionId = reader["mission_id"]?.ToString() ?? String.Empty;
            outcome.VoyageId = MysqlCommandHelper.ReadString(reader["voyage_id"]);
            outcome.VesselId = MysqlCommandHelper.ReadString(reader["vessel_id"]);
            outcome.CaptainId = MysqlCommandHelper.ReadString(reader["captain_id"]);
            outcome.CaptainName = MysqlCommandHelper.ReadString(reader["captain_name"]);
            outcome.Persona = MysqlCommandHelper.ReadString(reader["persona"]);
            outcome.Attempt = MysqlCommandHelper.ReadInt(reader["attempt"], 1);
            outcome.ReworkRound = MysqlCommandHelper.ReadInt(reader["rework_round"], 0);
            outcome.Runtime = ReadNullableEnum<AgentRuntimeEnum>(reader["runtime"]);
            outcome.Model = MysqlCommandHelper.ReadString(reader["model"]);
            outcome.ModelProvider = MysqlCommandHelper.ReadString(reader["model_provider"]);
            outcome.ModelEndpointId = MysqlCommandHelper.ReadString(reader["model_endpoint_id"]);
            outcome.HarborId = MysqlCommandHelper.ReadString(reader["harbor_id"]);
            outcome.ContextTokens = MysqlCommandHelper.ReadNullableInt(reader["context_tokens"]);
            outcome.Temperature = MysqlCommandHelper.ReadNullableDouble(reader["temperature"]);
            outcome.TopP = MysqlCommandHelper.ReadNullableDouble(reader["top_p"]);
            outcome.ToolsEnabled = MysqlCommandHelper.ReadNullableBool(reader["tools_enabled"]);
            outcome.McpToolsEnabled = MysqlCommandHelper.ReadNullableBool(reader["mcp_tools_enabled"]);
            outcome.ReasoningEffort = MysqlCommandHelper.ReadString(reader["reasoning_effort"]);
            outcome.RuntimeVersion = MysqlCommandHelper.ReadString(reader["runtime_version"]);
            outcome.ModelDigest = MysqlCommandHelper.ReadString(reader["model_digest"]);
            outcome.PromptVersion = MysqlCommandHelper.ReadString(reader["prompt_version"]);
            outcome.ArmadaVersion = MysqlCommandHelper.ReadString(reader["armada_version"]);
            outcome.ExitCode = MysqlCommandHelper.ReadNullableInt(reader["exit_code"]);
            outcome.RuntimeFailureKind = ReadNullableEnum<RuntimeFailureKindEnum>(reader["runtime_failure_kind"]);
            outcome.StageCheckStatus = ReadNullableEnum<StageCheckStatusEnum>(reader["stage_check_status"]);
            outcome.StageCheckReason = ReadNullableEnum<StageCheckReasonEnum>(reader["stage_check_reason"]);
            outcome.Disposition = MysqlCommandHelper.ReadEnum(reader["disposition"], StageDispositionEnum.Pending);
            outcome.FailureKind = ReadNullableEnum<MissionFailureKindEnum>(reader["failure_kind"]);
            outcome.FailureReason = MysqlCommandHelper.ReadString(reader["failure_reason"]);
            outcome.JudgeVerdict = MysqlCommandHelper.ReadString(reader["judge_verdict"]);
            outcome.ReviewedMissionId = MysqlCommandHelper.ReadString(reader["reviewed_mission_id"]);
            outcome.NextCaptainId = MysqlCommandHelper.ReadString(reader["next_captain_id"]);
            outcome.CommitHash = MysqlCommandHelper.ReadString(reader["commit_hash"]);
            outcome.FinishedByOutcomeId = MysqlCommandHelper.ReadString(reader["finished_by_outcome_id"]);
            outcome.FinishedByCaptainId = MysqlCommandHelper.ReadString(reader["finished_by_captain_id"]);
            outcome.PriorWorkKept = MysqlCommandHelper.ReadNullableBool(reader["prior_work_kept"]);
            outcome.ContractRepairKind = ReadNullableEnum<ContractRepairKindEnum>(reader["contract_repair_kind"]);
            outcome.SuspectedCause = MysqlCommandHelper.ReadEnum(reader["suspected_cause"], FailureCauseEnum.None);
            outcome.CauseSource = MysqlCommandHelper.ReadEnum(reader["cause_source"], FailureCauseSourceEnum.None);
            outcome.CauseRuleId = MysqlCommandHelper.ReadString(reader["cause_rule_id"]);
            outcome.CauseNote = MysqlCommandHelper.ReadString(reader["cause_note"]);
            outcome.ClassifiedByUserId = MysqlCommandHelper.ReadString(reader["classified_by_user_id"]);
            outcome.ClassifiedUtc = MysqlCommandHelper.ReadNullableDate(reader["classified_utc"]);
            outcome.IsEvaluation = MysqlCommandHelper.ReadBool(reader["is_evaluation"], false);
            outcome.StartedUtc = MysqlCommandHelper.ReadNullableDate(reader["started_utc"]);
            outcome.EndedUtc = MysqlCommandHelper.ReadDate(reader["ended_utc"]);
            outcome.DurationMs = MysqlCommandHelper.ReadNullableLong(reader["duration_ms"]);
            outcome.CreatedUtc = MysqlCommandHelper.ReadDate(reader["created_utc"]);
            outcome.LastUpdateUtc = MysqlCommandHelper.ReadDate(reader["last_update_utc"]);
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
