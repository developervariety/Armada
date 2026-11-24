namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Microsoft.Data.Sqlite;

    /// <summary>Serializable prior-outcome archive and administrative correction, with no mission reset.</summary>
    public sealed partial class AdministrativeVoyageRecoveryMethods : IAdministrativeVoyageRecoveryMethods
    {
        private readonly StoredDialect _Sql;
        internal AdministrativeVoyageRecoveryMethods(StoredDialect sql) { _Sql = sql ?? throw new ArgumentNullException(nameof(sql)); }

        /// <inheritdoc />
        public async Task<AdministrativeVoyageRecoveryPlan> ExecuteAsync(string tenantId, string userId, string voyageId,
            AdministrativeVoyageRecoveryRequest request, CancellationToken token = default)
        {
            using (DbConnection connection = await _Sql.OpenAsync(token).ConfigureAwait(false))
            using (DbTransaction transaction = _Sql.Provider == DatabaseTypeEnum.Sqlite && connection is SqliteConnection sqlite
                ? sqlite.BeginTransaction(deferred: false)
                : await connection.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false))
            {
                CapturedState captured = await CaptureAsync(connection, transaction, tenantId, userId, voyageId, request, true, token).ConfigureAwait(false);
                Voyage voyage = captured.Voyage;
                string json = captured.Json;
                AdministrativeVoyageRecoveryPlan plan = captured.Plan;
                if (request.HistoricalWork != null)
                {
                    if (!request.CapturedHistoricalEvidence || request.HistoricalWork.Kind != HistoricalWorkDispositionKindEnum.Unaccepted
                        || !String.Equals(request.HistoricalWork.GraphFingerprint, plan.Fingerprint, StringComparison.Ordinal))
                        plan.Blockers.Add("exact_owner_historical_work_disposition_required");
                }
                if (request.DryRun) { await transaction.RollbackAsync(token).ConfigureAwait(false); return plan; }
                if (plan.Blockers.Count > 0 || !String.Equals(plan.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("Administrative completion correction is unproved or its preview is stale; nothing changed.");
                DateTime now = DateTime.UtcNow;
                ArmadaEvent audit = new ArmadaEvent("voyage.administrative_completion_corrected",
                    "Planning-only administrative completion corrected to Failed; no mission restarted or accepted.")
                { TenantId = tenantId, UserId = userId, EntityType = "voyage", EntityId = voyageId, VoyageId = voyageId,
                    Payload = JsonSerializer.Serialize(new { PriorSnapshot = json, PriorFingerprint = plan.Fingerprint,
                        OwnerDisposition = request.HistoricalWork }), CreatedUtc = now };
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO events (id,tenant_id,user_id,event_type,entity_type,entity_id,voyage_id,message,payload,created_utc) VALUES (@event,@tenant,@user,@type,'voyage',@id,@id,@message,@payload,@created);";
                    Add(command, "@event", audit.Id); Add(command, "@tenant", tenantId); Add(command, "@user", userId);
                    Add(command, "@type", audit.EventType); Add(command, "@id", voyageId);
                    Add(command, "@message", audit.Message); Add(command, "@payload", audit.Payload);
                    _Sql.Binder.AddTimestamp(command, "@created", "events", "created_utc", now);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                using (DbCommand command = Command(connection, transaction,
                    "UPDATE voyages SET status='Failed',last_update_utc=@updated WHERE id=@id AND status='Complete';", voyageId))
                {
                    _Sql.Binder.AddTimestamp(command, "@updated", "voyages", "last_update_utc", now);
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                        throw new InvalidOperationException("The voyage changed; the correction and audit were rolled back.");
                }
                if (request.HistoricalWork != null)
                {
                    CapturedState corrected = await CaptureAsync(connection, transaction, tenantId, userId, voyageId, request, false, token).ConfigureAwait(false);
                    AdministrativeRestartPermit permit = new AdministrativeRestartPermit
                    {
                        TenantId = tenantId, UserId = userId, VoyageId = voyageId, MissionId = request.HistoricalWork.MissionId,
                        PriorFingerprint = plan.Fingerprint, CorrectedFingerprint = corrected.Plan.Fingerprint,
                        SourceEvidence = request.CapturedSourceEvidence!, Disposition = request.HistoricalWork,
                        PlanningSourceCommit = request.SourceCommit, RestartTitle = request.RestartTitle, RestartDescription = request.RestartDescription
                    };
                    ArmadaEvent issued = new ArmadaEvent("mission.administrative_restart_permit_issued", "One-use saved-work restart permit issued; no mission started.")
                    { TenantId = tenantId, UserId = userId, EntityType = "mission", EntityId = permit.MissionId,
                        MissionId = permit.MissionId, VoyageId = voyageId };
                    permit.PermitId = issued.Id; issued.Payload = JsonSerializer.Serialize(permit);
                    await InsertEventAsync(connection, transaction, issued, token).ConfigureAwait(false);
                    plan.RestartPermitId = issued.Id;
                }
                await transaction.CommitAsync(token).ConfigureAwait(false);
                plan.Applied = true; plan.AuditEventId = audit.Id;
                return plan;
            }
        }

        private sealed class CapturedState
        {
            public Voyage Voyage { get; set; } = null!;
            public List<Mission> Missions { get; set; } = new List<Mission>();
            public SortedDictionary<string, object?> Snapshot { get; set; } = null!;
            public string Json { get; set; } = String.Empty;
            public AdministrativeVoyageRecoveryPlan Plan { get; set; } = null!;
        }

        private async Task<CapturedState> CaptureAsync(DbConnection connection, DbTransaction transaction,
            string tenantId, string userId, string voyageId, AdministrativeVoyageRecoveryRequest request,
            bool evaluateEligibility, CancellationToken token)
        {
            string table = _Sql.Provider == DatabaseTypeEnum.SqlServer ? "voyages WITH (UPDLOCK,HOLDLOCK)" : "voyages";
            string suffix = _Sql.Provider == DatabaseTypeEnum.Postgresql || _Sql.Provider == DatabaseTypeEnum.Mysql ? " FOR UPDATE" : String.Empty;
            Voyage voyage;
            SortedDictionary<string, object?> priorVoyage;
            using (DbCommand command = Command(connection, transaction, "SELECT * FROM " + table + " WHERE id=@id" + suffix + ";", voyageId))
            using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new KeyNotFoundException("Voyage not found.");
                voyage = VoyageColumns.Read(reader, _Sql.Values);
                priorVoyage = Row(reader);
            }
            List<Mission> missions = new List<Mission>();
            SortedDictionary<string, object?> snapshot = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            { ["voyage"] = priorVoyage, ["sourceEvidence"] = request.CapturedSourceEvidence };
            List<SortedDictionary<string, object?>> missionRows = new List<SortedDictionary<string, object?>>();
            string missionTable = _Sql.Provider == DatabaseTypeEnum.SqlServer ? "missions WITH (UPDLOCK,HOLDLOCK)" : "missions";
            using (DbCommand command = Command(connection, transaction, "SELECT * FROM " + missionTable + " WHERE voyage_id=@id ORDER BY id" + suffix + ";", voyageId))
            using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (missions.Count >= 32) throw new InvalidOperationException("The administrative graph exceeds its bounded stage inventory.");
                    missions.Add(MissionColumns.Read(reader, _Sql.Values)); missionRows.Add(Row(reader));
                }
            }
            snapshot["missions"] = missionRows;
            snapshot["restartTitle"] = request.RestartTitle;
            snapshot["restartDescription"] = request.RestartDescription;
            if (request.HistoricalWork != null)
            {
                List<SortedDictionary<string, object?>> cancellation = await RowsAsync(connection, transaction, "events",
                    "id=@id", request.HistoricalWork.CancellationEventId, token).ConfigureAwait(false);
                snapshot["cancellationAudit"] = cancellation;
                if (cancellation.Count != 1) request.CapturedHistoricalEvidence = false;
            }
            foreach (string vesselId in missions.Select(m => m.VesselId).Where(id => !String.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal))
                snapshot["vessel:" + vesselId] = await RowsAsync(connection, transaction, "vessels", "id=@id", vesselId!, token, "id,tenant_id,user_id,local_path,default_branch").ConfigureAwait(false);
            List<string> blockers = evaluateEligibility
                ? AdministrativeVoyageRecoveryRule.FindBlockers(voyage, missions, tenantId, userId, request.SourceCommit,
                    request.HistoricalWork, request.CapturedHistoricalEvidence) : new List<string>();
            if (String.IsNullOrEmpty(request.CapturedSourceEvidence)) blockers.Add("repository_source_evidence_required");
            foreach (Mission mission in missions)
            {
                foreach (string relation in new[] { "merge_entries", "landing_jobs", "harbor_jobs" })
                {
                    List<SortedDictionary<string, object?>> rows = await RowsAsync(connection, transaction, relation,
                        "mission_id=@id", mission.Id, token).ConfigureAwait(false);
                    snapshot[relation + ":" + mission.Id] = rows;
                    if (rows.Count > 0) blockers.Add("landing_or_checkout_lineage_requires_review:" + mission.Id);
                }
                List<SortedDictionary<string, object?>> facts = await RowsAsync(connection, transaction, "mission_attempt_facts",
                    "mission_id=@id", mission.Id, token).ConfigureAwait(false);
                snapshot["facts:" + mission.Id] = facts;
                List<SortedDictionary<string, object?>> nativeEvents = await RowsAsync(connection, transaction, "events",
                    "mission_id=@id AND event_type NOT IN ('mission.administrative_restart_permit_issued','mission.administrative_restart_consumed')",
                    mission.Id, token).ConfigureAwait(false);
                snapshot["nativeEvents:" + mission.Id] = nativeEvents;
                if (!String.Equals(mission.Persona, "Architect", StringComparison.OrdinalIgnoreCase)
                    && nativeEvents.Any(row => Convert.ToString(row["event_type"]) == "mission.completed"
                        || Convert.ToString(row["event_type"]) == "mission.landed"))
                    blockers.Add("conflicting_accepted_native_outcome:" + mission.Id);
                if (!request.CapturedHistoricalEvidence && ((mission.Status == MissionStatusEnum.Cancelled && facts.Count > 0)
                    || facts.Any(row => Convert.ToString(row["fact_type"]) == "Restarted" || Convert.ToString(row["fact_type"]) == "Retried")))
                    blockers.Add("prior_execution_evidence_requires_review:" + mission.Id);
                if (facts.Any(row => Convert.ToString(row["fact_type"]) == "Landed"))
                    blockers.Add("landed_attempt_record:" + mission.Id);
                List<SortedDictionary<string, object?>> captains = await RowsAsync(connection, transaction, "captains",
                    "current_mission_id=@id", mission.Id, token).ConfigureAwait(false);
                snapshot["captains:" + mission.Id] = captains;
                if (captains.Count > 0) blockers.Add("captain_still_bound:" + mission.Id);
                List<SortedDictionary<string, object?>> checks = await RowsAsync(connection, transaction, "check_runs",
                    "mission_id=@id", mission.Id, token).ConfigureAwait(false);
                snapshot["checks:" + mission.Id] = checks;
                if (checks.Any(IsUnresolvedCheck)) blockers.Add("unresolved_mission_check:" + mission.Id);
            }
            List<SortedDictionary<string, object?>> voyageChecks = await RowsAsync(connection, transaction,
                "check_runs", "voyage_id=@id", voyageId, token).ConfigureAwait(false);
            snapshot["voyageChecks"] = voyageChecks;
            if (voyageChecks.Any(IsUnresolvedCheck)) blockers.Add("unresolved_voyage_check");
            string json = JsonSerializer.Serialize(snapshot);
            if (Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024)
                throw new InvalidOperationException("The complete prior-outcome archive exceeds its bound; nothing changed.");
            AdministrativeVoyageRecoveryPlan plan = new AdministrativeVoyageRecoveryPlan
            {
                VoyageId = voyageId, Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
                Blockers = blockers.Distinct(StringComparer.Ordinal).ToList(), SourceEvidence = request.CapturedSourceEvidence
            };
            return new CapturedState { Voyage = voyage, Missions = missions, Snapshot = snapshot, Json = json, Plan = plan };
        }

        private async Task InsertEventAsync(DbConnection connection, DbTransaction transaction, ArmadaEvent audit, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO events (id,tenant_id,user_id,event_type,entity_type,entity_id,mission_id,vessel_id,voyage_id,message,payload,created_utc) VALUES (@event,@tenant,@user,@type,@entity,@id,@mission,@vessel,@voyage,@message,@payload,@created);";
                Add(command, "@event", audit.Id); Add(command, "@tenant", audit.TenantId); Add(command, "@user", audit.UserId);
                Add(command, "@type", audit.EventType); Add(command, "@entity", audit.EntityType); Add(command, "@id", audit.EntityId);
                Add(command, "@mission", audit.MissionId); Add(command, "@vessel", audit.VesselId); Add(command, "@voyage", audit.VoyageId);
                Add(command, "@message", audit.Message); Add(command, "@payload", audit.Payload);
                _Sql.Binder.AddTimestamp(command, "@created", "events", "created_utc", audit.CreatedUtc);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private static bool IsUnresolvedCheck(SortedDictionary<string, object?> row)
            => Convert.ToString(row["status"]) == "Pending" || Convert.ToString(row["status"]) == "Running";

        private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql, string id)
        {
            DbCommand command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            Add(command, "@id", id); return command;
        }
        private static void Add(DbCommand command, string name, string? value) => StoredValueBinder.Value(command, name, value);
        private static SortedDictionary<string, object?> Row(DbDataReader reader)
        {
            SortedDictionary<string, object?> row = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            for (int index = 0; index < reader.FieldCount; index++) row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            return row;
        }
        private static async Task<List<SortedDictionary<string, object?>>> RowsAsync(DbConnection connection,
            DbTransaction transaction, string table, string where, string id, CancellationToken token, string columns = "*")
        {
            using (DbCommand command = Command(connection, transaction, "SELECT " + columns + " FROM " + table + " WHERE " + where + " ORDER BY " + (table == "harbor_jobs" ? "job_id" : "id") + ";", id))
            using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                List<SortedDictionary<string, object?>> rows = new List<SortedDictionary<string, object?>>();
                while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(Row(reader));
                return rows;
            }
        }
    }
}
