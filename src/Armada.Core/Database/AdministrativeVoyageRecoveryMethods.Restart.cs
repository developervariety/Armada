namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Microsoft.Data.Sqlite;

    public sealed partial class AdministrativeVoyageRecoveryMethods
    {
        /// <inheritdoc />
        public async Task<Mission> ConsumeRestartPermitAsync(string tenantId, string userId, string missionId,
            AdministrativeRestartPermit permit, AdministrativeVoyageRecoveryRequest verifiedRequest, FleetCapacityReservation reservation, CancellationToken token = default)
        {
            using (DbConnection connection = await _Sql.OpenAsync(token).ConfigureAwait(false))
            using (DbTransaction transaction = _Sql.Provider == DatabaseTypeEnum.Sqlite && connection is SqliteConnection sqlite
                ? sqlite.BeginTransaction(deferred: false)
                : await connection.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false))
            {
                await VerifyCapacityLeaseAsync(connection, transaction, reservation, token).ConfigureAwait(false);
                CapturedState current = await CaptureAsync(connection, transaction, tenantId, userId, permit.VoyageId,
                    verifiedRequest, false, token).ConfigureAwait(false);
                if (current.Voyage.Status != VoyageStatusEnum.Failed || permit.MissionId != missionId
                    || permit.TenantId != tenantId || permit.UserId != userId
                    || current.Plan.Fingerprint != permit.CorrectedFingerprint || current.Plan.Blockers.Count != 0
                    || !verifiedRequest.CapturedHistoricalEvidence || verifiedRequest.CapturedSourceEvidence != permit.SourceEvidence)
                    throw new InvalidOperationException("The one-use restart permit is spent, stale or no longer proved.");
                using (DbCommand command = Command(connection, transaction,
                    "SELECT payload FROM events WHERE id=@id AND event_type='mission.administrative_restart_permit_issued';", permit.PermitId))
                {
                    string? json = Convert.ToString(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
                    if (json != JsonSerializer.Serialize(permit)) throw new InvalidOperationException("The original immutable permit is unavailable or changed.");
                }
                Mission target = current.Missions.Single(m => m.Id == missionId);
                if (target.Status != MissionStatusEnum.Cancelled) throw new InvalidOperationException("The original cancelled stage changed.");
                AdministrativeRestartReceipt receipt = new AdministrativeRestartReceipt
                {
                    PermitId = permit.PermitId, MissionId = missionId, VoyageId = permit.VoyageId,
                    OriginalCreatedUtc = target.CreatedUtc, DependencyId = target.DependsOnMissionId,
                    DependencyCommit = current.Missions.Single(m => m.Id == target.DependsOnMissionId).CommitHash
                        ?? throw new InvalidOperationException("The preserved dependency has no source commit."),
                    SourceCommit = permit.Disposition.PreservedSourceCommit,
                    PriorSnapshot = current.Json
                };
                string consumedId = "evt_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(permit.PermitId))).ToLowerInvariant().Substring(0, 20);
                ArmadaEvent consumed = new ArmadaEvent("mission.administrative_restart_consumed",
                    "Exact-owner saved-work permit consumed once for the same mission and voyage; prior history retained.")
                { Id = consumedId, TenantId = tenantId, UserId = userId, EntityType = "mission", EntityId = missionId,
                    MissionId = missionId, VesselId = target.VesselId, VoyageId = permit.VoyageId,
                    Payload = JsonSerializer.Serialize(receipt) };
                // Deterministic event identity is the durable one-use fence, inside the same transaction as reset.
                await InsertEventAsync(connection, transaction, consumed, token).ConfigureAwait(false);
                DateTime now = DateTime.UtcNow;
                using (DbCommand command = Command(connection, transaction,
                    "UPDATE missions SET status='Pending',mission_assignment_state='Pending',assignment_hold_reason=NULL,assignment_hold_entity_id=NULL,captain_id=NULL,branch_name=NULL,dock_id=NULL,process_id=NULL,process_started_utc=NULL,pr_url=NULL,commit_hash=NULL,diff_snapshot=NULL,agent_output=NULL,failure_reason=NULL,reconciled_utc=NULL,reconciled_reason=NULL,started_utc=NULL,completed_utc=NULL,start_from_ref=@source,title=@title,description=@description,last_update_utc=@updated WHERE id=@id AND status='Cancelled';", missionId))
                {
                    Add(command, "@source", receipt.SourceCommit); Add(command, "@title", String.IsNullOrWhiteSpace(permit.RestartTitle) ? target.Title : permit.RestartTitle);
                    Add(command, "@description", String.IsNullOrWhiteSpace(permit.RestartDescription) ? target.Description : permit.RestartDescription);
                    _Sql.Binder.AddTimestamp(command, "@updated", "missions", "last_update_utc", now);
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                        throw new InvalidOperationException("The target stage changed; the permit consumption was rolled back.");
                }
                using (DbCommand command = Command(connection, transaction,
                    "UPDATE voyages SET status='InProgress',completed_utc=NULL,last_update_utc=@updated WHERE id=@id AND status='Failed';", permit.VoyageId))
                {
                    _Sql.Binder.AddTimestamp(command, "@updated", "voyages", "last_update_utc", now);
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                        throw new InvalidOperationException("The corrected voyage changed; the permit consumption was rolled back.");
                }
                MissionAttemptFact fact = new MissionAttemptFact
                { TenantId = tenantId, UserId = userId, MissionId = missionId, VoyageId = permit.VoyageId,
                    VesselId = target.VesselId, RootMissionId = missionId, FactType = MissionAttemptFactTypeEnum.Restarted,
                    ReasonCode = "administrative_restart_permit", CreatedUtc = now };
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO mission_attempt_facts (id,tenant_id,user_id,mission_id,voyage_id,vessel_id,root_mission_id,fact_type,is_rescue,reason_code,created_utc) VALUES (@fact,@tenant,@user,@id,@voyage,@vessel,@id,'Restarted',@rescue,'administrative_restart_permit',@created);";
                    Add(command, "@fact", fact.Id); Add(command, "@tenant", tenantId); Add(command, "@user", userId);
                    Add(command, "@id", missionId); Add(command, "@voyage", permit.VoyageId); Add(command, "@vessel", target.VesselId);
                    StoredValueBinder.Value(command, "@rescue", false);
                    _Sql.Binder.AddTimestamp(command, "@created", "mission_attempt_facts", "created_utc", now);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                Mission restarted;
                using (DbCommand command = Command(connection, transaction, "SELECT * FROM missions WHERE id=@id;", missionId))
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("The restarted stage disappeared.");
                    restarted = MissionColumns.Read(reader, _Sql.Values);
                }
                await VerifyCapacityLeaseAsync(connection, transaction, reservation, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return restarted;
            }
        }
        private async Task VerifyCapacityLeaseAsync(DbConnection connection, DbTransaction transaction,
            FleetCapacityReservation reservation, CancellationToken token)
        {
            if (reservation.OwnershipLost) throw new InvalidOperationException("Fleet capacity ownership was lost before the restart commit.");
            string table = _Sql.Provider == DatabaseTypeEnum.SqlServer ? "coordination_leases WITH (UPDLOCK,HOLDLOCK)" : "coordination_leases";
            string suffix = _Sql.Provider == DatabaseTypeEnum.Postgresql || _Sql.Provider == DatabaseTypeEnum.Mysql ? " FOR UPDATE" : String.Empty;
            using (DbCommand command = Command(connection, transaction, "SELECT holder,expires_utc FROM " + table + " WHERE name=@id" + suffix + ";", reservation.LeaseName))
            using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != reservation.LeaseHolder
                    || _Sql.Values.ToUtc(reader.GetValue(1)) <= DateTime.UtcNow)
                    throw new InvalidOperationException("The durable capacity lease is no longer owned; no restart was committed.");
            }
        }
    }
}
