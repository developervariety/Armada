namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.Globalization;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Server-owned automatic-work permission with conditional, atomically audited writes.</summary>
    internal static class MissionAutomaticWorkPermissionPersistence
    {
        internal const string JsonColumn = "automatic_work_permission_json";
        internal const string RevisionColumn = "automatic_work_permission_revision";
        private const int _MaximumBytes = 65536;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        internal static void Read(IDataRecord record, Mission mission)
        {
            mission.AutomaticWorkPermissionWriteRevision = Convert.ToInt64(record[RevisionColumn], CultureInfo.InvariantCulture);
            mission.AutomaticWorkPermissionWriteStamp = record["last_update_utc"];
            mission.AutomaticWorkPermissionRawJson = record[JsonColumn] == DBNull.Value ? null : (string)record[JsonColumn];
            mission.AutomaticWorkPermission = null;
            mission.AutomaticWorkPermissionInvalid = false;
            if (mission.AutomaticWorkPermissionRawJson == null) return;
            mission.AutomaticWorkPermission = Parse(mission.AutomaticWorkPermissionRawJson);
            mission.AutomaticWorkPermissionInvalid = mission.AutomaticWorkPermission == null;
        }

        internal static bool SameBinding(Mission first, Mission second) =>
            first.AutomaticWorkPermissionWriteRevision == second.AutomaticWorkPermissionWriteRevision
            && String.Equals(first.AutomaticWorkPermissionRawJson, second.AutomaticWorkPermissionRawJson, StringComparison.Ordinal);

        internal static string UnchangedSql(DatabaseTypeEnum provider) =>
            RevisionColumn + "=@automatic_permission_revision AND " + EqualSql(JsonColumn, "@automatic_permission_raw", provider);

        internal static void BindExpected(StoredParameters parameters, Mission mission)
        {
            parameters.Text("@automatic_permission_raw", JsonColumn, mission.AutomaticWorkPermissionRawJson)
                .Long("@automatic_permission_revision", RevisionColumn, mission.AutomaticWorkPermissionWriteRevision);
        }

        internal static async Task<bool> TryWriteAsync(DbConnection connection, DatabaseTypeEnum provider,
            Mission expected, MissionAutomaticWorkPermission next, ArmadaEvent audit, CancellationToken token)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (audit == null) throw new ArgumentNullException(nameof(audit));
            if (expected.AutomaticWorkPermissionWriteStamp == null)
                throw new InvalidOperationException("automatic_work_permission_loaded_snapshot_required");
            string json = JsonSerializer.Serialize(next, _Json);
            if (Parse(json) == null) throw new ArgumentException("automatic_work_permission_invalid", nameof(next));
            MissionAutomaticWorkPermission? previous = expected.AutomaticWorkPermissionRawJson == null
                ? null : Parse(expected.AutomaticWorkPermissionRawJson);
            if (!ChangedGrantMatches(expected, previous?.Verification, next.Verification)
                || !ChangedGrantMatches(expected, previous?.DependentAdmission, next.DependentAdmission))
                throw new ArgumentException("automatic_work_permission_grant_binding_differs", nameof(next));
            ValidateAudit(expected, audit);

            await connection.OpenAsync(token).ConfigureAwait(false);
            using (IDisposable? writeLease = provider == DatabaseTypeEnum.Sqlite
                ? await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false) : null)
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                try
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "UPDATE missions SET " + JsonColumn + "=@next, " + RevisionColumn
                            + "=" + RevisionColumn + "+1, last_update_utc=@now WHERE id=@id AND status=@status AND last_update_utc=@stamp AND "
                            + UnchangedSql(provider) + " AND " + EqualSql("tenant_id", "@tenant", provider)
                            + " AND " + EqualSql("user_id", "@user", provider)
                            + " AND " + EqualSql("commit_hash", "@commit", provider)
                            + " AND ((started_utc IS NULL AND @started IS NULL) OR started_utc=@started);";
                        StoredParameters parameters = StoredValueBinder.For(provider).For(command, "missions");
                        parameters.Text("@next", JsonColumn, json).Text("@id", "id", expected.Id)
                            .Text("@status", "status", expected.Status.ToString())
                            .Text("@tenant", "tenant_id", expected.TenantId).Text("@user", "user_id", expected.UserId)
                            .Text("@commit", "commit_hash", expected.CommitHash).Utc("@started", "started_utc", expected.StartedUtc)
                            .Utc("@now", "last_update_utc", DateTime.UtcNow);
                        BindExpected(parameters, expected);
                        DbParameter stamp = command.CreateParameter();
                        stamp.ParameterName = "@stamp";
                        stamp.Value = expected.AutomaticWorkPermissionWriteStamp;
                        command.Parameters.Add(stamp);
                        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                        {
                            await transaction.RollbackAsync(token).ConfigureAwait(false);
                            return false;
                        }
                    }

                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO events (id, tenant_id, user_id, event_type, entity_type, entity_id, captain_id, mission_id, vessel_id, voyage_id, message, payload, created_utc) "
                            + "VALUES (@id, @tenant_id, @user_id, @event_type, @entity_type, @entity_id, @captain_id, @mission_id, @vessel_id, @voyage_id, @message, @payload, @created_utc);";
                        EventColumns.Write(StoredValueBinder.For(provider).For(command, "events"), audit);
                        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                            throw new InvalidOperationException("automatic_work_permission_audit_not_written");
                    }
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return true;
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
        }

        private static void ValidateAudit(Mission mission, ArmadaEvent audit)
        {
            if (String.IsNullOrWhiteSpace(audit.Id) || String.IsNullOrWhiteSpace(audit.EventType)
                || String.IsNullOrWhiteSpace(audit.Message) || audit.CreatedUtc.Kind != DateTimeKind.Utc
                || audit.EntityType != "mission" || audit.EntityId != mission.Id || audit.MissionId != mission.Id
                || audit.TenantId != mission.TenantId || audit.UserId != mission.UserId
                || audit.VesselId != mission.VesselId || audit.VoyageId != mission.VoyageId)
                throw new ArgumentException("automatic_work_permission_audit_binding_differs", nameof(audit));
        }

        private static bool ChangedGrantMatches(Mission mission, MissionAutomaticWorkGrant? previous, MissionAutomaticWorkGrant? next)
        {
            if (next == null || JsonSerializer.Serialize(previous) == JsonSerializer.Serialize(next)) return true;
            return next.MissionId == mission.Id && next.TenantId == mission.TenantId && next.UserId == mission.UserId
                && mission.StartedUtc.HasValue && next.StartedUtc == mission.StartedUtc.Value
                && next.CommitHash == mission.CommitHash;
        }

        private static MissionAutomaticWorkPermission? Parse(string raw)
        {
            if (Encoding.UTF8.GetByteCount(raw) > _MaximumBytes) return null;
            try
            {
                Utf8JsonReader reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(raw));
                Stack<HashSet<string>> objects = new Stack<HashSet<string>>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new HashSet<string>(StringComparer.Ordinal));
                    else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                    else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!)) return null;
                }
                MissionAutomaticWorkPermission? result = JsonSerializer.Deserialize<MissionAutomaticWorkPermission>(raw, _Json);
                return result != null && result.Version == 1 && ValidGrant(result.Verification) && ValidGrant(result.DependentAdmission) ? result : null;
            }
            catch (JsonException) { return null; }
        }

        private static bool ValidGrant(MissionAutomaticWorkGrant? grant)
        {
            if (grant == null) return true;
            if (String.IsNullOrWhiteSpace(grant.MissionId) || String.IsNullOrWhiteSpace(grant.GrantedByUserId)
                || grant.StartedUtc.Kind != DateTimeKind.Utc || grant.GrantedUtc.Kind != DateTimeKind.Utc
                || grant.ExpiresUtc.Kind != DateTimeKind.Utc || grant.ExpiresUtc <= grant.GrantedUtc
                || grant.CommitHash == null || grant.CommitHash.Length != 40) return false;
            foreach (char character in grant.CommitHash)
                if ((character < '0' || character > '9') && (character < 'a' || character > 'f')) return false;
            return true;
        }

        internal static string EqualSql(string column, string parameter, DatabaseTypeEnum provider)
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
    }
}
