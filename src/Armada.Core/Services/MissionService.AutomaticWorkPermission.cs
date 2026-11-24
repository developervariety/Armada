namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private readonly ConcurrentDictionary<string, string> _AutomaticPermissionHoldReasons =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private async Task<int> StartAutomaticCompletionContinuationsAsync(CancellationToken token)
        {
            int started = 0;
            List<Mission> produced = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.WorkProduced, token).ConfigureAwait(false);
            foreach (Mission mission in produced)
            {
                if (mission.AutomaticWorkPermission == null || mission.AutomaticWorkPermissionInvalid
                    || mission.HeldForOperatorReview || mission.ProcessId.HasValue || !mission.StartedUtc.HasValue
                    || String.IsNullOrWhiteSpace(mission.CommitHash) || String.IsNullOrWhiteSpace(mission.CaptainId)
                    || IsCompletionInFlight(mission.Id) || _GateRerunsInFlight.ContainsKey(mission.Id)) continue;
                bool hasRecordedVerification = await HasCurrentAutomaticVerificationAsync(mission, token).ConfigureAwait(false);
                if (!hasRecordedVerification && !await CanAdmitAutomaticWorkAsync(mission,
                    MissionAutomaticActionEnum.Verification, token).ConfigureAwait(false)) continue;
                if (!await AutomaticCompletionStillNeedsContinuationAsync(mission, hasRecordedVerification, token).ConfigureAwait(false)) continue;
                Captain? captain = await _Database.Captains.ReadAsync(mission.CaptainId!, token).ConfigureAwait(false);
                if (captain == null || !_GateRerunsInFlight.TryAdd(mission.Id, 0)) continue;
                started++;
                _ = Task.Run(async () =>
                {
                    TaskCompletionSource<bool> completed = new TaskCompletionSource<bool>();
                    CompletionGuardEntry entry = new CompletionGuardEntry(completed.Task, MissionCompletionAttempt.Of(mission));
                    bool entered = false;
                    try
                    {
                        using (IDisposable boundary = await MissionExecutionBoundary.Locks.AcquireAsync(mission.Id,
                            CancellationToken.None).ConfigureAwait(false))
                        {
                            Mission? current = await _Database.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                            if (current == null || current.Status != MissionStatusEnum.WorkProduced
                                || !SameAutomaticWorkIdentity(mission, current)) return;
                            entered = TryEnterAutomaticContinuationGuard(mission.Id, entry);
                            if (!entered) return;
                            _HandlingCompletionOf.Value = mission.Id;
                            await HandleCompletionCoreAsync(captain, mission.Id, CancellationToken.None,
                                resumeAutomaticCompletion: true).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "automatic completion continuation for " + mission.Id + " stopped: " + ex.GetType().Name);
                    }
                    finally
                    {
                        completed.TrySetResult(true);
                        if (entered) _InFlightCompletions.TryRemove(new KeyValuePair<string, CompletionGuardEntry>(mission.Id, entry));
                        _GateRerunsInFlight.TryRemove(mission.Id, out byte _);
                    }
                });
            }
            return started;
        }

        private bool TryEnterAutomaticContinuationGuard(string missionId, CompletionGuardEntry entry)
        {
            while (true)
            {
                if (_InFlightCompletions.TryAdd(missionId, entry)) return true;
                if (!_InFlightCompletions.TryGetValue(missionId, out CompletionGuardEntry? previous) || previous == null)
                    continue;
                if (!previous.Gate.IsCompleted || previous.Attempt == null || entry.Attempt == null
                    || previous.Attempt.StartedUtc != entry.Attempt.StartedUtc) return false;
                // This is continuation of durable unfinished work, not another exit notification.
                if (_InFlightCompletions.TryUpdate(missionId, entry, previous)) return true;
            }
        }

        private async Task<bool> HasCurrentAutomaticVerificationAsync(Mission mission, CancellationToken token)
        {
            if (_DefinitionOfDoneGate == null) return true;
            if (!mission.StartedUtc.HasValue || String.IsNullOrWhiteSpace(mission.CommitHash)) return false;
            EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(new EnumerationQuery
            {
                MissionId = mission.Id, EventType = DefinitionOfDoneEvaluationRecord.EventType,
                Order = Enums.EnumerationOrderEnum.CreatedDescending, PageSize = 1
            }, token).ConfigureAwait(false);
            foreach (ArmadaEvent item in page.Objects)
            {
                if (!String.Equals(item.MissionId, mission.Id, StringComparison.Ordinal)
                    || !String.Equals(item.TenantId, mission.TenantId, StringComparison.Ordinal)
                    || !String.Equals(item.UserId, mission.UserId, StringComparison.Ordinal)
                    || item.CreatedUtc < mission.StartedUtc.Value || String.IsNullOrWhiteSpace(item.Payload)
                    || item.Payload.Length > DefinitionOfDoneEvaluationRecord.MaxStoredPayloadLength) return false;
                try
                {
                    DefinitionOfDoneEvaluationRecord? record = JsonSerializer.Deserialize<DefinitionOfDoneEvaluationRecord>(item.Payload);
                    return record != null && record.SchemaVersion == DefinitionOfDoneEvaluationRecord.CurrentSchemaVersion
                        && record.StartedUtc >= mission.StartedUtc.Value && record.CompletedUtc >= record.StartedUtc
                        && String.Equals(record.CommitHash, mission.CommitHash, StringComparison.Ordinal)
                        && String.Equals(record.BranchName, mission.BranchName, StringComparison.Ordinal)
                        && record.RecoveryAttempts == mission.RecoveryAttempts && !record.UncommittedTrackedChanges
                        && (record.Outcome == Enums.DefinitionOfDoneEvaluationOutcomeEnum.Passed
                            ? String.Equals(record.MeasuredCommitHash, mission.CommitHash, StringComparison.Ordinal)
                            : record.MeasuredCommitHash == null || String.Equals(record.MeasuredCommitHash, mission.CommitHash, StringComparison.Ordinal))
                        && (record.Outcome == Enums.DefinitionOfDoneEvaluationOutcomeEnum.Passed
                            || record.Outcome == Enums.DefinitionOfDoneEvaluationOutcomeEnum.Skipped
                            || record.Outcome == Enums.DefinitionOfDoneEvaluationOutcomeEnum.NotVerifiable);
                }
                catch (JsonException) { return false; }
            }
            return false;
        }

        private async Task<bool> AutomaticCompletionStillNeedsContinuationAsync(Mission mission,
            bool hasRecordedVerification, CancellationToken token)
        {
            if (!hasRecordedVerification) return true;
            EnumerationResult<ArmadaEvent> checkpoints = await _Database.Events.EnumerateAsync(new EnumerationQuery
            {
                MissionId = mission.Id,
                EventType = "mission.automatic_completion_settled",
                PageSize = 20
            }, token).ConfigureAwait(false);
            foreach (ArmadaEvent checkpoint in checkpoints.Objects)
            {
                if (String.IsNullOrWhiteSpace(checkpoint.Payload)) continue;
                try
                {
                    AutomaticCompletionCheckpoint? value = JsonSerializer.Deserialize<AutomaticCompletionCheckpoint>(checkpoint.Payload);
                    if (value != null && String.Equals(value.MissionId, mission.Id, StringComparison.Ordinal)
                        && value.StartedUtc == mission.StartedUtc
                        && String.Equals(value.CommitHash, mission.CommitHash, StringComparison.Ordinal)) return false;
                }
                catch (JsonException) { }
            }
            return true;
        }

        private async Task RecordAutomaticCompletionSettledAsync(Mission mission, CancellationToken token)
        {
            if (mission.AutomaticWorkPermission == null) return;
            ArmadaEvent checkpoint = new ArmadaEvent("mission.automatic_completion_settled",
                "Automatic completion reached its normal review, handoff or landing boundary.");
            EventOwnerScope.ApplyFromMission(checkpoint, mission);
            checkpoint.EntityType = "mission";
            checkpoint.EntityId = mission.Id;
            checkpoint.MissionId = mission.Id;
            checkpoint.VesselId = mission.VesselId;
            checkpoint.VoyageId = mission.VoyageId;
            checkpoint.Payload = JsonSerializer.Serialize(new AutomaticCompletionCheckpoint
            {
                MissionId = mission.Id, StartedUtc = mission.StartedUtc, CommitHash = mission.CommitHash
            });
            await _Database.Events.CreateAsync(checkpoint, token).ConfigureAwait(false);
        }

        private sealed class AutomaticCompletionCheckpoint
        {
            public string MissionId { get; set; } = String.Empty;
            public DateTime? StartedUtc { get; set; }
            public string? CommitHash { get; set; }
        }

        private async Task<bool> CanAdmitAssignmentDependenciesAsync(Mission mission, Mission? expectedDependency,
            CancellationToken token)
        {
            if (expectedDependency != null
                && (!await CanAdmitAutomaticWorkAsync(expectedDependency, MissionAutomaticActionEnum.DependentAdmission, token).ConfigureAwait(false)
                    || !await DependencyGroupSatisfiedAsync(mission, expectedDependency, token).ConfigureAwait(false)))
                return false;
            if (mission.IntegrationBarrier != null)
            {
                foreach (MissionIntegrationBarrierMember member in mission.IntegrationBarrier.Members)
                {
                    Mission? terminal = await _Database.Missions.ReadAsync(member.CurrentTerminalMissionId, token).ConfigureAwait(false);
                    if (terminal == null || !await CanAdmitAutomaticWorkAsync(terminal,
                        MissionAutomaticActionEnum.DependentAdmission, token).ConfigureAwait(false)) return false;
                }
            }
            return true;
        }

        private async Task RollBackPermissionBlockedAssignmentAsync(Mission mission, Captain captain, Dock? dock,
            bool preserveSavedBranch)
        {
            await ReleaseClaimIfHeldAsync(captain.Id, mission.Id).ConfigureAwait(false);
            await RevertUncommittedAssignmentAsync(mission, preserveSavedBranch,
                MissionAssignmentStateEnum.WaitingForDependency).ConfigureAwait(false);
            if (dock != null) await DiscardUncommittedDockAsync(mission, dock).ConfigureAwait(false);
        }

        private async Task<Mission?> TryGetAutomaticVerificationPermissionAsync(Mission expected, Captain captain,
            CancellationToken token)
        {
            Mission? current = await _Database.Missions.ReadAsync(expected.Id, token).ConfigureAwait(false);
            if (expected.AutomaticWorkPermission == null && !expected.AutomaticWorkPermissionInvalid
                && current?.AutomaticWorkPermission == null && current?.AutomaticWorkPermissionInvalid != true)
                return expected;
            if (current == null || current.Status != MissionStatusEnum.WorkProduced
                || !SameAutomaticWorkIdentity(expected, current)) return null;
            if (await CanAdmitAutomaticWorkAsync(current, MissionAutomaticActionEnum.Verification, token).ConfigureAwait(false))
                return current;
            await ReleaseClaimIfHeldAsync(captain.Id, expected.Id).ConfigureAwait(false);
            // Completion callers must remain responsive. Durable recovery resumes this same work after a grant.
            return null;
        }

        private async Task<bool> CanAdmitAutomaticWorkAsync(Mission expected, MissionAutomaticActionEnum action,
            CancellationToken token)
        {
            Mission? current = await _Database.Missions.ReadAsync(expected.Id, token).ConfigureAwait(false);
            bool optedIn = expected.AutomaticWorkPermission != null || expected.AutomaticWorkPermissionInvalid
                || current?.AutomaticWorkPermission != null || current?.AutomaticWorkPermissionInvalid == true;
            if (!optedIn) return true;

            string? reason = null;
            if (current == null || !SameAutomaticWorkIdentity(expected, current))
                reason = "automatic_work_permission_binding_changed";
            else if (!MissionAutomaticWorkPermissionPolicy.IsAllowed(current, action, _UtcNow(), out reason))
            {
                // The policy supplies the precise missing, invalid, expired or stale grant reason.
            }
            else if (action == MissionAutomaticActionEnum.DependentAdmission
                && (current.HeldForOperatorReview || current.Status == MissionStatusEnum.Review
                    || (current.RequiresReview && (!current.ReviewedUtc.HasValue
                        || !current.StartedUtc.HasValue || current.ReviewedUtc.Value < current.StartedUtc.Value))))
                reason = "automatic_work_permission_manual_review_pending";
            else if (action == MissionAutomaticActionEnum.DependentAdmission
                && IsCompletionInFlight(current.Id)
                && !String.Equals(_HandlingCompletionOf.Value, current.Id, StringComparison.Ordinal))
                reason = "automatic_work_permission_completion_in_flight";

            string key = expected.Id + ":" + action;
            if (reason == null)
            {
                _AutomaticPermissionHoldReasons.TryRemove(key, out string? _);
                return true;
            }
            if (!_AutomaticPermissionHoldReasons.TryGetValue(key, out string? previous)
                || !String.Equals(previous, reason, StringComparison.Ordinal))
            {
                _AutomaticPermissionHoldReasons[key] = reason;
                await RecordAssignmentHoldEventAsync(current ?? expected, "mission.automatic_work_held", reason,
                    expected.Id, "Automatic " + action + " is held: " + reason + ". No work was authorized.", token).ConfigureAwait(false);
                await AppendMissionActivityAsync(expected.Id, "automatic work held: " + action + ": " + reason, token).ConfigureAwait(false);
            }
            return false;
        }

        private static bool SameAutomaticWorkIdentity(Mission expected, Mission current) =>
            String.Equals(expected.Id, current.Id, StringComparison.Ordinal)
            && String.Equals(expected.TenantId, current.TenantId, StringComparison.Ordinal)
            && String.Equals(expected.UserId, current.UserId, StringComparison.Ordinal)
            && expected.StartedUtc == current.StartedUtc
            && String.Equals(expected.CommitHash, current.CommitHash, StringComparison.Ordinal);

        /// <summary>Apply an authenticated operator's permission change without restarting or accepting work.</summary>
        public async Task<Mission?> UpdateAutomaticWorkPermissionAsync(AuthContext caller, string missionId,
            MissionAutomaticWorkPermissionRequest request, CancellationToken token = default)
        {
            if (caller == null || !caller.IsAuthenticated || String.IsNullOrWhiteSpace(caller.UserId)
                || !String.IsNullOrWhiteSpace(caller.MissionId) || !String.IsNullOrWhiteSpace(caller.AskThreadId)
                || !String.IsNullOrWhiteSpace(caller.AskTurnId))
                throw new UnauthorizedAccessException("Only an authenticated operator can change automatic-work permissions.");
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!request.Operation.HasValue || !Enum.IsDefined(request.Operation.Value)
                || String.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2048)
                throw new ArgumentException("A known operation and a reason of at most 2048 characters are required.");

            Mission? mission = await CallerScopedRead.ReadMissionAsync(_Database, caller, missionId, token).ConfigureAwait(false);
            if (mission == null) return null;
            if (mission.AutomaticWorkPermissionInvalid
                || (mission.AutomaticWorkPermission != null && mission.AutomaticWorkPermission.Version != 1))
                throw new InvalidOperationException("automatic_work_permission_invalid: repair the stored permission before changing it.");

            MissionAutomaticWorkPermission? current = mission.AutomaticWorkPermission;
            MissionAutomaticWorkPermission next = new MissionAutomaticWorkPermission
            {
                Verification = current?.Verification,
                DependentAdmission = current?.DependentAdmission
            };
            DateTime utcNow = DateTime.UtcNow;
            if (request.Operation == MissionAutomaticPermissionOperationEnum.Enable)
            {
                if (request.Action.HasValue || request.ExpectedStartedUtc.HasValue
                    || request.ExpectedCommitHash != null || request.ExpiresUtc.HasValue)
                    throw new ArgumentException("Enable accepts no action, launch, source or expiry fields.");
                if (current != null) return mission;
            }
            else
            {
                if (current == null)
                    throw new InvalidOperationException("automatic_work_permission_not_enabled: explicitly enable the barrier first.");
                if (!request.Action.HasValue || !Enum.IsDefined(request.Action.Value)
                    || !request.ExpectedStartedUtc.HasValue || !mission.StartedUtc.HasValue
                    || request.ExpectedStartedUtc.Value != mission.StartedUtc.Value
                    || String.IsNullOrWhiteSpace(request.ExpectedCommitHash)
                    || !String.Equals(request.ExpectedCommitHash, mission.CommitHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("automatic_work_permission_binding_changed: reload the current launch and source.");

                MissionAutomaticWorkGrant? existing = request.Action == MissionAutomaticActionEnum.Verification
                    ? current.Verification : current.DependentAdmission;
                MissionAutomaticWorkGrant? changed = null;
                if (request.Operation == MissionAutomaticPermissionOperationEnum.Grant)
                {
                    if (mission.Status != MissionStatusEnum.WorkProduced && mission.Status != MissionStatusEnum.Review
                        && !(request.Action == MissionAutomaticActionEnum.DependentAdmission && IsDependencySatisfyingStatus(mission.Status)))
                        throw new InvalidOperationException("automatic_work_permission_source_not_ready: the current launch must have produced its source.");
                    if (!request.ExpiresUtc.HasValue || request.ExpiresUtc.Value.Kind != DateTimeKind.Utc
                        || request.ExpiresUtc.Value <= utcNow || request.ExpiresUtc.Value > utcNow.AddHours(1))
                        throw new ArgumentException("Grant requires a future UTC expiry no more than one hour from server time.");
                    changed = new MissionAutomaticWorkGrant
                    {
                        MissionId = mission.Id,
                        TenantId = mission.TenantId,
                        UserId = mission.UserId,
                        StartedUtc = mission.StartedUtc.Value,
                        CommitHash = mission.CommitHash!,
                        GrantedByUserId = caller.UserId!,
                        GrantedUtc = utcNow,
                        ExpiresUtc = request.ExpiresUtc.Value
                    };
                    if (request.Action == MissionAutomaticActionEnum.Verification) next.Verification = changed;
                    else next.DependentAdmission = changed;
                    Mission candidate = new Mission(mission.Title)
                    {
                        Id = mission.Id, TenantId = mission.TenantId, UserId = mission.UserId,
                        StartedUtc = mission.StartedUtc, CommitHash = mission.CommitHash, AutomaticWorkPermission = next
                    };
                    if (!MissionAutomaticWorkPermissionPolicy.IsAllowed(candidate, request.Action.Value, utcNow, out string? refusal))
                        throw new InvalidOperationException(refusal);
                    if (SameGrant(existing, changed)) return mission;
                }
                else
                {
                    if (request.ExpiresUtc.HasValue) throw new ArgumentException("Revoke accepts no expiry.");
                    if (existing == null) return mission;
                    if (request.Action == MissionAutomaticActionEnum.Verification) next.Verification = null;
                    else next.DependentAdmission = null;
                }
            }

            ArmadaEvent audit = new ArmadaEvent("mission.automatic_work_permission_changed",
                "Operator changed one mission's automatic-work permission.");
            EventOwnerScope.ApplyFromMission(audit, mission);
            audit.EntityType = "mission";
            audit.EntityId = mission.Id;
            audit.MissionId = mission.Id;
            audit.VesselId = mission.VesselId;
            audit.VoyageId = mission.VoyageId;
            audit.Payload = JsonSerializer.Serialize(new
            {
                operation = request.Operation, action = request.Action, actorUserId = caller.UserId,
                reason = request.Reason, expectedStartedUtc = request.ExpectedStartedUtc,
                expectedCommitHash = request.ExpectedCommitHash, expiresUtc = request.ExpiresUtc,
                expectedRevision = mission.AutomaticWorkPermissionWriteRevision,
                newRevision = mission.AutomaticWorkPermissionWriteRevision + 1
            });
            if (!await _Database.Missions.TryWriteAutomaticWorkPermissionAsync(mission, next, audit, token).ConfigureAwait(false))
                throw new InvalidOperationException("automatic_work_permission_binding_changed: no permission or audit was written; reload before retrying.");
            return await CallerScopedRead.ReadMissionAsync(_Database, caller, mission.Id, token).ConfigureAwait(false);
        }

        private static bool SameGrant(MissionAutomaticWorkGrant? existing, MissionAutomaticWorkGrant next) =>
            existing != null && String.Equals(existing.MissionId, next.MissionId, StringComparison.Ordinal)
            && String.Equals(existing.TenantId, next.TenantId, StringComparison.Ordinal)
            && String.Equals(existing.UserId, next.UserId, StringComparison.Ordinal)
            && existing.StartedUtc == next.StartedUtc && String.Equals(existing.CommitHash, next.CommitHash, StringComparison.Ordinal)
            && existing.ExpiresUtc == next.ExpiresUtc
            && String.Equals(existing.GrantedByUserId, next.GrantedByUserId, StringComparison.Ordinal);
    }
}
