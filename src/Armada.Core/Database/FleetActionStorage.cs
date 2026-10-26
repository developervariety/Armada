namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Shared validation, serialization, binding, and mapping for fleet action stores.</summary>
    internal static partial class FleetActionStorage
    {
        internal const int MaximumJsonBytes = 2 * 1024 * 1024;
        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions();

        internal static void ValidateOwner(string tenantId, string userId)
        {
            if (String.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 128 || String.IsNullOrWhiteSpace(userId) || userId.Length > 128) throw new ArgumentException("Tenant and user identifiers are required and bounded.");
        }

        internal static void ValidateAction(FleetAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            ValidateOwner(action.TenantId, action.UserId);
            if (String.IsNullOrWhiteSpace(action.Id) || action.Id.Length > 128 || !action.Id.StartsWith("fac_", StringComparison.Ordinal) || String.IsNullOrWhiteSpace(action.Name) || action.Name.Length > 255) throw new ArgumentException("Action identifier and name are required and bounded.", nameof(action));
            if (action.PipelineId?.Length > 128 || action.Kind != FleetActionKindEnum.Mission || action.Concurrency < 1 || action.Concurrency > 8 || String.IsNullOrWhiteSpace(action.PromptTemplate) || action.PromptTemplate.Length > 16000) throw new ArgumentException("Action kind, concurrency, pipeline, or prompt is invalid.", nameof(action));
        }

        internal static void ValidateRun(FleetActionRun run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            ValidateOwner(run.TenantId, run.UserId);
            if (String.IsNullOrWhiteSpace(run.Id) || run.Id.Length > 128 || !run.Id.StartsWith("far_", StringComparison.Ordinal) || String.IsNullOrWhiteSpace(run.ActionId) || run.ActionId.Length > 128 || !run.ActionId.StartsWith("fac_", StringComparison.Ordinal) || String.IsNullOrWhiteSpace(run.RequestId) || run.RequestId.Length > 128 || run.Revision < 0) throw new ArgumentException("Run identifiers and revision are invalid.", nameof(run));
            if (String.IsNullOrEmpty(run.RequestFingerprint) || run.RequestFingerprint.Length != 64 || !IsHex(run.RequestFingerprint)) throw new ArgumentException("Request fingerprint must be a SHA-256 hexadecimal value.", nameof(run));
            if (run.Snapshot == null || run.State == null || run.Snapshot.Targets == null || run.State.TargetStates == null || run.Snapshot.Targets.Count < 1 || run.Snapshot.Targets.Count > 50) throw new ArgumentException("A run requires one through fifty target snapshots and states.", nameof(run));
            if (String.IsNullOrWhiteSpace(run.Snapshot.ActionName) || run.Snapshot.ActionName.Length > 255 || String.IsNullOrWhiteSpace(run.Snapshot.PromptTemplate) || run.Snapshot.ActionKind != FleetActionKindEnum.Mission || run.Snapshot.Concurrency < 1 || run.Snapshot.Concurrency > 8 || run.Snapshot.PromptTemplate.Length > 16000 || run.Snapshot.PipelineId?.Length > 128 || run.State.Status != FleetActionRunStatusEnum.Pending || run.State.StartedUtc != null || run.State.CompletedUtc != null) throw new ArgumentException("A new run must have a valid pending snapshot and state.", nameof(run));
            HashSet<string> targetIds = new HashSet<string>(StringComparer.Ordinal); HashSet<string> stateIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (FleetActionRunTarget target in run.Snapshot.Targets) if (target == null || String.IsNullOrWhiteSpace(target.TargetId) || target.TargetId.Length > 128 || !target.TargetId.StartsWith("fat_", StringComparison.Ordinal) || String.IsNullOrWhiteSpace(target.VesselId) || target.VesselId.Length > 128 || String.IsNullOrEmpty(target.RenderedPrompt) || target.RenderedPrompt.Length > 16000 || target.VesselTenantIdSnapshot?.Length > 128 || target.VesselUserIdSnapshot?.Length > 128 || target.VesselNameSnapshot?.Length > 255 || target.ResolvedPipelineId?.Length > 128 || target.ResolvedPipelineHash?.Length > 128 || !targetIds.Add(target.TargetId)) throw new ArgumentException("Run target snapshot is invalid or duplicated.", nameof(run));
            foreach (FleetActionRunTargetState state in run.State.TargetStates) if (state == null || !targetIds.Contains(state.TargetId) || !stateIds.Add(state.TargetId) || state.Status != FleetActionTargetStatusEnum.Pending || state.VoyageId != null || state.CancelRequested || state.DispatchStartedUtc != null || state.StartedUtc != null || state.CompletedUtc != null || state.Reason != null || state.Error != null) throw new ArgumentException("A new run must have only pending target states.", nameof(run));
            if (stateIds.Count != targetIds.Count) throw new ArgumentException("Run state must include every target exactly once.", nameof(run));
            EnsureJsonSize(JsonSerializer.Serialize(run.Snapshot, JsonOptions)); EnsureJsonSize(JsonSerializer.Serialize(run.State, JsonOptions));
        }

        internal static void EnsureJsonSize(string json)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new ArgumentException("Fleet action JSON cannot exceed two megabytes.");
        }

        internal static void ValidateState(FleetActionRunState state, FleetActionRunSnapshot snapshot)
        {
            if (!Enum.IsDefined(state.Status) || state.TargetStates == null || state.TargetStates.Count != snapshot.Targets.Count) throw new ArgumentException("Fleet action run state is invalid.", nameof(state));
            HashSet<string> expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (FleetActionRunTarget target in snapshot.Targets) expected.Add(target.TargetId);
            HashSet<string> actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (FleetActionRunTargetState targetState in state.TargetStates)
            {
                if (targetState == null || !expected.Contains(targetState.TargetId) || !actual.Add(targetState.TargetId) || !Enum.IsDefined(targetState.Status) || targetState.VoyageId?.Length > 128 || targetState.Reason?.Length > 2000 || targetState.Error?.Length > 2000) throw new ArgumentException("Fleet action target state is invalid.", nameof(state));
            }
            EnsureJsonSize(JsonSerializer.Serialize(state, JsonOptions));
        }

        internal static bool IsHex(string value)
        {
            foreach (char character in value) if (!Uri.IsHexDigit(character)) return false;
            return true;
        }

        internal static void Add(DbCommand command, string name, object? value)
        {
            DbParameter parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
        }

        internal static void AddTime(DbCommand command, DatabaseTypeEnum provider, string table, string column, DateTime? value)
        {
            StoredValueBinder.For(provider).AddTimestamp(command, "@" + column, table, column, value);
        }

        internal static string ReadText(DbDataReader reader, int ordinal) => Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? String.Empty;

        internal static FleetAction MapAction(DbDataReader reader)
        {
            return new FleetAction { Id = ReadText(reader, 0), TenantId = ReadText(reader, 1), UserId = ReadText(reader, 2), Name = ReadText(reader, 3), Active = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture), Kind = Enum.Parse<FleetActionKindEnum>(ReadText(reader, 5)), PromptTemplate = ReadText(reader, 6), PipelineId = ProductionFactSql.ReadText(reader.GetValue(7)), Concurrency = Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture), CreatedUtc = ProductionFactSql.ReadUtc(reader.GetValue(9)), LastUpdateUtc = ProductionFactSql.ReadUtc(reader.GetValue(10)) };
        }

        internal static FleetActionRun MapRun(DbDataReader reader)
        {
            FleetActionRunSnapshot snapshot = JsonSerializer.Deserialize<FleetActionRunSnapshot>(ReadText(reader, 8), JsonOptions) ?? throw new InvalidOperationException("Fleet action run snapshot is missing.");
            FleetActionRunState state = JsonSerializer.Deserialize<FleetActionRunState>(ReadText(reader, 9), JsonOptions) ?? throw new InvalidOperationException("Fleet action run state is missing.");
            FleetActionRun run = new FleetActionRun { Id = ReadText(reader, 0), TenantId = ReadText(reader, 1), UserId = ReadText(reader, 2), ActionId = ReadText(reader, 3), RequestId = ReadText(reader, 4), RequestFingerprint = ReadText(reader, 5), Revision = Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture), Snapshot = snapshot, State = state, CreatedUtc = ProductionFactSql.ReadUtc(reader.GetValue(10)), LastUpdateUtc = ProductionFactSql.ReadUtc(reader.GetValue(11)) };
            if (!String.Equals(ReadText(reader, 6), run.State.Status.ToString(), StringComparison.Ordinal)) throw new InvalidOperationException("Fleet action run state index does not match its JSON state.");
            return run;
        }
    }
}
