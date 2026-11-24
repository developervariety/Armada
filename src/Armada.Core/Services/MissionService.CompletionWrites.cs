namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private sealed class CompletionWriteAbandonedException : OperationCanceledException { }

        // Scoped to this completion only. No ordinary mission writer uses this refresh policy.
        private sealed class CompletionWriteState
        {
            private readonly DatabaseDriver _Database;
            private Mission? _Expected;

            internal CompletionWriteState(DatabaseDriver database, Mission? expected)
            {
                _Database = database;
                _Expected = expected;
            }

            internal bool MatchesInitial(Mission working) => _Expected != null
                && String.Equals(OrdinaryFields(working), OrdinaryFields(_Expected), StringComparison.Ordinal);

            internal async Task<bool> SaveAsync(Mission working, CancellationToken token, bool allowCapturedSourceAdvance = false, bool allowAttemptReset = false)
            {
                Mission? expected = _Expected;
                if (expected == null || expected.Status == MissionStatusEnum.Cancelled
                    || !(SameLaunch(expected, working) || (allowAttemptReset && working.Status == MissionStatusEnum.Pending
                        && working.StartedUtc == null && SameOwner(expected, working)))
                    || !String.Equals(expected.ResearchReplayId, working.ResearchReplayId, StringComparison.Ordinal)
                    || (!allowCapturedSourceAdvance && !String.Equals(expected.CommitHash, working.CommitHash, StringComparison.Ordinal)))
                    return false;
                Mission? current = await _Database.Missions.ReadAsync(expected.Id, token).ConfigureAwait(false);
                if (!CanRefresh(expected, current)) return false;
                // The current stored model supplies permission fields; the old working model never does.
                expected = current!;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    CopyPermission(expected, working);
                    if (await _Database.Missions.TryUpdateCompletionAsync(working, expected, token).ConfigureAwait(false))
                    {
                        // UpdateCore increments this owner field exactly once. A second writer's increment
                        // must still make the post-save ordinary comparison refuse.
                        working.AdmissionWriteRevision = checked(expected.AdmissionWriteRevision + 1);
                        working.AdmissionWriteJson = expected.AdmissionWriteJson;
                        Mission? saved = await _Database.Missions.ReadAsync(expected.Id, token).ConfigureAwait(false);
                        if (saved == null || !String.Equals(OrdinaryFields(saved), OrdinaryFields(working), StringComparison.Ordinal))
                            return false;
                        _Expected = saved;
                        CopyPermission(saved, working);
                        working.AdmissionWriteStamp = saved.AdmissionWriteStamp;
                        working.IntegrationBarrierWriteStamp = saved.IntegrationBarrierWriteStamp;
                        return true;
                    }
                    if (attempt != 0) return false;
                    Mission? retry = await _Database.Missions.ReadAsync(expected.Id, token).ConfigureAwait(false);
                    // One retry is allowed ONLY when permission revision/raw changed and every ordinary
                    // field still equals the last persisted snapshot, including status, launch and source.
                    if (!PermissionChanged(expected, retry) || !CanRefresh(expected, retry)) return false;
                    expected = retry!;
                }
                return false;
            }

            internal async Task SaveOrAbandonAsync(Mission mission, CancellationToken token)
            {
                if (!await SaveAsync(mission, token, allowAttemptReset: mission.Status == MissionStatusEnum.Pending && mission.StartedUtc == null).ConfigureAwait(false))
                    throw new CompletionWriteAbandonedException();
            }

            internal async Task WriteGeneratedApprovalAsync(Mission working, GeneratedArtifactReviewBinding approved, CancellationToken token)
            {
                Mission? expected = _Expected;
                if (expected == null) throw new CompletionWriteAbandonedException();
                Mission? current = await _Database.Missions.ReadAsync(expected.Id, token).ConfigureAwait(false);
                if (!CanRefresh(expected, current)) throw new CompletionWriteAbandonedException();
                expected = current!;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (await _Database.Missions.TryWriteGeneratedArtifactReviewAsync(expected, approved, false, token).ConfigureAwait(false))
                    {
                        // This known owner changes exactly this binding and the update stamp. It does
                        // not increment admission_revision. No other ordinary field may be adopted.
                        expected.GeneratedArtifactReview = approved;
                        Mission? saved = await _Database.Missions.ReadAsync(expected.Id, token).ConfigureAwait(false);
                        if (saved == null || !String.Equals(OrdinaryFields(saved), OrdinaryFields(expected), StringComparison.Ordinal))
                            throw new CompletionWriteAbandonedException();
                        _Expected = saved;
                        working.GeneratedArtifactReview = saved.GeneratedArtifactReview;
                        CopyPermission(saved, working);
                        working.AdmissionWriteStamp = saved.AdmissionWriteStamp;
                        return;
                    }
                    if (attempt != 0) throw new CompletionWriteAbandonedException();
                    Mission? retry = await _Database.Missions.ReadAsync(expected.Id, token).ConfigureAwait(false);
                    if (!PermissionChanged(expected, retry) || !CanRefresh(expected, retry)) throw new CompletionWriteAbandonedException();
                    expected = retry!;
                }
            }

            private static bool CanRefresh(Mission expected, Mission? current) => current != null
                && current.Status != MissionStatusEnum.Cancelled
                && SameLaunch(expected, current)
                && String.Equals(expected.CommitHash, current.CommitHash, StringComparison.Ordinal)
                && expected.Status == current.Status
                && String.Equals(OrdinaryFields(expected), OrdinaryFields(current), StringComparison.Ordinal)
                && (Object.Equals(expected.AutomaticWorkPermissionWriteStamp, current.AutomaticWorkPermissionWriteStamp)
                    || PermissionChanged(expected, current));

            private static bool SameOwner(Mission expected, Mission current) =>
                String.Equals(expected.Id, current.Id, StringComparison.Ordinal)
                && String.Equals(expected.TenantId, current.TenantId, StringComparison.Ordinal)
                && String.Equals(expected.UserId, current.UserId, StringComparison.Ordinal);
            private static bool SameLaunch(Mission expected, Mission current) => SameOwner(expected, current)
                && expected.StartedUtc == current.StartedUtc;

            private static bool PermissionChanged(Mission expected, Mission? current) => current != null
                && (expected.AutomaticWorkPermissionWriteRevision != current.AutomaticWorkPermissionWriteRevision
                    || !String.Equals(expected.AutomaticWorkPermissionRawJson, current.AutomaticWorkPermissionRawJson, StringComparison.Ordinal));

            private static void CopyPermission(Mission current, Mission working)
            {
                working.AutomaticWorkPermission = current.AutomaticWorkPermission;
                working.AutomaticWorkPermissionInvalid = current.AutomaticWorkPermissionInvalid;
                working.AutomaticWorkPermissionRawJson = current.AutomaticWorkPermissionRawJson;
                working.AutomaticWorkPermissionWriteRevision = current.AutomaticWorkPermissionWriteRevision;
                working.AutomaticWorkPermissionWriteStamp = current.AutomaticWorkPermissionWriteStamp;
            }

            // Every ordinary MissionColumns.Write field participates. Permission-only updates change
            // none of them. LastUpdateUtc is separately compared atomically by its original stored stamp.
            private static string OrdinaryFields(Mission mission) => JsonSerializer.Serialize(new
            {
                    mission.AgentOutput,
                    mission.AssignmentHoldEntityId,
                    mission.AssignmentHoldReason,
                    mission.AssignmentState,
                    mission.BranchName,
                    mission.CapabilityHint,
                    mission.CaptainId,
                    mission.CommitHash,
                    mission.CompletedUtc,
                    mission.CreatedUtc,
                    mission.DependsOnMissionId,
                    mission.Description,
                    mission.DiffSnapshot,
                    mission.DockId,
                    mission.DockProvisionAttemptId,
                    mission.FailureReason,
                    mission.GeneratedArtifactReview,
                    mission.HarborJobId,
                    mission.HeldForOperatorReview,
                    mission.HeldForOperatorReviewReason,
                    mission.Id,
                    mission.IsEvaluation,
                    mission.LandingRetryCount,
                    mission.LastRecoveryActionUtc,
                    mission.Mode,
                    mission.ParentMissionId,
                    mission.Persona,
                    mission.PrUrl,
                    mission.PreferredModel,
                    mission.PreservedRunOutput,
                    mission.PrestagedFiles,
                    mission.Priority,
                    mission.ProcessId,
                    mission.ProcessStartedUtc,
                    mission.ReconciledReason,
                    mission.ReconciledUtc,
                    mission.RecoveryAttempts,
                    mission.RequestedCaptainId,
                    mission.RequiresReview,
                    mission.RetrySkipCaptainIds,
                    mission.ReviewComment,
                    mission.ReviewDenyAction,
                    mission.ReviewRequestedUtc,
                    mission.ReviewedByUserId,
                    mission.ReviewedUtc,
                    mission.StageChainId,
                    mission.StageOrder,
                    mission.StartFromRef,
                    mission.StartedUtc,
                    mission.Status,
                    mission.TenantId,
                    mission.Tier,
                    mission.Title,
                    mission.TotalRuntimeMs,
                    mission.UserId,
                    mission.VesselId,
                    mission.VoyageId,
                    mission.ResearchReplayId,
                    mission.AdmissionWriteRevision,
                    mission.AdmissionWriteJson,
                    mission.IntegrationBarrierWriteRevision,
                    mission.IntegrationBarrierRawJson,
                    mission.IntegrationBarrierRawJsonPresent,
                    mission.IntegrationBarrierInvalid
            });
        }
    }
}
