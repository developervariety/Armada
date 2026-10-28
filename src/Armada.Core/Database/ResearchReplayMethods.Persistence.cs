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
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Microsoft.Data.Sqlite;

    /// <summary>Immutable attempt reads and provider-bound replay persistence.</summary>
    public sealed partial class ResearchReplayMethods
    {
        /// <inheritdoc />
        public async Task<Mission?> ReadPriorMissionAsync(string tenantId, string userId, string replayId,
            string missionId, CancellationToken token = default)
        {
            using (DbConnection connection = await _Sql.OpenAsync(token).ConfigureAwait(false))
            {
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT snapshot_json,request_fingerprint FROM research_replays WHERE id=@id AND tenant_id=@tenant AND user_id=@owner;";
                    Add(command, "@id", replayId); Add(command, "@tenant", tenantId); Add(command, "@owner", userId);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
                        string json = reader.GetString(0);
                        if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant() != reader.GetString(1))
                            throw new InvalidOperationException("The immutable Research attempt archive failed its integrity check.");
                        Snapshot snapshot = JsonSerializer.Deserialize<Snapshot>(json, _Json)
                            ?? throw new InvalidOperationException("The Research attempt archive is unreadable.");
                        return snapshot.Missions.SingleOrDefault(m => m.Id == missionId);
                    }
                }
            }
        }

        private async Task<Voyage?> ReadVoyageAsync(DbConnection connection, DbTransaction transaction, string id, CancellationToken token)
        {
            string table = _Sql.Provider == DatabaseTypeEnum.SqlServer ? "voyages WITH (UPDLOCK,HOLDLOCK)" : "voyages";
            string suffix = _Sql.Provider == DatabaseTypeEnum.Postgresql || _Sql.Provider == DatabaseTypeEnum.Mysql ? " FOR UPDATE" : String.Empty;
            using (DbCommand command = Command(connection, transaction, "SELECT * FROM " + table + " WHERE id=@id" + suffix + ";"))
            {
                Add(command, "@id", id);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    return await reader.ReadAsync(token).ConfigureAwait(false) ? VoyageColumns.Read(reader, _Sql.Values) : null;
                }
            }
        }

        private async Task<ResearchReplayPlan?> ReadCommittedRequestAsync(DbConnection connection, DbTransaction transaction,
            string tenantId, string userId, string voyageId, ResearchReplayRequest request, CancellationToken token)
        {
            using (DbCommand command = Command(connection, transaction,
                "SELECT request_fingerprint,receipt_json FROM research_replays WHERE tenant_id=@tenant AND user_id=@owner AND request_id=@request;"))
            {
                Add(command, "@tenant", tenantId); Add(command, "@owner", userId); Add(command, "@request", request.RequestId);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
                    ResearchReplayPlan plan = JsonSerializer.Deserialize<ResearchReplayPlan>(reader.GetString(1), _Json)!;
                    if (reader.GetString(0) != request.ExpectedFingerprint || plan.VoyageId != voyageId || plan.SourceCommit != request.SourceCommit)
                        throw new InvalidOperationException("The replay request ID was already used for another expected state.");
                    return plan;
                }
            }
        }

        private async Task InsertArchiveAsync(DbConnection connection, DbTransaction transaction, string tenantId, string userId,
            ResearchReplayRequest request, string json, ResearchReplayPlan plan, CancellationToken token)
        {
            using (DbCommand command = Command(connection, transaction,
                "INSERT INTO research_replays (id,tenant_id,user_id,voyage_id,request_id,request_fingerprint,snapshot_json,receipt_json,created_utc) VALUES (@id,@tenant,@owner,@voyage,@request,@fingerprint,@snapshot,@receipt,@created);"))
            {
                Add(command, "@id", plan.ReplayId); Add(command, "@tenant", tenantId); Add(command, "@owner", userId);
                Add(command, "@voyage", plan.VoyageId); Add(command, "@request", request.RequestId);
                Add(command, "@fingerprint", plan.Fingerprint); Add(command, "@snapshot", json);
                Add(command, "@receipt", JsonSerializer.Serialize(plan, _Json));
                Time(command, "@created", "research_replays", "created_utc", plan.ArchivedUtc);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private async Task ResetStageAsync(DbConnection connection, DbTransaction transaction, Mission mission,
            string source, ResearchReplayPlan plan, CancellationToken token)
        {
            // Preserve original creation, dependency, criteria, captain routing and recovery budgets.
            // Old output is available ONLY through the separately addressed immutable attempt pager.
            using (DbCommand command = Command(connection, transaction,
                "UPDATE missions SET status='Pending',mission_assignment_state='Pending',assignment_hold_reason=NULL,assignment_hold_entity_id=NULL,captain_id=NULL,branch_name=NULL,dock_id=NULL,process_id=NULL,process_started_utc=NULL,pr_url=NULL,commit_hash=NULL,diff_snapshot=NULL,agent_output=NULL,preserved_run_output=NULL,failure_reason=NULL,reconciled_utc=NULL,reconciled_reason=NULL,started_utc=NULL,completed_utc=NULL,total_runtime_ms=NULL,review_comment=NULL,reviewed_by_user_id=NULL,review_requested_utc=NULL,reviewed_utc=NULL,start_from_ref=@source,research_replay_id=@replay,last_update_utc=@updated WHERE id=@id AND status=@expected;"))
            {
                Add(command, "@source", source); Add(command, "@replay", plan.ReplayId); Add(command, "@id", mission.Id);
                Add(command, "@expected", mission.Status.ToString()); Time(command, "@updated", "missions", "last_update_utc", plan.ArchivedUtc);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("A Research stage changed during replay; the transaction was rolled back.");
            }
        }

    }
}
