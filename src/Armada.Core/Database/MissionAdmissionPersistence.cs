namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Globalization;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>Bounded historical observations and conditional writes independent of admission policy.</summary>
    internal static class MissionAdmissionPersistence
    {
        private const int _MaximumBytes = 65536;

        internal static void Read(IDataRecord reader, Mission mission)
        {
            mission.AdmissionWriteRevision = Convert.ToInt64(reader["admission_revision"], CultureInfo.InvariantCulture);
            mission.AdmissionWriteStamp = reader["last_update_utc"];
            mission.AdmissionWriteJson = reader["last_admission_json"] == DBNull.Value ? null : (string)reader["last_admission_json"];
            string? json = mission.AdmissionWriteJson;
            if (json == null || Encoding.UTF8.GetByteCount(json) > _MaximumBytes) return;
            try
            {
                MissionAdmissionObservation? observation = JsonSerializer.Deserialize<MissionAdmissionObservation>(json);
                if (observation != null && IsConsistent(observation) && observation.MissionId == (string)reader["id"]
                    && observation.TenantId == NullableText(reader["tenant_id"])
                    && observation.UserId == NullableText(reader["user_id"])
                    && observation.VesselId == NullableText(reader["vessel_id"]))
                    {
                    Redact(observation);
                    mission.LastAdmissionObservation = observation;
                }
            }
            catch (JsonException) { return; }
        }

        internal static async Task<bool> RecordAsync(DbConnection connection, DatabaseTypeEnum provider,
            Mission expected, MissionAdmissionObservation observation, CancellationToken token)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (expected.AdmissionWriteStamp == null)
                throw new InvalidOperationException("Admission evidence requires a loaded mission snapshot.");
            if (observation.Version != 1 || String.IsNullOrEmpty(observation.ObservationId)
                || observation.ObservationId.Length > 64 || observation.MissionId != expected.Id
                || observation.TenantId != expected.TenantId || observation.UserId != expected.UserId
                || observation.VesselId != expected.VesselId || observation.ObservedUtc.Kind != DateTimeKind.Utc
                || observation.GlobalActiveWorkloads < 0 || observation.GlobalWorkloadLimit < 0)
                throw new InvalidOperationException("Admission evidence does not match the evaluated mission.");
            if (!IsConsistent(observation))
                throw new InvalidOperationException("Admission evidence has contradictory outcomes.");
            // Clone before redaction so the policy's decision object remains unchanged.
            MissionAdmissionObservation safe = JsonSerializer.Deserialize<MissionAdmissionObservation>(JsonSerializer.Serialize(observation))!;
            Redact(safe);
            string json = JsonSerializer.Serialize(safe);
            if (Encoding.UTF8.GetByteCount(json) > _MaximumBytes)
                throw new InvalidOperationException("Admission evidence exceeds its storage limit.");
            if (expected.Status != MissionStatusEnum.Pending) return false;
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE missions SET admission_revision=admission_revision+1, last_admission_json=@observation, mission_assignment_state=@next_assignment, last_update_utc=@now WHERE id=@id AND status=@status"
                    + " AND admission_revision=@revision AND mission_assignment_state=@assignment AND last_update_utc=@stamp AND "
                    + EqualsSql("last_admission_json", "@previous", provider);
                foreach (string name in new[] { "tenant_id", "user_id", "vessel_id", "captain_id", "dock_id", "voyage_id", "branch_name" })
                    command.CommandText += " AND " + EqualsSql(name, "@" + name, provider);
                command.CommandText += " AND ((process_id IS NULL AND @process IS NULL) OR process_id=@process);";
                Add(command, "@revision", expected.AdmissionWriteRevision, DbType.Int64);
                Add(command, "@id", expected.Id, DbType.String);
                Add(command, "@status", expected.Status.ToString(), DbType.String);
                Add(command, "@assignment", expected.AssignmentState.ToString(), DbType.String);
                Add(command, "@next_assignment", (observation.Admit ? expected.AssignmentState
                    : MissionAssignmentStateEnum.WaitingForResourcePressure).ToString(), DbType.String);
                Add(command, "@stamp", expected.AdmissionWriteStamp);
                StoredValueBinder.For(provider).For(command, "missions").Utc("@now", "last_update_utc", DateTime.UtcNow);
                Add(command, "@previous", expected.AdmissionWriteJson, DbType.String);
                Add(command, "@observation", json, DbType.String);
                Add(command, "@tenant_id", expected.TenantId, DbType.String);
                Add(command, "@user_id", expected.UserId, DbType.String);
                Add(command, "@vessel_id", expected.VesselId, DbType.String);
                Add(command, "@captain_id", expected.CaptainId, DbType.String);
                Add(command, "@dock_id", expected.DockId, DbType.String);
                Add(command, "@voyage_id", expected.VoyageId, DbType.String);
                Add(command, "@branch_name", expected.BranchName, DbType.String);
                Add(command, "@process", expected.ProcessId, DbType.Int32);
                await connection.OpenAsync(token).ConfigureAwait(false);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            }
        }

        internal static string PreserveOwnerSql(DatabaseTypeEnum provider)
        {
            return "admission_revision = admission_revision + 1, last_admission_json = CASE WHEN " + EqualsSql("tenant_id", "@tenant_id", provider)
                + " AND " + EqualsSql("user_id", "@user_id", provider)
                + " AND " + EqualsSql("vessel_id", "@vessel_id", provider)
                + " THEN last_admission_json ELSE NULL END";
        }

        private static string EqualsSql(string column, string parameter, DatabaseTypeEnum provider)
        {
            if (provider == DatabaseTypeEnum.Postgresql) parameter = "CAST(" + parameter + " AS text)";
            string comparison = provider switch
            {
                DatabaseTypeEnum.Sqlite => "CAST(" + column + " AS BLOB)=CAST(" + parameter + " AS BLOB)",
                DatabaseTypeEnum.Postgresql => "convert_to(" + column + ", 'UTF8')=convert_to(" + parameter + ", 'UTF8')",
                DatabaseTypeEnum.Mysql => "BINARY " + column + "=BINARY " + parameter,
                DatabaseTypeEnum.SqlServer => "CONVERT(varbinary(max)," + column + ")=CONVERT(varbinary(max)," + parameter + ")",
                _ => throw new NotSupportedException()
            };
            return "((" + column + " IS NULL AND " + parameter + " IS NULL) OR " + comparison + ")";
        }

        private static bool IsConsistent(MissionAdmissionObservation observation)
        {
            bool limitReached = observation.GlobalWorkloadLimit > 0
                && observation.GlobalActiveWorkloads >= observation.GlobalWorkloadLimit;
            return observation.Version == 1 && !String.IsNullOrEmpty(observation.ObservationId)
                && observation.ObservationId.Length <= 64 && observation.ObservedUtc.Kind == DateTimeKind.Utc
                && observation.GlobalActiveWorkloads >= 0 && observation.GlobalWorkloadLimit >= 0
                && observation.GlobalLimitReached == limitReached
                && (limitReached ? !observation.Admit && observation.PressureDecision == null
                    : observation.PressureDecision != null && observation.PressureDecision.Admit == observation.Admit);
        }

        private static void Redact(MissionAdmissionObservation observation)
        {
            observation.Reason = BoundedReason(observation.Reason);
            if (observation.PressureDecision != null)
                observation.PressureDecision.Reason = BoundedReason(observation.PressureDecision.Reason);
        }

        private static string BoundedReason(string? reason)
        {
            string safe = SecretRedactor.Redact(reason ?? String.Empty);
            return safe.Length <= 1000 ? safe : safe.Substring(0, 1000) + " [truncated]";
        }

        private static string? NullableText(object value) => value == DBNull.Value ? null : (string)value;

        private static void Add(DbCommand command, string name, object? value, DbType? type = null)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            if (type.HasValue) parameter.DbType = type.Value;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
