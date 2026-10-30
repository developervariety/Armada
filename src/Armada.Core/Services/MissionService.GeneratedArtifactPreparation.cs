namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private static readonly KeyedAsyncLock _GeneratedPreparation = new KeyedAsyncLock(StringComparer.Ordinal);

        /// <summary>Prepare declared artifacts before handoff or landing, preserving the same Judge and its history.</summary>
        public Task<GeneratedArtifactLandingResult> PrepareGeneratedArtifactsAsync(Mission produced,
            CancellationToken token = default) => PrepareGeneratedArtifactsForTargetAsync(produced, null, token);

        /// <summary>Prepare against the actual landing target; a null target uses the vessel default.</summary>
        public async Task<GeneratedArtifactLandingResult> PrepareGeneratedArtifactsForTargetAsync(Mission produced,
            string? targetBranch, CancellationToken token = default)
        {
            if (produced == null) throw new ArgumentNullException(nameof(produced));
            try { return await PrepareGeneratedArtifactsCoreAsync(produced, targetBranch, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "generated candidate preparation failed for " + produced.Id + ": " + ex.Message);
                Mission? current = await FindGeneratedReviewJudgeAsync(produced, token).ConfigureAwait(false);
                if (GeneratedArtifactReviewRule.IsAwaitingApproval(current))
                    return BoundCandidateResult(current!.GeneratedArtifactReview!, "ReviewRequired", "generated_candidate_transition_incomplete");
                return GeneratedPreparationFailure("generated_preparation_failed: " + ex.GetType().Name);
            }
        }

        private async Task<GeneratedArtifactLandingResult> PrepareGeneratedArtifactsCoreAsync(Mission produced,
            string? targetBranch, CancellationToken token)
        {
            Vessel? vessel = String.IsNullOrWhiteSpace(produced.VesselId) ? null
                : await _Database.Vessels.ReadAsync(produced.VesselId, token).ConfigureAwait(false);
            if (vessel?.GeneratedArtifacts == null || produced.IsReadOnlyMode)
                return new GeneratedArtifactLandingResult();
            Dock? producedDock = String.IsNullOrWhiteSpace(produced.DockId) ? null
                : await _Database.Docks.ReadAsync(produced.DockId, token).ConfigureAwait(false);
            if (producedDock?.HostCheckout != null || producedDock?.HostCheckoutLifecycle != null)
                return GeneratedPreparationFailure("generated_artifacts_harbor_unsupported");
            if (_Git == null || String.IsNullOrWhiteSpace(vessel.LocalPath) || String.IsNullOrWhiteSpace(produced.BranchName))
                return GeneratedPreparationFailure("generated_candidate_repository_unavailable");
            Mission? originalJudge = await FindGeneratedReviewJudgeAsync(produced, token).ConfigureAwait(false);
            bool hasJudge = originalJudge != null;
            string ownerId = originalJudge?.Id ?? produced.Id;
            using (await _GeneratedPreparation.AcquireAsync(ownerId, token).ConfigureAwait(false))
            {
                Mission judge = await _Database.Missions.ReadAsync(ownerId, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("generated_candidate_mission_missing");
                if (judge.HeldForOperatorReview) return GeneratedPreparationFailure("generated_candidate_operator_hold");
                if (!String.Equals(judge.BranchName, produced.BranchName, StringComparison.Ordinal))
                    return GeneratedPreparationFailure("generated_candidate_review_branch_mismatch");
                string? target = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath,
                    "refs/heads/" + (targetBranch ?? vessel.DefaultBranch), token).ConfigureAwait(false);
                string? source = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath,
                    "refs/heads/" + produced.BranchName, token).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(target) || String.IsNullOrWhiteSpace(source))
                    return GeneratedPreparationFailure("generated_candidate_source_unresolved");
                GeneratedArtifactReviewBinding? binding = judge.GeneratedArtifactReview;
                if (binding != null && binding.ApprovedUtc == null)
                {
                    await FinishGeneratedCandidateBranchAsync(vessel, judge, source, token).ConfigureAwait(false);
                    return BoundCandidateResult(binding, "ReviewRequired", "generated_candidate_review_pending");
                }
                if ((binding == null || (binding.ApprovedUtc != null && binding.CandidateCommit == source))
                    && await _Git.TryIsAncestorAsync(vessel.LocalPath, source, target, token).ConfigureAwait(false) == true)
                    return new GeneratedArtifactLandingResult { Reason = "generated_source_already_delivered" };
                if (binding?.ApprovedUtc != null && String.Equals(binding.TargetBaseCommit, target, StringComparison.Ordinal)
                    && String.Equals(binding.CandidateCommit, source, StringComparison.Ordinal))
                    return BoundCandidateResult(binding, "Regenerated", "generated_candidate_review_approved");
                if (binding != null && binding.PreparationAttempt >= Math.Max(0, _Settings.MaxLandingRetries) + 1)
                    return GeneratedPreparationFailure("generated_candidate_drift_limit");
                if (!String.IsNullOrWhiteSpace(judge.VoyageId))
                {
                    List<Mission> stages = await _Database.Missions.EnumerateByVoyageAsync(judge.VoyageId, token).ConfigureAwait(false);
                    foreach (Mission stage in stages)
                        if (stage.Id != judge.Id && stage.BranchName == judge.BranchName
                            && (stage.Status == MissionStatusEnum.Assigned || stage.Status == MissionStatusEnum.InProgress
                                || stage.ProcessId.HasValue))
                            return GeneratedPreparationFailure("generated_candidate_branch_in_use");
                }
                string root = Path.Combine(_Settings.DocksDirectory, "_generated-artifacts", judge.Id);
                Directory.CreateDirectory(root);
                string worktree = Path.Combine(root, Guid.NewGuid().ToString("N"));
                bool created = false;
                bool safeToRemove = true;
                try
                {
                    await _Git.CreateDetachedWorktreeAtCommitAsync(vessel.LocalPath, worktree, target, token).ConfigureAwait(false);
                    created = true;
                    GeneratedArtifactLandingResult result = await GeneratedArtifactLandingRunner.PrepareAsync(worktree,
                        source, vessel.GeneratedArtifacts, judge.Id, vessel.Id,
                        TimeSpan.FromSeconds(_Settings.MergeQueueTestTimeoutSeconds), binding != null, token).ConfigureAwait(false);
                    if (result.Outcome == "Unchanged" || result.Outcome == "Failed") return result;
                    if (result.Outcome == "ReviewRequired" && !hasJudge)
                        return GeneratedPreparationFailure("generated_candidate_judge_missing");
                    if (String.IsNullOrWhiteSpace(result.CandidateCommit))
                        return GeneratedPreparationFailure("generated_candidate_missing");
                    string anchor = "refs/armada/missions/" + judge.Id;
                    await PinGeneratedCandidateAsync(vessel.LocalPath, result.CandidateCommit, anchor, token).ConfigureAwait(false);
                    if (result.Outcome != "ReviewRequired") return result;
                    if (String.IsNullOrWhiteSpace(judge.CommitHash)
                        || await _Git.TryIsAncestorAsync(vessel.LocalPath, judge.CommitHash, result.CandidateCommit, token).ConfigureAwait(false) != true)
                        return GeneratedPreparationFailure("generated_candidate_reviewed_source_missing");
                    GeneratedArtifactLandingResult reviewed = new GeneratedArtifactLandingResult
                    {
                        Outcome = "ReviewRequired", SourceCommit = judge.CommitHash,
                        TargetBaseCommit = result.TargetBaseCommit, CandidateCommit = result.CandidateCommit
                    };
                    GeneratedArtifactReviewBinding next = GeneratedArtifactReviewRule.NextBinding(judge, reviewed,
                        _Settings.MaxLandingRetries, DateTime.UtcNow);
                    using (await MissionExecutionBoundary.LaunchWrites.AcquireAsync(judge.Id, token).ConfigureAwait(false))
                    {
                        if (!await _Database.Missions.TryWriteGeneratedArtifactReviewAsync(judge, next, true, token).ConfigureAwait(false))
                            return GeneratedPreparationFailure("generated_candidate_review_changed");
                    }
                    await MissionAttemptFactRecorder.RecordAsync(_Database, judge, MissionAttemptFactTypeEnum.Retried,
                        "generated_candidate_review", _Logging, token).ConfigureAwait(false);
                    // The preparation worktree remains a Git root until the old dock is reclaimed and
                    // the existing mission anchor is refreshed. A failed refresh retains this worktree.
                    safeToRemove = false;
                    await ReclaimGeneratedReviewLaunchAsync(judge, token).ConfigureAwait(false);
                    await PinGeneratedCandidateAsync(vessel.LocalPath, next.CandidateCommit, anchor, token).ConfigureAwait(false);
                    safeToRemove = true;
                    await _Git.CompareAndSwapBranchRefAsync(vessel.LocalPath, produced.BranchName,
                        next.CandidateCommit, source, token).ConfigureAwait(false);
                    ArmadaEvent prepared = new ArmadaEvent("mission.generated_candidate_review_prepared",
                        "Regenerated landing candidate waits for the same Judge at " + next.CandidateCommit)
                    {
                        TenantId = judge.TenantId, MissionId = judge.Id, VesselId = judge.VesselId,
                        EntityId = judge.Id,
                        Payload = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            next.SourceCommit, BranchSourceCommit = source, next.TargetBaseCommit,
                            next.CandidateCommit, next.PreparationAttempt
                        })
                    };
                    EventOwnerScope.ApplyFromMission(prepared, judge);
                    await _Database.Events.CreateAsync(prepared, token).ConfigureAwait(false);
                    return BoundCandidateResult(next, "ReviewRequired", "generated_candidate_review_pending");
                }
                finally
                {
                    if (created && safeToRemove)
                    {
                        try { await _Git.RemoveWorktreeAsync(worktree, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception ex)
                        { _Logging.Warn(_Header + "generated candidate worktree cleanup failed: " + ex.Message); }
                    }
                    else if (created)
                        _Logging.Warn(_Header + "generated candidate worktree retained until its durable anchor is restored: " + worktree);
                }
            }
        }

        private async Task<Mission?> FindGeneratedReviewJudgeAsync(Mission produced, CancellationToken token)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            Mission? current = produced;
            while (current != null && seen.Add(current.Id))
            {
                if (!OwnershipPolicy.SameTenant(produced.TenantId, current.TenantId)
                    || OwnershipPolicy.UserOfRecord(produced.UserId) != OwnershipPolicy.UserOfRecord(current.UserId)
                    || produced.VesselId != current.VesselId || produced.VoyageId != current.VoyageId) return null;
                if (PersonaCatalog.Matches(current.Persona, PersonaCatalog.Judge)) return current;
                if (String.IsNullOrWhiteSpace(current.DependsOnMissionId)) return null;
                current = await _Database.Missions.ReadAsync(current.DependsOnMissionId, token).ConfigureAwait(false);
            }
            return null;
        }

        private async Task PinGeneratedCandidateAsync(string repository, string candidate, string anchor, CancellationToken token)
        {
            await _Git!.CopyRefAsync(repository, candidate, anchor, token).ConfigureAwait(false);
            if (!String.Equals(await _Git.GetRevisionCommitShaAsync(repository, anchor, token).ConfigureAwait(false), candidate, StringComparison.Ordinal))
                throw new InvalidOperationException("generated_candidate_anchor_unverified");
        }

        private async Task ReclaimGeneratedReviewLaunchAsync(Mission prior, CancellationToken token)
        {
            Dock? oldDock = String.IsNullOrWhiteSpace(prior.DockId) ? null
                : await _Database.Docks.ReadAsync(prior.DockId, token).ConfigureAwait(false);
            if (oldDock != null && (oldDock.VesselId != prior.VesselId || oldDock.BranchName != prior.BranchName
                || !OwnershipPolicy.SameTenant(oldDock.TenantId, prior.TenantId)
                || OwnershipPolicy.UserOfRecord(oldDock.UserId) != OwnershipPolicy.UserOfRecord(prior.UserId)))
                throw new InvalidOperationException("generated_candidate_prior_dock_owner_changed");
            if (oldDock != null)
                await _Docks.ReclaimAsync(oldDock.Id, token: token).ConfigureAwait(false);
            string? priorCaptainId = prior.CaptainId ?? oldDock?.CaptainId;
            if (!String.IsNullOrWhiteSpace(priorCaptainId))
            {
                Captain? captain = await _Database.Captains.ReadAsync(priorCaptainId, token).ConfigureAwait(false);
                if (captain?.CurrentMissionId == prior.Id && captain.CurrentDockId == prior.DockId)
                    await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
            }
        }

        private async Task FinishGeneratedCandidateBranchAsync(Vessel vessel, Mission judge, string currentSource, CancellationToken token)
        {
            GeneratedArtifactReviewBinding binding = judge.GeneratedArtifactReview!;
            if (String.Equals(currentSource, binding.CandidateCommit, StringComparison.Ordinal)) return;
            if (judge.Status != MissionStatusEnum.Pending || judge.HeldForOperatorReview
                || await _Git!.TryIsAncestorAsync(vessel.LocalPath!, currentSource, binding.CandidateCommit, token).ConfigureAwait(false) != true)
                throw new InvalidOperationException("generated_candidate_branch_changed");
            await ReclaimGeneratedReviewLaunchAsync(judge, token).ConfigureAwait(false);
            await PinGeneratedCandidateAsync(vessel.LocalPath!, binding.CandidateCommit, "refs/armada/missions/" + judge.Id, token).ConfigureAwait(false);
            await _Git.CompareAndSwapBranchRefAsync(vessel.LocalPath!, judge.BranchName!, binding.CandidateCommit, currentSource, token).ConfigureAwait(false);
        }

        private async Task<bool> EnsureGeneratedReviewBeforeHandoffAsync(Mission upstream, CancellationToken token)
        {
            if (!PersonaCatalog.Matches(upstream.Persona, PersonaCatalog.Judge)) return true;
            GeneratedArtifactLandingResult preparation = await PrepareGeneratedArtifactsAsync(upstream, token).ConfigureAwait(false);
            if (preparation.Outcome == "ReviewRequired") return false;
            if (preparation.Outcome != "Failed") return true;
            if (GeneratedArtifactReviewRule.IsTemporaryPreparationWait(preparation.Reason))
            {
                await AppendMissionActivityAsync(upstream.Id, "Handoff waits: " + preparation.Reason, token).ConfigureAwait(false);
                return false;
            }
            Mission? current = await _Database.Missions.ReadAsync(upstream.Id, token).ConfigureAwait(false);
            if (current == null) return false;
            if (!current.HeldForOperatorReview)
            {
                current.HeldForOperatorReview = true;
                current.HeldForOperatorReviewReason = preparation.Reason ?? "generated_preparation_failed";
                await _Database.Missions.UpdateAsync(current, token).ConfigureAwait(false);
                await AppendMissionActivityAsync(current.Id, "Handoff refused: " + current.HeldForOperatorReviewReason, token).ConfigureAwait(false);
            }
            return false;
        }

        private static GeneratedArtifactLandingResult BoundCandidateResult(GeneratedArtifactReviewBinding binding, string outcome, string reason) =>
            new GeneratedArtifactLandingResult
            {
                Outcome = outcome, Reason = reason, SourceCommit = binding.SourceCommit,
                TargetBaseCommit = binding.TargetBaseCommit, CandidateCommit = binding.CandidateCommit
            };

        private static GeneratedArtifactLandingResult GeneratedPreparationFailure(string reason) =>
            new GeneratedArtifactLandingResult { Outcome = "Failed", Reason = reason };
    }
}
