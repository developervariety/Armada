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

    /// <summary>Serializable archive and full-chain reset transaction.</summary>
    public sealed partial class ResearchReplayMethods
    {
        private async Task<ResearchReplayPlan> ExecuteOnceAsync(string tenantId, string userId, string voyageId,
            ResearchReplayRequest request, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(tenantId) || String.IsNullOrWhiteSpace(userId)
                || String.IsNullOrWhiteSpace(voyageId)) throw new ArgumentException("Exact owner and voyage are required.");
            if (!request.DryRun && (String.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128
                || String.IsNullOrWhiteSpace(request.ExpectedFingerprint) || request.ExpectedFingerprint.Length != 64))
                throw new ArgumentException("Apply requires a bounded request ID and the preview fingerprint.");
            using (DbConnection connection = await _Sql.OpenAsync(token).ConfigureAwait(false))
            using (DbTransaction transaction = _Sql.Provider == DatabaseTypeEnum.Sqlite && connection is SqliteConnection sqlite
                ? sqlite.BeginTransaction(deferred: false)
                : await connection.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false))
            {
                // Lock the voyage before any mutation. The archive request key is also unique in the database.
                Voyage voyage = await ReadVoyageAsync(connection, transaction, voyageId, token).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("Research voyage not found.");
                if (!request.DryRun)
                {
                    ResearchReplayPlan? prior = await ReadCommittedRequestAsync(connection, transaction,
                        tenantId, userId, voyageId, request, token).ConfigureAwait(false);
                    if (prior != null) { await transaction.RollbackAsync(token).ConfigureAwait(false); return prior; }
                }
                Snapshot snapshot = await CaptureAsync(connection, transaction, voyage, token).ConfigureAwait(false);
                snapshot.Instructions = request.CapturedInstructions;
                snapshot.CurrentConfigurationDigest = request.CapturedConfigurationDigest;
                List<Mission> stages = ResearchReplayRule.Validate(voyage, snapshot.Missions, tenantId, userId, request.SourceCommit);
                List<SortedDictionary<string, object?>> vesselRows = snapshot.Rows.TryGetValue("vessel", out List<SortedDictionary<string, object?>>? capturedVessels)
                    ? capturedVessels : new List<SortedDictionary<string, object?>>();
                if (vesselRows.Count != 1 || !OwnershipPolicy.SameTenant(Convert.ToString(vesselRows[0]["tenant_id"]), tenantId)
                    || OwnershipPolicy.UserOfRecord(Convert.ToString(vesselRows[0]["user_id"])) != userId)
                    throw new UnauthorizedAccessException("The Research vessel belongs to another owner or is missing.");
                await RefuseActiveWorkAsync(connection, transaction, voyageId, stages, token).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(snapshot, _Json);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                if (bytes.Length > MaximumSnapshotBytes)
                    throw new InvalidOperationException("The complete prior attempt exceeds the bounded replay archive; nothing was reset.");
                string fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                ResearchReplayPlan plan = new ResearchReplayPlan
                {
                    VoyageId = voyageId, SourceCommit = request.SourceCommit,
                    MissionIds = stages.Select(m => m.Id).ToList(), Fingerprint = fingerprint,
                    PriorStages = stages.Select(ResearchReplayStage.FromMission).ToList(),
                    ConfigurationDigest = snapshot.CurrentConfigurationDigest,
                    ArchiveLimitations = new List<string>
                    {
                        "Archive retains the current database rows, separate playbook snapshots, full reports and available top-level delivered instruction files.",
                        "Original effective runtime settings, template versions and externally referenced supplements or sibling contents cannot be reconstructed from these records; they are not claimed as archived.",
                        "Available delivered instruction files: " + snapshot.Instructions.Count + "; missing files are not invented."
                    }
                };
                if (request.DryRun) { await transaction.RollbackAsync(token).ConfigureAwait(false); return plan; }
                if (!String.Equals(fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("The Research replay preview is stale; nothing was reset.");
                plan.ReplayId = Constants.IdGenerator.GenerateKSortable("rpl_", 24);
                plan.ArchivedUtc = DateTime.UtcNow;
                plan.Applied = true;
                await InsertArchiveAsync(connection, transaction, tenantId, userId, request, json, plan, token).ConfigureAwait(false);
                foreach (Mission mission in stages)
                    await ResetStageAsync(connection, transaction, mission, request.SourceCommit, plan, token).ConfigureAwait(false);
                using (DbCommand command = Command(connection, transaction,
                    "UPDATE voyages SET status='InProgress',completed_utc=NULL,last_update_utc=@updated WHERE id=@id AND status='Failed';"))
                {
                    Add(command, "@id", voyageId); Time(command, "@updated", "voyages", "last_update_utc", plan.ArchivedUtc);
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                        throw new InvalidOperationException("The Research voyage changed during replay.");
                }
                await transaction.CommitAsync(token).ConfigureAwait(false);
                plan.AppliedNow = true;
                return plan;
            }
        }

    }
}
