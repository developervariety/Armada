namespace Armada.Core.Services
{
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Service for managing mission landing operations including retries and dedicated worktree merges.
    /// </summary>
    public partial class LandingService : ILandingService
    {
        #region Public-Members

        /// <summary>
        /// Delegate invoked to perform the actual landing (push/PR/merge) for a mission.
        /// Wired by ArmadaServer to route through the existing HandleMissionCompleteAsync logic.
        /// </summary>
        public Func<Mission, Dock, Task>? OnPerformLanding { get; set; }

        /// <summary>
        /// Voyage completion hook, raised by <see cref="VoyageCompletionRule.ApplyAsync"/> when a retried landing
        /// ends its voyage. Wired by ArmadaServer to the same hook every other completion writer raises.
        /// </summary>
        public Func<Voyage, Task>? OnVoyageComplete { get; set; }

        #endregion

        #region Private-Members

        private static readonly string _RecoverBranchPrefix = "recover/working-checkout-";

        private string _Header = "[LandingService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IGitService _Git;
        private IncidentService _Incidents;
        private IntegrationCandidateValidator? _IntegrationValidator;
        private HarborCheckoutService? _HarborCheckouts;

        #endregion

        #region Constructors-and-Factories

        private async Task RemoveRetryWorktreeAsync(string repoPath, string worktreePath, CancellationToken token)
        {
            try
            {
                await _Git.RemoveWorktreeAsync(worktreePath, token).ConfigureAwait(false);
                await _Git.PruneWorktreesAsync(repoPath, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _Logging.Warn(_Header + "could not remove landing retry worktree " + worktreePath + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="git">Git service.</param>
        public LandingService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService git)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Incidents = new IncidentService(database);
        }

        /// <summary>
        /// Wire the shared integration-candidate validator. The validator depends on the check service,
        /// which is built after this one, so it is supplied post-construction. Without it a landing
        /// advances the target without validating the merged commit and logs a warning saying so.
        /// </summary>
        /// <param name="validator">Shared integration-candidate validator.</param>
        public void SetIntegrationValidator(IntegrationCandidateValidator validator)
        {
            _IntegrationValidator = validator ?? throw new ArgumentNullException(nameof(validator));
        }

        /// <summary>Wire the authenticated checkout provider used by Harbor-owned mission docks.</summary>
        /// <param name="checkouts">The shared checkout provider.</param>
        public void SetHarborCheckouts(HarborCheckoutService? checkouts)
        {
            _HarborCheckouts = checkouts;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        /// <remarks>
        /// A landing that reaches its first attempt is recorded in <see cref="ArmadaMetrics.LandingDuration"/> with
        /// its outcome and whether a target-branch drift sent it round again; a request refused before any attempt
        /// is not a landing and records nothing.
        /// </remarks>
        public async Task<bool> MergeInDedicatedWorktreeAsync(
            Vessel vessel,
            Mission mission,
            string targetBranch,
            string? sourceBranch = null,
            string? commitMessage = null,
            CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            Stopwatch elapsed = Stopwatch.StartNew();
            LandingMeasure measure = new LandingMeasure();
            try
            {
                bool landed = await MergeInDedicatedWorktreeCoreAsync(vessel, mission, targetBranch, sourceBranch, commitMessage, measure, token).ConfigureAwait(false);
                if (measure.Outcome == null)
                    measure.Outcome = landed ? ArmadaMetrics.OutcomeLanded : ArmadaMetrics.OutcomeFailure;
                return landed;
            }
            catch (OperationCanceledException)
            {
                measure.Outcome = ArmadaMetrics.OutcomeCanceled;
                throw;
            }
            finally
            {
                if (measure.Attempted)
                    ArmadaMetrics.RecordLanding(ArmadaMetrics.LandingLocalMerge, elapsed.Elapsed, measure.Outcome ?? ArmadaMetrics.OutcomeFailure, measure.Retried);
            }
        }

        /// <summary>
        /// Refusal text for a landing retry requested while another retry for the same mission runs.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <returns>The refusal message.</returns>
        public static string RetryInFlightMessage(string missionId)
        {
            return "A landing retry for mission " + missionId + " is already running. Wait for it and read the mission status; "
                + "a second retry would land the same work again.";
        }

        /// <inheritdoc />
        public bool IsRetryInFlight(string missionId)
        {
            if (String.IsNullOrEmpty(missionId)) return false;
            return LandingRetryRuns.IsRunning(missionId);
        }

        /// <inheritdoc />
        public async Task<bool> RetryLandingAsync(string missionId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            // One retry per mission at a time. A retry resets the mission to WorkProduced before it lands, so a
            // second retry started while the first runs passes every status check and lands the same work again.
            if (!LandingRetryRuns.TryStart(missionId))
            {
                _Logging.Warn(_Header + "mission " + missionId + " already has a landing retry in flight -- refusing a second one");
                return false;
            }

            RetryOutcome outcome;
            try
            {
                outcome = await RetryLandingOnceAsync(missionId, tenantId, token).ConfigureAwait(false);
            }
            finally
            {
                LandingRetryRuns.End(missionId);
            }

            // Voyage completion keeps a voyage while one of its missions has a retry running, so the voyage is
            // decided here, after the retry has left the registry, by the one completion rule. A retry that lands
            // completes the voyage (a Failed one too, which the periodic sweeps revisit only for a bounded window);
            // a retry that fails leaves the mission LandingFailed and the voyage Failed.
            if (outcome.Attempted)
                await ApplyVoyageCompletionAsync(outcome.VoyageId, missionId, token).ConfigureAwait(false);
            return outcome.Landed;
        }

        #endregion

        #region Private-Methods

        private async Task<RetryOutcome> RetryLandingOnceAsync(string missionId, string? tenantId, CancellationToken token)
        {
            RetryOutcome outcome = new RetryOutcome();
            Mission? mission = !String.IsNullOrEmpty(tenantId)
                ? await _Database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false)
                : await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            if (mission == null)
            {
                _Logging.Warn(_Header + "mission " + missionId + " not found");
                return outcome;
            }

            string? noLanding = MissionLandingEligibility.RefusalReason(mission);
            if (noLanding != null)
            {
                _Logging.Warn(_Header + "mission " + missionId + " cannot be landed: " + noLanding);
                return outcome;
            }

            if (mission.Status != MissionStatusEnum.LandingFailed && mission.Status != MissionStatusEnum.WorkProduced)
            {
                _Logging.Warn(_Header + "mission " + missionId + " is in status " + mission.Status + ", not LandingFailed or WorkProduced -- cannot retry");
                return outcome;
            }

            if (String.IsNullOrEmpty(mission.VesselId))
            {
                _Logging.Warn(_Header + "mission " + missionId + " has no vessel -- cannot retry landing");
                return outcome;
            }

            // Use the mission's own TenantId for vessel lookup (more reliable than caller's tenantId)
            string? lookupTenantId = !String.IsNullOrEmpty(mission.TenantId) ? mission.TenantId : tenantId;
            Vessel? vessel = !String.IsNullOrEmpty(lookupTenantId)
                ? await _Database.Vessels.ReadAsync(lookupTenantId, mission.VesselId, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            // Fall back to unscoped read if tenant-scoped read fails
            if (vessel == null)
                vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null)
            {
                _Logging.Warn(_Header + "vessel " + mission.VesselId + " not found -- cannot retry landing for mission " + missionId);
                return outcome;
            }

            if (String.IsNullOrEmpty(vessel.LocalPath))
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " has no LocalPath -- cannot retry landing for mission " + missionId);
                return outcome;
            }

            if (String.IsNullOrEmpty(mission.BranchName))
            {
                _Logging.Warn(_Header + "mission " + missionId + " has no branch name -- cannot retry landing");
                return outcome;
            }

            // Emit retry event
            try
            {
                ArmadaEvent retryEvent = new ArmadaEvent("mission.landing_retry", "Retrying landing: " + mission.Title);
                retryEvent.EntityType = "mission";
                retryEvent.EntityId = mission.Id;
                retryEvent.MissionId = mission.Id;
                retryEvent.VesselId = mission.VesselId;
                retryEvent.VoyageId = mission.VoyageId;
                EventOwnerScope.ApplyFromMission(retryEvent, mission);
                await _Database.Events.CreateAsync(retryEvent, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record landing retry event for mission " + missionId + ": " + ex.Message);
            }

            // Attempt rebase of mission branch onto current target branch
            string repoPath = vessel.LocalPath;
            string targetBranch = vessel.DefaultBranch;
            string missionBranch = mission.BranchName;

            try
            {
                // Fetch latest from remote
                await _Git.FetchAsync(repoPath, token).ConfigureAwait(false);

                // Check if branch still exists
                bool branchExists = await _Git.BranchExistsAsync(repoPath, missionBranch, token).ConfigureAwait(false);
                if (!branchExists)
                {
                    _Logging.Warn(_Header + "branch " + missionBranch + " no longer exists -- cannot retry landing for mission " + missionId);
                    return outcome;
                }

                _Logging.Info(_Header + "retrying landing for mission " + missionId + " branch " + missionBranch);

                // Cancellation and retry admission use the same short publication boundary.
                using (await DirectLandingAdmission.AcquireAsync(_Database, mission.TenantId, mission.VoyageId, mission.Id, token).ConfigureAwait(false))
                {
                    if (await DirectLandingAdmission.IsCancelledAsync(_Database, mission, token).ConfigureAwait(false)) return outcome;
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                }
                outcome.Attempted = true;
                outcome.VoyageId = mission.VoyageId;

                // Try to find the dock or create a temporary context for landing
                Dock? dock = null;
                if (!String.IsNullOrEmpty(mission.DockId))
                {
                    dock = !String.IsNullOrEmpty(tenantId)
                        ? await _Database.Docks.ReadAsync(tenantId, mission.DockId, token).ConfigureAwait(false)
                        : await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false);
                }

                // Landing reads the change from the dock. A failed landing's dock is reclaimed, so a
                // retry usually finds no worktree; reading it then refused the retry as
                // landing_evidence_unavailable and replaced the original failure reason. Read the change
                // from a temporary detached worktree at the mission branch tip instead.
                string? evidenceWorktree = null;
                if (dock == null || String.IsNullOrEmpty(dock.WorktreePath) || !Directory.Exists(dock.WorktreePath))
                {
                    string? branchTip = await _Git.GetRevisionCommitShaAsync(repoPath, "refs/heads/" + missionBranch, token).ConfigureAwait(false);
                    if (String.IsNullOrWhiteSpace(branchTip))
                        throw new InvalidOperationException("the tip of branch " + missionBranch + " could not be resolved in " + repoPath);
                    evidenceWorktree = Path.Combine(Path.GetTempPath(), "armada-landing-retry", mission.Id + "-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.GetDirectoryName(evidenceWorktree)!);
                    await _Git.CreateDetachedWorktreeAtCommitAsync(repoPath, evidenceWorktree, branchTip!, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "dock for mission " + missionId + " is gone; retrying landing from a temporary worktree at "
                        + missionBranch + " " + branchTip);

                    Dock retryDock = new Dock(vessel.Id);
                    if (dock != null) retryDock.Id = dock.Id;
                    retryDock.TenantId = dock?.TenantId ?? vessel.TenantId;
                    retryDock.CaptainId = dock?.CaptainId;
                    retryDock.BranchName = missionBranch;
                    retryDock.WorktreePath = evidenceWorktree;
                    retryDock.Active = false; // Not a real provisioned dock
                    dock = retryDock;
                }

                // Invoke the landing handler if available
                if (OnPerformLanding != null)
                {
                    try
                    {
                        await OnPerformLanding.Invoke(mission, dock).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (evidenceWorktree != null) await RemoveRetryWorktreeAsync(repoPath, evidenceWorktree, token).ConfigureAwait(false);
                    }
                    _Logging.Info(_Header + "landing retry completed for mission " + missionId);

                    // Re-read mission to get updated status from landing handler
                    mission = !String.IsNullOrEmpty(tenantId)
                        ? await _Database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false)
                        : await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);

                    outcome.Landed = mission != null && mission.Status == MissionStatusEnum.Complete;
                    return outcome;
                }
                else
                {
                    if (evidenceWorktree != null) await RemoveRetryWorktreeAsync(repoPath, evidenceWorktree, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "no landing handler configured -- cannot retry landing for mission " + missionId);
                    mission.Status = MissionStatusEnum.LandingFailed;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    return outcome;
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "landing retry failed for mission " + missionId + ": " + ex.Message);

                // Capture any merge-conflict file list so the operator sees exactly what to fix.
                string failureReason = "Landing retry failed: " + ex.Message;
                try
                {
                    IReadOnlyList<string> conflicts = await _Git.GetConflictedFilesAsync(repoPath, token).ConfigureAwait(false);
                    if (conflicts.Count > 0)
                        failureReason = "Landing retry failed with merge conflicts in: " + String.Join(", ", conflicts);
                }
                catch (Exception conflictEx)
                {
                    _Logging.Warn(_Header + "could not list merge-conflict files for mission " + missionId + " in " + repoPath
                        + "; the failure reason will not name them: " + conflictEx.Message);
                }

                // Ensure mission goes back to LandingFailed with the captured reason
                try
                {
                    using IDisposable admission = await DirectLandingAdmission.AcquireAsync(_Database, mission.TenantId, mission.VoyageId, mission.Id, token).ConfigureAwait(false);
                    mission = !String.IsNullOrEmpty(tenantId)
                        ? await _Database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false)
                        : await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
                    if (mission != null && !await DirectLandingAdmission.IsCancelledAsync(_Database, mission, token).ConfigureAwait(false)
                        && mission.Status != MissionStatusEnum.LandingFailed)
                    {
                        mission.Status = MissionStatusEnum.LandingFailed;
                        mission.FailureReason = failureReason;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    }
                }
                catch (Exception restoreEx)
                {
                    _Logging.Warn(_Header + "landing retry failed for mission " + missionId
                        + " and its status could not be restored to LandingFailed; the mission may show a stale status: " + restoreEx.Message);
                }

                return outcome;
            }
        }

        /// <summary>
        /// The integration-worktree landing itself. <paramref name="measure"/> learns whether an attempt started,
        /// whether it retried, and an outcome the boolean result cannot carry.
        /// </summary>
        private async Task<bool> MergeInDedicatedWorktreeCoreAsync(
            Vessel vessel,
            Mission mission,
            string targetBranch,
            string? sourceBranch,
            string? commitMessage,
            LandingMeasure measure,
            CancellationToken token)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            LandingModeEnum? effectiveLandingMode = await ResolveNativeLandingModeAsync(vessel, mission, token).ConfigureAwait(false);
            Mission? currentReviewMission = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            Mission? currentReviewJudge = currentReviewMission == null ? null
                : await GeneratedArtifactReviewResolver.ResolveJudgeAsync(_Database, currentReviewMission, token).ConfigureAwait(false);
            if (currentReviewJudge != null && (currentReviewJudge.HeldForOperatorReview
                || GeneratedArtifactReviewRule.IsAwaitingApproval(currentReviewJudge)))
            {
                mission.FailureReason = currentReviewJudge.HeldForOperatorReview
                    ? "generated_candidate_operator_hold" : "generated_candidate_review_pending";
                measure.Outcome = "review_required";
                await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                return false;
            }

            Dock? missionDock = !String.IsNullOrWhiteSpace(mission.DockId)
                ? await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false)
                : null;
            if (missionDock?.HostCheckoutLifecycle != null && missionDock.HostCheckout == null)
                throw new InvalidOperationException("harbor_checkout_lifecycle_incomplete");
            if (missionDock?.HostCheckout != null)
                return await MergeInHarborDedicatedWorktreeAsync(vessel, mission, missionDock, targetBranch, sourceBranch,
                    commitMessage, measure, token).ConfigureAwait(false);

            if (String.IsNullOrEmpty(vessel.LocalPath))
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " has no LocalPath -- cannot merge mission " + mission.Id);
                return false;
            }

            if (String.IsNullOrEmpty(targetBranch))
            {
                _Logging.Warn(_Header + "mission " + mission.Id + " has no target branch -- cannot merge");
                return false;
            }

            string? missionBranch = !String.IsNullOrEmpty(sourceBranch) ? sourceBranch : mission.BranchName;
            if (String.IsNullOrEmpty(missionBranch))
            {
                _Logging.Warn(_Header + "mission " + mission.Id + " has no branch name -- cannot merge");
                return false;
            }

            string integrationRoot = Path.Combine(_Settings.DocksDirectory, "_integration");
            string worktreePath = Path.Combine(integrationRoot, mission.Id);
            Directory.CreateDirectory(integrationRoot);

            while (true)
            {
                using (await DirectLandingAdmission.AcquireAsync(_Database, mission.TenantId, mission.VoyageId, mission.Id, token).ConfigureAwait(false))
                {
                    if (await DirectLandingAdmission.IsCancelledAsync(_Database, mission, token).ConfigureAwait(false))
                    {
                        measure.Outcome = ArmadaMetrics.OutcomeCanceled;
                        return false;
                    }
                }
                measure.Attempted = true;
                bool succeeded = false;
                bool refusedByValidation = false;
                bool worktreeCreatedThisAttempt = false;
                bool prepareGeneratedAfterDrift = false;
                Exception? failure = null;

                try
                {
                    await _Git.FetchAsync(vessel.LocalPath, token).ConfigureAwait(false);

                    // The integration worktree is created DETACHED at the target tip. Checking out the
                    // target branch by name would make git refuse whenever any other worktree of the
                    // landing repository already holds that branch, and one stray worktree would then
                    // block every landing for the vessel. The target ref is advanced afterwards by a
                    // compare-and-swap from the tip this worktree started at, so a concurrent move of
                    // the target is refused rather than overwritten.
                    string targetRef = "refs/heads/" + targetBranch;
                    string? targetTip = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath, targetRef, token).ConfigureAwait(false);
                    if (String.IsNullOrWhiteSpace(targetTip))
                        throw new InvalidOperationException("Unable to resolve target branch " + targetBranch + " in landing repository " + vessel.LocalPath);

                    // A prior attempt the admiral restart interrupted can leave a worktree at the
                    // deterministic per-mission integration path. Left in place it makes the add below
                    // refuse with "already exists" and fails the whole landing, so recover a leftover this
                    // landing owns before creating a fresh one. Only a worktree this landing owns is cleared;
                    // a path that is not one is preserved, never deleted.
                    await RecoverOwnedStaleIntegrationWorktreeAsync(vessel.LocalPath, worktreePath, mission.Id, token).ConfigureAwait(false);

                    _Logging.Info(_Header + "creating integration worktree " + worktreePath + " for mission " + mission.Id + " target " + targetBranch + " at " + targetTip + " attempt " + (mission.LandingRetryCount + 1));
                    await _Git.CreateDetachedWorktreeAtCommitAsync(vessel.LocalPath, worktreePath, targetTip!, token).ConfigureAwait(false);
                    worktreeCreatedThisAttempt = true;

                    // No target branch is passed: the worktree is detached on purpose, and checking the
                    // branch out inside it would hit the same refusal.
                    GeneratedArtifactLandingResult generated = await PrepareGeneratedLandingAsync(vessel, mission, targetBranch, token).ConfigureAwait(false);
                    if (generated.Outcome == "ReviewRequired")
                    {
                        mission.FailureReason = generated.Reason;
                        measure.Outcome = "review_required";
                        return false;
                    }
                    if (generated.Outcome == "Failed")
                    {
                        mission.FailureReason = generated.Reason;
                        if (GeneratedArtifactReviewRule.IsTemporaryPreparationWait(generated.Reason))
                            measure.Outcome = ArmadaMetrics.OutcomeIncomplete;
                        return false;
                    }
                    if (generated.Outcome == "Regenerated" && generated.TargetBaseCommit != targetTip)
                        throw new InvalidOperationException("target branch drift: generated candidate belongs to another landing base");
                    string mergeSource = generated.Outcome == "Regenerated" ? generated.CandidateCommit! : missionBranch;
                    await _Git.MergeBranchLocalAsync(worktreePath, vessel.LocalPath, mergeSource, null, commitMessage, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "merged branch " + missionBranch + " into integration worktree " + worktreePath);

                    string? mergedHead = await _Git.GetRevisionCommitShaAsync(worktreePath, "HEAD", token).ConfigureAwait(false);
                    if (String.IsNullOrWhiteSpace(mergedHead))
                        throw new InvalidOperationException("Unable to resolve the merged commit in integration worktree " + worktreePath);

                    // The merged commit is validated before anything is published or advanced. A refused
                    // candidate leaves the target, the remote and the mission branch exactly as they were.
                    if (!String.Equals(mergedHead, targetTip, StringComparison.OrdinalIgnoreCase))
                    {
                        IntegrationValidationResult? validation = await ValidateCandidateAsync(
                            vessel, mission, targetBranch, missionBranch, targetTip!, mergedHead!, token).ConfigureAwait(false);
                        if (validation != null && !validation.AllowsPublication)
                        {
                            mission.FailureReason = validation.Reason;
                            mission.LastUpdateUtc = DateTime.UtcNow;
                            await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                            measure.Outcome = ArmadaMetrics.OutcomeFailure;
                            refusedByValidation = true;
                        }

                        // A target that moved while the candidate was validated makes the candidate stale:
                        // the validated tree is not the tree the target would get. Refuse it as drift so the
                        // bounded retry rebuilds and validates the merge against the new target tip. The
                        // compare-and-swap below refuses the same race when it happens after this read.
                        if (!refusedByValidation)
                        {
                            string? tipAfterValidation = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath, targetRef, token).ConfigureAwait(false);
                            if (!String.Equals(tipAfterValidation, targetTip, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidOperationException("target branch drift: " + targetBranch + " moved from " + targetTip + " to "
                                    + (tipAfterValidation ?? "(unresolved)") + " while merged commit " + mergedHead + " was validated; the stale candidate is not published");
                            }
                        }
                    }

                    if (!refusedByValidation)
                    {
                        using IDisposable admission = await DirectLandingAdmission.AcquireAsync(_Database, mission.TenantId, mission.VoyageId, mission.Id, token).ConfigureAwait(false);
                        if (await DirectLandingAdmission.IsCancelledAsync(_Database, mission, token).ConfigureAwait(false))
                        {
                            _Logging.Info(_Header + "publication refused for cancelled mission or voyage " + mission.Id);
                            measure.Outcome = ArmadaMetrics.OutcomeCanceled;
                            return false;
                        }
                        string? generatedRefusal = await RefuseUnapprovedGeneratedPublicationAsync(vessel, mission,
                            targetBranch, missionBranch, token).ConfigureAwait(false);
                        if (generatedRefusal != null)
                        {
                            if (generatedRefusal == "generated_candidate_target_drift"
                                || generatedRefusal == "generated_candidate_source_changed")
                            {
                                prepareGeneratedAfterDrift = true;
                                throw new InvalidOperationException("generated_candidate_reprepare_required: " + generatedRefusal);
                            }
                            mission.FailureReason = generatedRefusal;
                            measure.Outcome = "review_required";
                            return false;
                        }
                        // LocalMerge lands into the local repository only -- pushing to a remote is
                        // the operator's decision, never automatic. Pushing here made every landing
                        // fail once the local default branch diverged from the remote: git rejects
                        // the push as non-fast-forward, IsTargetBranchDrift misclassifies that
                        // rejection as target-branch drift, and the retry loop re-merges and
                        // re-pushes the same divergence until maxLandingRetries is exhausted. The
                        // mission is then marked LandingFailed and its branch is left stray even
                        // though the local merge itself succeeded.
                        //
                        // Any other mode pushes BEFORE the bare target moves. Advancing the bare first
                        // left a merge only the bare held whenever origin rejected the push, and the
                        // branch sweep and the next sync then treated that work as landed.
                        if (effectiveLandingMode != LandingModeEnum.LocalMerge)
                        {
                            await _Git.PushHeadToBranchAsync(worktreePath, "origin", targetBranch, token).ConfigureAwait(false);
                            _Logging.Info(_Header + "pushed merged changes from integration worktree " + worktreePath);
                        }
                        else
                        {
                            _Logging.Info(_Header + "landing mode is LocalMerge -- skipping push to origin for mission " + mission.Id);
                        }

                        if (!String.Equals(mergedHead, targetTip, StringComparison.OrdinalIgnoreCase))
                        {
                            await _Git.CompareAndSwapBranchRefAsync(vessel.LocalPath, targetBranch, mergedHead!, targetTip!, token).ConfigureAwait(false);
                            _Logging.Info(_Header + "advanced " + targetBranch + " from " + targetTip + " to " + mergedHead);
                        }

                        succeeded = true;
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "integration merge failed for mission " + mission.Id + " branch " + missionBranch + ": " + ex.Message);
                    failure = ex;
                    succeeded = false;

                    string? classified = prepareGeneratedAfterDrift ? null : ClassifyIntegrationFailure(ex);
                    if (classified != null)
                    {
                        mission.FailureReason = classified;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    // Remove only a worktree this attempt created. When the attempt refused a pre-existing
                    // path it did not own, that path is deliberately preserved, and the prune never touches a
                    // worktree whose directory is still present, so both leave an unowned path intact.
                    if (worktreeCreatedThisAttempt)
                    {
                        try
                        {
                            await _Git.RemoveWorktreeAsync(worktreePath, token).ConfigureAwait(false);
                            _Logging.Info(_Header + "removed integration worktree " + worktreePath);
                        }
                        catch (Exception removeEx)
                        {
                            _Logging.Warn(_Header + "failed to remove integration worktree " + worktreePath + ": " + removeEx.Message);
                        }

                        try
                        {
                            await _Git.PruneWorktreesAsync(vessel.LocalPath, token).ConfigureAwait(false);
                        }
                        catch (Exception pruneEx)
                        {
                            _Logging.Warn(_Header + "failed to prune integration worktrees for " + vessel.LocalPath + ": " + pruneEx.Message);
                        }
                    }
                }

                if (!succeeded && failure != null && (prepareGeneratedAfterDrift || IsTargetBranchDrift(failure)))
                {
                    Mission? current = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                    Mission? judge = current == null ? null
                        : await GeneratedArtifactReviewResolver.ResolveJudgeAsync(_Database, current, token).ConfigureAwait(false);
                    if (judge?.GeneratedArtifactReview?.ApprovedUtc != null && vessel.GeneratedArtifacts != null)
                    {
                        GeneratedArtifactLandingResult prepared = await PrepareGeneratedLandingAsync(vessel,
                            current!, targetBranch, token).ConfigureAwait(false);
                        if (prepared.Outcome == "ReviewRequired" || prepared.Outcome == "Failed")
                        {
                            mission.FailureReason = prepared.Reason;
                            measure.Outcome = prepared.Outcome == "ReviewRequired" ? "review_required" : ArmadaMetrics.OutcomeFailure;
                            return false;
                        }
                    }
                }

                if (succeeded)
                {
                    // Ask git where the work ACTUALLY is before deciding what to report. The merge
                    // above and the sync below are separate steps, and only the first one decides
                    // whether the work landed.
                    bool landed = await VerifyWorkLandedAsync(vessel, mission, targetBranch, token).ConfigureAwait(false);

                    if (await SyncUserWorkingDirectoryAfterLandingAsync(vessel, mission, targetBranch, token).ConfigureAwait(false))
                        return true;

                    // The sync failed. If the work did not land, that is a real landing failure.
                    if (!landed) return false;

                    // The work landed and only the post-step failed. Reporting that as a landing
                    // failure is not a cosmetic error: it sends the retry loop back over work that
                    // is already on the target branch, and re-merging an already-landed branch is
                    // how the working checkout and the bare repo diverge in the first place.
                    // "Work landed, post-step failed" and "work lost" are opposite problems, and an
                    // operator must not have to run git to tell them apart.
                    mission.FailureReason = "work_landed_post_step_failed: "
                        + (mission.FailureReason ?? "the working directory was not synced")
                        + " -- the work IS on " + targetBranch + " (verified by ancestry); no landing "
                        + "retry is needed. Sync the configured checkout by hand.";
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + mission.FailureReason);
                    measure.Outcome = ArmadaMetrics.OutcomeLandedPostStepFailed;
                    return true;
                }

                if (failure == null || !IsTargetBranchDrift(failure))
                {
                    return false;
                }

                using (await DirectLandingAdmission.AcquireAsync(_Database, mission.TenantId, mission.VoyageId, mission.Id, token).ConfigureAwait(false))
                {
                    if (await DirectLandingAdmission.IsCancelledAsync(_Database, mission, token).ConfigureAwait(false))
                    {
                        measure.Outcome = ArmadaMetrics.OutcomeCanceled;
                        return false;
                    }
                }

                int maxRetries = _Settings.MaxLandingRetries;
                if (mission.LandingRetryCount >= maxRetries)
                {
                    mission.FailureReason = "target_branch_drift_retry_exhausted: maxLandingRetries=" + maxRetries;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Error(_Header + "target branch drift retry exhausted for mission " + mission.Id + " after " + mission.LandingRetryCount + " retries");
                    await RaiseLandingPushExhaustedAsync(vessel, mission, targetBranch, failure.Message, token).ConfigureAwait(false);
                    return false;
                }

                mission.LandingRetryCount++;
                measure.Retried = true;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "target branch drift detected for mission " + mission.Id + "; auto-rebasing and retrying landing attempt " + mission.LandingRetryCount + " of " + maxRetries);
            }
        }

        /// <summary>
        /// Run the shared integration validation for the merged commit this attempt built. Returns null
        /// when no validator is wired, which is logged, never silent.
        /// </summary>
        private async Task<IntegrationValidationResult?> ValidateCandidateAsync(
            Vessel vessel,
            Mission mission,
            string targetBranch,
            string missionBranch,
            string targetTip,
            string mergedHead,
            CancellationToken token,
            string? repositoryPath = null)
        {
            if (_IntegrationValidator == null)
            {
                _Logging.Warn(_Header + "no integration validator is wired; merged commit " + mergedHead + " for mission " + mission.Id
                    + " advances " + targetBranch + " without integration checks");
                return null;
            }

            string? sourceCommit = null;
            try
            {
                sourceCommit = await _Git.GetRevisionCommitShaAsync(repositoryPath ?? vessel.LocalPath!, "refs/heads/" + missionBranch, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not resolve the tip of " + missionBranch + " for the integration evidence of mission " + mission.Id + ": " + ex.Message);
            }

            IntegrationCandidate candidate = new IntegrationCandidate
            {
                MissionId = mission.Id,
                LandingPath = "LocalMerge",
                TargetBranch = targetBranch,
                SourceBranch = missionBranch,
                SourceCommit = String.IsNullOrWhiteSpace(sourceCommit) ? mission.CommitHash : sourceCommit,
                TargetBaseCommit = targetTip,
                IntegrationCommit = mergedHead,
                TenantId = mission.TenantId,
                UserId = mission.UserId
            };
            return await _IntegrationValidator.ValidateAsync(vessel, candidate, token).ConfigureAwait(false);
        }

        /// <summary>
        /// What one landing retry did, for the voyage decision made after it leaves the retry registry.
        /// </summary>
        private sealed class RetryOutcome
        {
            /// <summary>True once the retry reset the mission to WorkProduced and started its landing.</summary>
            public bool Attempted { get; set; } = false;

            /// <summary>True when the mission reads Complete after the landing.</summary>
            public bool Landed { get; set; } = false;

            /// <summary>The mission's voyage, or null.</summary>
            public string? VoyageId { get; set; } = null;
        }

        /// <summary>
        /// What one landing call learned for its measurement.
        /// </summary>
        private sealed class LandingMeasure
        {
            /// <summary>True once the first integration attempt starts.</summary>
            public bool Attempted { get; set; } = false;

            /// <summary>True when a target-branch drift sent the landing round again.</summary>
            public bool Retried { get; set; } = false;

            /// <summary>An outcome the boolean result cannot carry, or null to derive it from the result.</summary>
            public string? Outcome { get; set; } = null;
        }

        private async Task ApplyVoyageCompletionAsync(string? voyageId, string missionId, CancellationToken token)
        {
            if (String.IsNullOrEmpty(voyageId)) return;
            try
            {
                VoyageCompletionResult result = await VoyageCompletionRule.ApplyAsync(_Database, voyageId, OnVoyageComplete, token).ConfigureAwait(false);
                if (result.HookException != null)
                    _Logging.Warn(_Header + "voyage completion hook failed for voyage " + voyageId + " after the landing retry of mission " + missionId + ": " + result.HookException.Message);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not apply voyage completion to voyage " + voyageId + " after the landing retry of mission " + missionId
                    + "; the periodic sweep decides it instead: " + ex.Message);
            }
        }

        /// <summary>
        /// Reports whether the configured checkout holds commits the landing repository does not,
        /// or null when the two agree.
        /// </summary>
        /// <remarks>
        /// The two are separate clones. Syncing merges the landing repository INTO the checkout, so
        /// a checkout that carries its own commits gains a merge commit the landing repository never
        /// learns about, and the gap widens on every landing instead of closing. The next landing
        /// then fails with "Diverging branches can't be fast-forwarded", where the cause looks like
        /// a defect in the new work rather than damage left by an earlier one.
        /// <para>
        /// This is reported and never repaired here. The obvious repair is a hard reset, and those
        /// commits exist in exactly one place on disk -- a reset is not a fix, it is the data loss.
        /// </para>
        /// </remarks>
        private async Task<string?> DescribeWorkingDirectoryDivergenceAsync(Vessel vessel, string targetBranch, CancellationToken token)
        {
            if (String.IsNullOrEmpty(vessel.LocalPath) || String.IsNullOrEmpty(vessel.WorkingDirectory)) return null;

            try
            {
                string? landingTip = await _Git.GetRevisionShaAsync(vessel.LocalPath!, targetBranch, token).ConfigureAwait(false);
                string? checkoutHead = await _Git.GetRevisionShaAsync(vessel.WorkingDirectory!, "HEAD", token).ConfigureAwait(false);

                if (String.IsNullOrWhiteSpace(landingTip) || String.IsNullOrWhiteSpace(checkoutHead)) return null;
                if (String.Equals(landingTip, checkoutHead, StringComparison.OrdinalIgnoreCase)) return null;

                // The checkout containing the landing tip while not equalling it means it is AHEAD:
                // it holds commits that exist nowhere else.
                bool? checkoutIsAhead = await _Git.TryIsAncestorAsync(
                    vessel.WorkingDirectory!, landingTip!, "HEAD", token).ConfigureAwait(false);
                if (checkoutIsAhead != true) return null;

                return "working_directory_diverged: the configured checkout at " + vessel.WorkingDirectory
                    + " holds commits that are not in the landing repository at " + vessel.LocalPath
                    + " (checkout HEAD " + checkoutHead + ", landing tip " + landingTip + "). Those commits exist in "
                    + "one place only. Do NOT reset the checkout: push them to a recover/<name> branch in the "
                    + "landing repository and land them through the merge queue, then re-sync.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not compare the configured checkout against the landing repository: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Confirms from git that the mission's commit is reachable from the target branch.
        /// </summary>
        /// <remarks>
        /// A merge step that returned without throwing is a claim about a COMMAND, not about where
        /// the code ended up. Only ancestry answers the second question, and an unverifiable answer
        /// is treated as "not landed" so a post-step failure is never excused by a check that never
        /// ran.
        /// </remarks>
        private async Task<bool> VerifyWorkLandedAsync(Vessel vessel, Mission mission, string targetBranch, CancellationToken token)
        {
            if (String.IsNullOrEmpty(vessel.LocalPath)) return false;
            if (String.IsNullOrWhiteSpace(mission.CommitHash)) return false;
            if (String.IsNullOrWhiteSpace(targetBranch)) return false;

            try
            {
                bool? landed = await _Git.TryIsAncestorAsync(
                    vessel.LocalPath, mission.CommitHash!, targetBranch, token).ConfigureAwait(false);
                return landed == true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not verify whether mission " + mission.Id
                    + " landed on " + targetBranch + ": " + ex.Message);
                return false;
            }
        }

        private async Task<LandingModeEnum?> ResolveNativeLandingModeAsync(Vessel vessel, Mission mission, CancellationToken token)
        {
            Voyage? voyage = String.IsNullOrWhiteSpace(mission.VoyageId) ? null
                : await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false);
            return LandingConfigurationResolver.Resolve(_Settings, vessel, voyage).LandingMode;
        }

        private async Task<bool> SyncUserWorkingDirectoryAfterLandingAsync(Vessel vessel, Mission mission, string targetBranch, CancellationToken token)
        {
            if (String.IsNullOrEmpty(vessel.WorkingDirectory))
            {
                return true;
            }

            LandingModeEnum? effectiveLandingMode = await ResolveNativeLandingModeAsync(vessel, mission, token).ConfigureAwait(false);
            bool syncAttempted = false;
            try
            {
                // A fast-forward preserves untracked files, so an untracked captain scratch dir left
                // in the configured checkout must not block the sync. Guard on TRACKED changes only,
                // matching MergeBranchLocalAsync, which the sync calls and which itself tolerates them.
                bool hasTrackedChanges = await _Git.HasUncommittedTrackedChangesAsync(vessel.WorkingDirectory, token).ConfigureAwait(false);
                string? currentBranch = await _Git.GetCurrentBranchAsync(vessel.WorkingDirectory, token).ConfigureAwait(false);

                if (!hasTrackedChanges && String.Equals(currentBranch, targetBranch, StringComparison.Ordinal))
                {
                    syncAttempted = true;
                    if (effectiveLandingMode == LandingModeEnum.LocalMerge)
                    {
                        await _Git.MergeBranchLocalAsync(
                            vessel.WorkingDirectory,
                            vessel.LocalPath!,
                            targetBranch,
                            targetBranch,
                            token: token).ConfigureAwait(false);
                        _Logging.Info(_Header + "synced user working directory " + vessel.WorkingDirectory + " from local landing repository");
                    }
                    else
                    {
                        await _Git.PullFastForwardOnlyAsync(vessel.WorkingDirectory, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "synced user working directory " + vessel.WorkingDirectory + " with fast-forward pull");
                    }

                    string? divergence = await DescribeWorkingDirectoryDivergenceAsync(vessel, targetBranch, token).ConfigureAwait(false);
                    if (divergence != null)
                    {
                        string? preserved = effectiveLandingMode == LandingModeEnum.LocalMerge
                            ? await PreserveWorkingCheckoutOnlyCommitsAsync(vessel, mission, targetBranch, token).ConfigureAwait(false)
                            : null;
                        mission.FailureReason = preserved != null ? divergence + " " + preserved : divergence;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                        _Logging.Warn(_Header + divergence);
                        return false;
                    }

                    return true;
                }

                if (effectiveLandingMode == LandingModeEnum.LocalMerge)
                {
                    mission.FailureReason = "working_directory_sync_failed: configured checkout is "
                        + (!hasTrackedChanges ? "on branch " + (currentBranch ?? "(unknown)") : "has uncommitted tracked changes")
                        + "; expected a checkout with no tracked changes on " + targetBranch;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + mission.FailureReason);
                    return false;
                }

                _Logging.Info(_Header + "leaving user working directory " + vessel.WorkingDirectory + " unchanged; run git pull on " + targetBranch + " to sync when ready");
                return true;
            }
            catch (Exception ex)
            {
                if (effectiveLandingMode == LandingModeEnum.LocalMerge)
                {
                    mission.FailureReason = "working_directory_sync_failed: " + ex.Message;

                    // A fast-forward refused because the checkout carries its own commits leaves those
                    // commits in one place on disk. Preserve and raise them; never reset the checkout.
                    if (syncAttempted)
                    {
                        string? preserved = await PreserveWorkingCheckoutOnlyCommitsAsync(vessel, mission, targetBranch, token).ConfigureAwait(false);
                        if (preserved != null) mission.FailureReason = mission.FailureReason + " " + preserved;
                    }

                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "failed to reconcile LocalMerge working directory " + vessel.WorkingDirectory + ": " + ex.Message);
                    return false;
                }

                _Logging.Info(_Header + "leaving user working directory " + vessel.WorkingDirectory + " unchanged after sync check failed: " + ex.Message + "; run git pull on " + targetBranch + " to sync when ready");
                return true;
            }
        }

        /// <summary>
        /// Keeps commits that exist only in the configured checkout reachable from the landing
        /// repository, and raises one event and one incident for them.
        /// </summary>
        /// <remarks>
        /// Those commits exist in one place on disk, so the checkout is never reset and nothing is
        /// pushed to a remote. The checkout HEAD is pushed, without force, to a recover branch in the
        /// landing repository, where an operator can land it through the merge queue and then
        /// fast-forward the checkout. A step that cannot run is named in the incident instead.
        /// </remarks>
        /// <returns>A sentence for the mission failure reason, or null when the checkout holds no commit the landing repository lacks.</returns>
        private async Task<string?> PreserveWorkingCheckoutOnlyCommitsAsync(Vessel vessel, Mission mission, string targetBranch, CancellationToken token)
        {
            if (String.IsNullOrEmpty(vessel.LocalPath) || String.IsNullOrEmpty(vessel.WorkingDirectory)) return null;

            string? landingTip = null;
            string? checkoutHead = null;
            int? onlyInCheckout = null;
            string? recoverBranch = null;
            string? problem = null;

            try
            {
                landingTip = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, "refs/heads/" + targetBranch, token).ConfigureAwait(false);
                checkoutHead = await _Git.GetRevisionCommitShaAsync(vessel.WorkingDirectory!, "HEAD", token).ConfigureAwait(false);

                if (String.IsNullOrWhiteSpace(landingTip) || String.IsNullOrWhiteSpace(checkoutHead))
                {
                    problem = "the landing tip or the checkout HEAD could not be resolved";
                }
                else if (String.Equals(landingTip, checkoutHead, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                else
                {
                    onlyInCheckout = await _Git.TryCountCommitsBetweenAsync(vessel.WorkingDirectory!, landingTip!, "HEAD", token).ConfigureAwait(false);
                    if (onlyInCheckout == null)
                    {
                        problem = "the commits held only by the checkout could not be counted";
                    }
                    else if (onlyInCheckout.Value == 0)
                    {
                        return null;
                    }
                    else
                    {
                        string candidate = _RecoverBranchPrefix + checkoutHead!.Substring(0, Math.Min(12, checkoutHead.Length));
                        await _Git.PushHeadToRepositoryBranchAsync(vessel.WorkingDirectory!, vessel.LocalPath!, candidate, token).ConfigureAwait(false);
                        recoverBranch = candidate;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                problem = (onlyInCheckout.HasValue && onlyInCheckout.Value > 0
                    ? "pushing the checkout HEAD to the landing repository failed: "
                    : "reading the checkout failed: ") + ex.Message;
            }

            string workingDirectory = vessel.WorkingDirectory!;
            string landingRepository = vessel.LocalPath!;
            string title;
            string rootCause;
            string recoveryNotes;
            string reasonSentence;

            if (recoverBranch != null)
            {
                title = "Working checkout holds unlanded commits: " + recoverBranch;
                rootCause = "The configured working checkout holds " + onlyInCheckout + " commit(s) that are not on "
                    + targetBranch + " in the landing repository, so the post-landing fast-forward cannot run.";
                recoveryNotes = "Recover branch: " + recoverBranch + " in the landing repository " + landingRepository + ".\n"
                    + "Checkout HEAD: " + checkoutHead + ".\n"
                    + "Landing tip: " + landingTip + ".\n"
                    + "Commits only in the checkout: " + onlyInCheckout + ".\n"
                    + "Do not reset the checkout. Steps:\n"
                    + "1. Review: git -C " + landingRepository + " log --oneline " + targetBranch + ".." + recoverBranch + "\n"
                    + "2. Land " + recoverBranch + " onto " + targetBranch + " through the merge queue.\n"
                    + "3. Fast-forward the checkout: git -C " + workingDirectory + " fetch " + landingRepository + " " + targetBranch
                    + " && git -C " + workingDirectory + " merge --ff-only FETCH_HEAD\n"
                    + "4. Confirm git -C " + workingDirectory + " rev-list --count FETCH_HEAD..HEAD and HEAD..FETCH_HEAD both print 0, "
                    + "then close this incident and delete " + recoverBranch + " from the landing repository.";
                reasonSentence = "Preserved " + onlyInCheckout + " checkout-only commit(s) as " + recoverBranch + " (" + checkoutHead
                    + ") in the landing repository; see the incident for the repair steps.";
            }
            else
            {
                title = "Working checkout divergence could not be preserved: " + workingDirectory;
                rootCause = "Working checkout divergence could not be preserved: " + problem + ".";
                recoveryNotes = "Nothing was pushed and the checkout was not changed. Do not reset the checkout.\n"
                    + "Checkout HEAD: " + (checkoutHead ?? "(unresolved)") + ".\n"
                    + "Landing tip: " + (landingTip ?? "(unresolved)") + ".\n"
                    + "Inspect by hand: git -C " + workingDirectory + " fetch " + landingRepository + " " + targetBranch
                    + ", then git -C " + workingDirectory + " rev-list --count FETCH_HEAD..HEAD. A non-zero count is work that exists only "
                    + "in the checkout: push it with git -C " + workingDirectory + " push " + landingRepository + " HEAD:refs/heads/"
                    + _RecoverBranchPrefix + "<sha> and land that branch through the merge queue.";
                reasonSentence = "Checkout-only commits were not preserved: " + problem + "; see the incident.";
            }

            string? raiseProblem = await RaiseWorkingCheckoutDivergenceAsync(
                vessel, mission, title, rootCause, recoveryNotes, recoverBranch, checkoutHead, onlyInCheckout, problem, token).ConfigureAwait(false);
            if (raiseProblem != null) reasonSentence = reasonSentence + " " + raiseProblem;

            _Logging.Warn(_Header + rootCause + " " + reasonSentence);
            return reasonSentence;
        }

        /// <summary>
        /// Make a direct landing that could not reach origin loud: one event and one open incident per
        /// vessel and target naming the branch, the git error, and the bare and origin heads.
        /// </summary>
        private async Task RaiseLandingPushExhaustedAsync(Vessel vessel, Mission mission, string targetBranch, string error, CancellationToken token)
        {
            string bareHead = "unknown";
            string originHead = "unknown";
            try
            {
                bareHead = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, "refs/heads/" + targetBranch, token).ConfigureAwait(false) ?? "unknown";
                originHead = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, "refs/remotes/origin/" + targetBranch, token).ConfigureAwait(false) ?? "unknown";
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _Logging.Warn(_Header + "could not read heads for the landing push failure of " + mission.Id + ": " + ex.Message);
            }

            string summary = "Mission " + mission.Id + " branch " + (mission.BranchName ?? "unknown") + " did not reach origin/" + targetBranch
                + " on vessel " + vessel.Name + " (" + vessel.Id + ") after " + mission.LandingRetryCount + " retries. Bare " + targetBranch + " is "
                + bareHead + ", origin/" + targetBranch + " is " + originHead + ". Git error: " + error;
            try
            {
                ArmadaEvent evt = new ArmadaEvent("landing.origin_push_failed", summary);
                evt.EntityType = "mission";
                evt.EntityId = mission.Id;
                evt.MissionId = mission.Id;
                evt.VesselId = vessel.Id;
                evt.VoyageId = mission.VoyageId;
                EventOwnerScope.ApplyFromMission(evt, mission);
                evt.Payload = JsonSerializer.Serialize(new { missionId = mission.Id, branch = mission.BranchName, targetBranch, bareTargetHead = bareHead, originTargetHead = originHead, retries = mission.LandingRetryCount, error });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _Logging.Warn(_Header + "could not record the landing push failure event for " + mission.Id + ": " + ex.Message);
            }

            AuthContext auth = AuthContext.Authenticated(
                mission.TenantId ?? Constants.DefaultTenantId,
                mission.UserId ?? Constants.DefaultUserId,
                isAdmin: false,
                isTenantAdmin: true,
                authMethod: "System",
                principalDisplay: "Landing");
            string title = "Landing push to origin failed: " + vessel.Name + " " + targetBranch;
            try
            {
                EnumerationResult<Incident> existing = await _Incidents.EnumerateAsync(auth,
                    new IncidentQuery { VesselId = vessel.Id, Search = title, ExcludeTerminal = true, PageSize = 1 }, token).ConfigureAwait(false);
                if (existing.TotalRecords > 0) return;
                await _Incidents.CreateAutomaticallyAsync(auth, new IncidentUpsertRequest
                {
                    Title = title,
                    Summary = summary,
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.High,
                    VesselId = vessel.Id,
                    MissionId = mission.Id,
                    VoyageId = mission.VoyageId,
                    Impact = "The mission's work is not on origin, and later landings on this target fail the same way until bare and origin agree.",
                    RecoveryNotes = "Compare the bare " + targetBranch + " with origin/" + targetBranch
                        + ". Fast-forward whichever is behind, or merge both in a scratch worktree and push the merge. Never reset the bare target to origin while it holds landed commits. Then retry the landing."
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _Logging.Warn(_Header + "could not open the landing push failure incident for " + mission.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Opens one incident and emits one event for a working checkout divergence, unless an open
        /// incident with the same title already exists for the vessel.
        /// </summary>
        /// <returns>Null on success or when already raised; otherwise a sentence naming what could not be recorded.</returns>
        private async Task<string?> RaiseWorkingCheckoutDivergenceAsync(
            Vessel vessel,
            Mission mission,
            string title,
            string rootCause,
            string recoveryNotes,
            string? recoverBranch,
            string? checkoutHead,
            int? onlyInCheckout,
            string? problem,
            CancellationToken token)
        {
            AuthContext auth = AuthContext.Authenticated(
                mission.TenantId ?? Constants.DefaultTenantId,
                mission.UserId ?? Constants.DefaultUserId,
                isAdmin: false,
                isTenantAdmin: true,
                authMethod: "System",
                principalDisplay: "Landing");

            try
            {
                IncidentQuery query = new IncidentQuery
                {
                    VesselId = vessel.Id,
                    Search = title,
                    ExcludeTerminal = true,
                    PageSize = 1
                };
                EnumerationResult<Incident> existing = await _Incidents.EnumerateAsync(auth, query, token).ConfigureAwait(false);
                if (existing.TotalRecords > 0) return null;

                IncidentUpsertRequest request = new IncidentUpsertRequest
                {
                    Title = title,
                    Summary = rootCause,
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.High,
                    VesselId = vessel.Id,
                    MissionId = mission.Id,
                    VoyageId = mission.VoyageId,
                    Impact = "Commits that exist only in the working checkout are at risk until they land, and later landings cannot fast-forward the checkout.",
                    RootCause = rootCause,
                    RecoveryNotes = recoveryNotes
                };
                await _Incidents.CreateAutomaticallyAsync(auth, request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not open the working checkout divergence incident for vessel " + vessel.Id + ": " + ex.Message);
                return "The incident could not be opened: " + ex.Message;
            }

            try
            {
                ArmadaEvent evt = new ArmadaEvent("landing.working_checkout_diverged", title);
                evt.EntityType = "vessel";
                evt.EntityId = vessel.Id;
                evt.MissionId = mission.Id;
                evt.VesselId = vessel.Id;
                evt.VoyageId = mission.VoyageId;
                EventOwnerScope.ApplyFromMission(evt, mission);
                evt.Payload = JsonSerializer.Serialize(new
                {
                    vesselId = vessel.Id,
                    missionId = mission.Id,
                    workingDirectory = vessel.WorkingDirectory,
                    landingRepository = vessel.LocalPath,
                    recoverBranch,
                    checkoutHead,
                    commitsOnlyInCheckout = onlyInCheckout,
                    problem
                });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record the working checkout divergence event for vessel " + vessel.Id + ": " + ex.Message);
                return "The divergence event could not be recorded: " + ex.Message;
            }

            return null;
        }

        /// <summary>
        /// Clears a stale worktree left at the deterministic per-mission integration path by an attempt that
        /// did not run its own cleanup, so the next integration <c>git worktree add</c> is not refused with
        /// an "already exists" path.
        /// </summary>
        /// <remarks>
        /// Only a worktree this landing owns is removed. The integration path is exclusive to one mission's
        /// landing, and that landing is serialized, so a worktree registered to this landing repository at the
        /// path is a finished or interrupted attempt of this same landing and is safe to rebuild. A path that
        /// is not a registered worktree of this landing repository is foreign: it is preserved and the caller
        /// surfaces it, so unexpected content at the path is never deleted blindly.
        /// </remarks>
        private async Task RecoverOwnedStaleIntegrationWorktreeAsync(string repoPath, string worktreePath, string missionId, CancellationToken token)
        {
            if (!Directory.Exists(worktreePath)) return;

            // Drop registrations whose directories are already gone. A prune never removes a worktree whose
            // directory is still present, so a leftover (or live) worktree at this path survives it.
            await _Git.PruneWorktreesAsync(repoPath, token).ConfigureAwait(false);
            if (!Directory.Exists(worktreePath)) return;

            bool owned = await _Git.IsWorktreeRegisteredAsync(repoPath, worktreePath, token).ConfigureAwait(false);
            if (!owned)
                throw new InvalidOperationException("integration worktree path " + worktreePath
                    + " already exists and is not a worktree of landing repository " + repoPath
                    + "; it is preserved, not removed. Inspect it for unique work and clear it by hand, then retry the landing.");

            _Logging.Warn(_Header + "recovering a stale integration worktree " + worktreePath
                + " left by an interrupted landing attempt for mission " + missionId);
            await _Git.RemoveWorktreeAsync(worktreePath, token).ConfigureAwait(false);
            await _Git.PruneWorktreesAsync(repoPath, token).ConfigureAwait(false);
        }

        private async Task PersistMissionRetryStateAsync(Mission mission, CancellationToken token)
        {
            try
            {
                using IDisposable admission = await DirectLandingAdmission.AcquireAsync(_Database, mission.TenantId, mission.VoyageId, mission.Id, token).ConfigureAwait(false);
                if (await DirectLandingAdmission.IsCancelledAsync(_Database, mission, token).ConfigureAwait(false)) return;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to persist landing retry state for mission " + mission.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Names the failure class of an integration step that git refused, carrying git's own
        /// message. Returns null for target-branch drift, which the retry loop handles itself.
        /// </summary>
        /// <remarks>
        /// A bare "merge failed" reads as a content conflict. When the cause is a worktree that
        /// holds a ref, or any other git refusal, the operator needs git's words -- they name the
        /// blocking path -- and a class that says which kind of problem it is.
        /// </remarks>
        internal static string? ClassifyIntegrationFailure(Exception ex)
        {
            if (ex == null || IsTargetBranchDrift(ex)) return null;
            string message = (ex.Message ?? String.Empty).Trim();
            if (message.Contains("is already used by worktree", StringComparison.OrdinalIgnoreCase)
                || message.Contains("is already checked out at", StringComparison.OrdinalIgnoreCase))
            {
                return "worktree_conflict: another worktree of the landing repository holds a ref this landing needs. "
                    + "Git reported: " + message
                    + " -- inspect that worktree for unique work before removing it, then retry the landing.";
            }

            return "integration_merge_failed: " + message;
        }

        private static bool IsTargetBranchDrift(Exception ex)
        {
            string message = ex.Message ?? String.Empty;
            return message.Contains("target branch drift", StringComparison.OrdinalIgnoreCase)
                || message.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                || message.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
                || message.Contains("stale info", StringComparison.OrdinalIgnoreCase)
                || message.Contains("failed to push some refs", StringComparison.OrdinalIgnoreCase)
                || message.Contains("remote contains work", StringComparison.OrdinalIgnoreCase)
                || message.Contains("tip of your current branch is behind", StringComparison.OrdinalIgnoreCase)
                || message.Contains("but expected", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
