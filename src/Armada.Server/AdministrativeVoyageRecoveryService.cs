namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>Exact-owner operator boundary for an explicit nonlaunching completion correction.</summary>
    public sealed class AdministrativeVoyageRecoveryService
    {
        private readonly DatabaseDriver _Database;
        private readonly IAdmiralService _Admiral;
        private readonly IGitService _Git;
        private readonly FleetActionService _Operators;
        private readonly ArmadaSettings _Settings;
        private readonly OperationNotifier _Notifier;

        /// <summary>Create the administrative correction owner.</summary>
        public AdministrativeVoyageRecoveryService(DatabaseDriver database, IAdmiralService admiral, IGitService git,
            ArmadaSettings? settings = null, OperationNotifier? notifier = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Operators = new FleetActionService(database, admiral);
            _Settings = settings ?? new ArmadaSettings();
            _Notifier = notifier ?? OperationNotifier.None;
        }

        /// <summary>Preview or correct an evidenced administrative Complete outcome to Failed; never launch work.</summary>
        public async Task<AdministrativeVoyageRecoveryPlan> ExecuteAsync(AuthContext caller, string voyageId,
            AdministrativeVoyageRecoveryRequest request, CancellationToken token = default)
        {
            AuthContext actor = await _Operators.RequireOperatorAsync(caller, token).ConfigureAwait(false);
            if (request == null || String.IsNullOrEmpty(request.SourceCommit)) throw new ArgumentException("Exact source is required.");
            List<Mission> stages = await _Database.Missions.EnumerateByVoyageAsync(voyageId, token).ConfigureAwait(false);
            if (stages.Any(m => !String.IsNullOrEmpty(m.ParentMissionId) || RescueMissionMarker.IsAutoRescue(m)))
                throw new InvalidOperationException("Administrative correction requires an original non-rescue stage family.");
            List<IDisposable> leases = new List<IDisposable>();
            try
            {
                foreach (string id in stages.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal))
                    leases.Add(await MissionRecoveryGuard.AcquireAsync(_Database, id, token).ConfigureAwait(false));
                foreach (string id in stages.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal))
                    leases.Add(await MissionExecutionBoundary.Locks.AcquireAsync(id, token).ConfigureAwait(false));
                foreach (string id in stages.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal))
                    leases.Add(await MissionExecutionBoundary.LaunchWrites.AcquireAsync(id, token).ConfigureAwait(false));
                leases.Add(await DirectLandingAdmission.AcquireAsync(_Database, actor.TenantId, voyageId, String.Empty, token).ConfigureAwait(false));
                leases.Add(await VoyageCompletionRule.AcquireAdministrativeCorrectionAsync(voyageId, token).ConfigureAwait(false));
                stages = await _Database.Missions.EnumerateByVoyageAsync(voyageId, token).ConfigureAwait(false);
                Voyage voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("Voyage not found.");
                AdministrativeVoyageRecoveryRule.FindBlockers(voyage, stages, actor.TenantId!, actor.UserId!, request.SourceCommit);
                foreach (Mission mission in stages)
                {
                    if (_Admiral.IsMissionCompletionInFlight(mission.Id)
                        || LandingRetryRuns.IsRunning(mission.Id)
                        || await MissionRecoveryGuard.FindActiveDescendantAsync(_Database, mission, token).ConfigureAwait(false) != null)
                        throw new InvalidOperationException("A completion, landing retry or rescue still owns the stage.");
                }
                // Only the live source owner creates evidence. Input JSON cannot assert that a base commit means no landing.
                request.CapturedSourceEvidence = await CaptureSourceEvidenceAsync(actor, stages, request, token).ConfigureAwait(false);
                return await _Database.AdministrativeVoyageRecovery.ExecuteAsync(actor.TenantId!, actor.UserId!, voyageId,
                    request, token).ConfigureAwait(false);
            }
            finally { for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose(); }
        }

        private async Task<string?> CaptureSourceEvidenceAsync(AuthContext actor, List<Mission> stages,
            AdministrativeVoyageRecoveryRequest request, CancellationToken token)
        {
            request.CapturedHistoricalEvidence = false;
            string source = request.SourceCommit;
            Mission? planner = stages.OrderBy(m => m.StageOrder).FirstOrDefault();
            if (planner == null || String.IsNullOrEmpty(planner.VesselId)
                || (request.HistoricalWork == null && String.IsNullOrEmpty(planner.BranchName))) return null;
            Vessel? vessel = await _Database.Vessels.ReadAsync(planner.VesselId, token).ConfigureAwait(false);
            if (vessel == null || !OwnershipPolicy.SameTenant(vessel.TenantId, actor.TenantId)
                || OwnershipPolicy.UserOfRecord(vessel.UserId) != actor.UserId)
                throw new UnauthorizedAccessException("The source vessel belongs to another owner.");
            if (String.IsNullOrEmpty(vessel.LocalPath)) return null;
            try
            {
                await _Git.RefreshRemoteTrackingBranchAsync(vessel.LocalPath,
                    String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch.Trim(), token).ConfigureAwait(false);
            }
            catch (NotSupportedException) { return null; }
            string target = LandingReconcileProof.TargetRefOf(vessel);
            string? baseCommit = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath, source, token).ConfigureAwait(false);
            string? branchCommit = String.IsNullOrEmpty(planner.BranchName) ? null
                : await _Git.GetRevisionCommitShaAsync(vessel.LocalPath, planner.BranchName, token).ConfigureAwait(false);
            string? targetCommit = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath, target, token).ConfigureAwait(false);
            if (!String.Equals(baseCommit, source, StringComparison.OrdinalIgnoreCase)
                || (request.HistoricalWork == null && !String.Equals(branchCommit, source, StringComparison.OrdinalIgnoreCase))
                || String.IsNullOrEmpty(targetCommit)) return null;
            if (request.HistoricalWork != null)
                return await CaptureHistoricalEvidenceAsync(actor, stages, vessel, planner, targetCommit,
                    request, token).ConfigureAwait(false);
            foreach (Mission mission in stages)
            {
                IReadOnlyList<string>? delivered = await _Git.TryFindCommitsByTrailerAsync(vessel.LocalPath,
                    targetCommit, LandingReconcileProof.MissionIdTrailerKey, mission.Id, mission.CreatedUtc.AddDays(-1), 1, token).ConfigureAwait(false);
                if (delivered == null || delivered.Count > 0) return null;
            }
            return JsonSerializer.Serialize(new
            {
                VesselId = vessel.Id, Repository = vessel.LocalPath, Source = baseCommit,
                PlanningBranch = planner.BranchName, PlanningBranchCommit = branchCommit,
                TargetRef = target, TargetCommit = targetCommit,
                TrailerSearchMissionIds = stages.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(),
                Evidence = "unchanged_planning_branch_and_no_recorded_or_trailered_stage_landing"
            });
        }
        private async Task<string?> CaptureHistoricalEvidenceAsync(AuthContext actor, List<Mission> stages,
            Vessel vessel, Mission planner, string targetCommit, AdministrativeVoyageRecoveryRequest request, CancellationToken token)
        {
            HistoricalWorkDisposition work = request.HistoricalWork!;
            Mission? target = stages.SingleOrDefault(m => m.Id == work.MissionId);
            if (target == null || !PersonaCatalog.Matches(target.Persona, PersonaCatalog.Worker)
                || target.DependsOnMissionId != planner.Id || target.Status != MissionStatusEnum.Cancelled
                || !IsDigest(work.NativeEvidenceSHA256) || !IsDigest(work.CountsEvidenceSHA256)
                || String.IsNullOrWhiteSpace(work.Reason) || work.Reason.Length > 2000
                || String.IsNullOrWhiteSpace(work.PreservedSourceRef)
                || !(work.PreservedSourceRef.StartsWith("recover/", StringComparison.Ordinal)
                    || work.PreservedSourceRef.StartsWith("refs/heads/recover/", StringComparison.Ordinal))) return null;
            ArmadaEvent? cancellation = await _Database.Events.ReadAsync(actor.TenantId!, actor.UserId!, work.CancellationEventId, token).ConfigureAwait(false);
            if (cancellation == null || cancellation.EventType != "mission.cancelled" || cancellation.MissionId != target.Id
                || cancellation.EntityId != target.Id || cancellation.VoyageId != target.VoyageId
                || !target.CompletedUtc.HasValue || cancellation.CreatedUtc < target.CompletedUtc.Value.AddSeconds(-5)
                || cancellation.CreatedUtc > target.CompletedUtc.Value.AddMinutes(5)) return null;
            EnumerationResult<ArmadaEvent> latest = await _Database.Events.EnumerateAsync(actor.TenantId!, actor.UserId!,
                new EnumerationQuery { MissionId = target.Id, EventType = "mission.cancelled", PageSize = 1,
                    Order = EnumerationOrderEnum.CreatedDescending }, token).ConfigureAwait(false);
            if (latest.Objects.Count != 1 || latest.Objects[0].Id != cancellation.Id) return null;
            string branch = work.PreservedSourceRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? work.PreservedSourceRef.Substring("refs/heads/".Length) : work.PreservedSourceRef;
            if (await _Git.TryBranchExistsAsync(vessel.LocalPath!, branch, token).ConfigureAwait(false) != true) return null;
            string? preserved = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, work.PreservedSourceRef, token).ConfigureAwait(false);
            string? exact = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, work.PreservedSourceCommit, token).ConfigureAwait(false);
            if (preserved == null || !String.Equals(preserved, exact, StringComparison.OrdinalIgnoreCase)
                || !String.Equals(preserved, work.PreservedSourceCommit, StringComparison.OrdinalIgnoreCase)) return null;
            string localTargetRef = "refs/heads/" + (String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch.Trim());
            string? localTargetCommit = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, localTargetRef, token).ConfigureAwait(false);
            if (localTargetCommit == null) return null;
            bool? includesPlanner = await _Git.TryIsAncestorAsync(vessel.LocalPath!, request.SourceCommit, preserved, token).ConfigureAwait(false);
            bool? landed = await _Git.TryIsAncestorAsync(vessel.LocalPath!, preserved, targetCommit, token).ConfigureAwait(false);
            bool? locallyLanded = await _Git.TryIsAncestorAsync(vessel.LocalPath!, preserved, localTargetCommit, token).ConfigureAwait(false);
            if (includesPlanner != true || landed != false || locallyLanded != false) return null;
            foreach (Mission stage in stages)
            {
                foreach (string tip in new[] { targetCommit, localTargetCommit }.Distinct(StringComparer.Ordinal))
                {
                    IReadOnlyList<string>? trailers = await _Git.TryFindCommitsByTrailerAsync(vessel.LocalPath!, tip,
                        LandingReconcileProof.MissionIdTrailerKey, stage.Id, stage.CreatedUtc.AddDays(-1), 1, token).ConfigureAwait(false);
                    if (trailers == null || trailers.Count > 0) return null;
                }
            }
            request.CapturedHistoricalEvidence = true;
            return JsonSerializer.Serialize(new
            {
                VesselId = vessel.Id, Repository = vessel.LocalPath, PlanningSource = request.SourceCommit,
                TargetMissionId = target.Id, PreservedSourceRef = work.PreservedSourceRef, PreservedCommit = preserved,
                TargetRef = LandingReconcileProof.TargetRefOf(vessel), TargetCommit = targetCommit,
                LocalTargetRef = localTargetRef, LocalTargetCommit = localTargetCommit,
                PlannerIncluded = true, SavedCommitLanded = false, Cancellation = cancellation,
                work.NativeEvidenceSHA256, work.CountsEvidenceSHA256, OwnerReason = work.Reason,
                AssertionBoundary = "Unaccepted is an explicit exact-owner disposition; repository and native facts are checked separately."
            });
        }

        private static bool IsDigest(string value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);

        /// <summary>Consume one exact-owner permit through the supported restart surface. No generic restart rule changes.</summary>
        public async Task<Mission> ConsumeRestartPermitAsync(AuthContext caller, string missionId, string permitId,
            string? title = null, string? description = null, CancellationToken token = default)
        {
            AuthContext actor = await _Operators.RequireOperatorAsync(caller, token).ConfigureAwait(false);
            ArmadaEvent stored = await _Database.Events.ReadAsync(actor.TenantId!, actor.UserId!, permitId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The owned restart permit is unavailable.");
            if (stored.EventType != "mission.administrative_restart_permit_issued") throw new InvalidOperationException("Not a restart permit.");
            AdministrativeRestartPermit permit;
            try
            {
                permit = JsonSerializer.Deserialize<AdministrativeRestartPermit>(stored.Payload ?? String.Empty)
                    ?? throw new InvalidOperationException("The restart permit is unreadable.");
            }
            catch (JsonException ex) { throw new InvalidOperationException("The stored restart permit is unreadable; nothing restarted.", ex); }
            if (permit.PermitId != permitId || permit.MissionId != missionId || permit.TenantId != actor.TenantId || permit.UserId != actor.UserId
                || (title != null && title != permit.RestartTitle) || (description != null && description != permit.RestartDescription))
                throw new InvalidOperationException("The permit owner, mission or issued instructions do not match.");
            using (IDisposable family = await MissionRecoveryGuard.AcquireAsync(_Database, missionId, token).ConfigureAwait(false))
            using (IDisposable execution = await MissionExecutionBoundary.Locks.AcquireAsync(missionId, token).ConfigureAwait(false))
            using (IDisposable launch = await MissionExecutionBoundary.LaunchWrites.AcquireAsync(missionId, token).ConfigureAwait(false))
            using (IDisposable publication = await DirectLandingAdmission.AcquireAsync(_Database, actor.TenantId, permit.VoyageId, missionId, token).ConfigureAwait(false))
            using (IDisposable completion = await VoyageCompletionRule.AcquireAdministrativeCorrectionAsync(permit.VoyageId, token).ConfigureAwait(false))
            {
                Mission target = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The original mission is unavailable.");
                if (_Admiral.IsMissionCompletionInFlight(missionId) || LandingRetryRuns.IsRunning(missionId)
                    || await MissionRecoveryGuard.FindActiveDescendantAsync(_Database, target, token).ConfigureAwait(false) != null)
                    throw new InvalidOperationException("A completion, landing or rescue owns this stage.");
                if (_Admiral.DispatchHold?.Snapshot() is DispatchHoldSnapshot hold)
                    throw new InvalidOperationException(DispatchHold.RefusalMessage(hold));
                List<Mission> stages = await _Database.Missions.EnumerateByVoyageAsync(permit.VoyageId, token).ConfigureAwait(false);
                AdministrativeVoyageRecoveryRequest request = new AdministrativeVoyageRecoveryRequest
                { SourceCommit = permit.PlanningSourceCommit, HistoricalWork = permit.Disposition,
                    RestartTitle = permit.RestartTitle, RestartDescription = permit.RestartDescription };
                request.CapturedSourceEvidence = await CaptureSourceEvidenceAsync(actor, stages, request, token).ConfigureAwait(false);
                if (!request.CapturedHistoricalEvidence || request.CapturedSourceEvidence != permit.SourceEvidence)
                    throw new InvalidOperationException("The retained source, target or cancellation evidence changed; the permit is stale.");
                Vessel vessel = await _Database.Vessels.ReadAsync(target.VesselId!, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Source vessel unavailable.");
                await using (FleetCapacityReservation admission = await new FleetCapacityAdmission(_Database, _Settings)
                    .AcquireAsync(vessel, permit.VoyageId, token).ConfigureAwait(false))
                {
                    await admission.VerifyOwnershipAsync(token).ConfigureAwait(false);
                    Mission restarted = await _Database.AdministrativeVoyageRecovery.ConsumeRestartPermitAsync(actor.TenantId!, actor.UserId!,
                        missionId, permit, request, admission, token).ConfigureAwait(false);
                    _Notifier.MissionChanged(restarted);
                    Voyage? reopened = await _Database.Voyages.ReadAsync(permit.VoyageId, token).ConfigureAwait(false);
                    if (reopened != null)
                    {
                        await new VoyageCheckArmingService(_Database, _Settings, new SyslogLogging.LoggingModule()).ArmAsync(reopened, vessel,
                            "administrative_restart_reopen", token).ConfigureAwait(false);
                        _Notifier.VoyageChanged(reopened);
                    }
                    return restarted;
                }
            }
        }

    }
}
