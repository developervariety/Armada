namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        /// <summary>Event written when an operator grants a terminal landing continuation.</summary>
        public const string TerminalContinuationGrantedEventType = "mission.terminal_continuation_granted";

        /// <summary>Event written when a Judge admitted by a terminal landing continuation lands.</summary>
        public const string TerminalContinuationLandedEventType = "mission.terminal_continuation_landed";

        /// <summary>
        /// Let one passed Judge whose landing failed inside a Failed voyage rerun at a regenerated candidate. Every
        /// predecessor must be delivered or Failed only because terminal-voyage reconciliation found its work unlanded,
        /// with its commit contained in the Judge's commit. The grant rewrites no status, counter or output; the
        /// existing preparation reopens the Judge, and the existing approval still needs a new Judge PASS and the
        /// required Checks at the exact candidate. Any other state refuses with a named reason and changes nothing.
        /// </summary>
        public async Task<TerminalLandingContinuationResult> ContinueTerminalLandingAsync(string missionId, string grantedBy,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentNullException(nameof(missionId));
            if (String.IsNullOrWhiteSpace(grantedBy)) throw new ArgumentNullException(nameof(grantedBy));
            Mission? judge = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            if (judge == null) return TerminalLandingContinuationResult.Refused("terminal_continuation_mission_not_found");
            if (!PersonaCatalog.Matches(judge.Persona, PersonaCatalog.Judge))
                return TerminalLandingContinuationResult.Refused("terminal_continuation_requires_judge");
            if (judge.GeneratedArtifactReview?.TerminalContinuation != null)
                return TerminalLandingContinuationResult.Refused("terminal_continuation_already_granted");
            if (judge.Status != MissionStatusEnum.LandingFailed)
                return TerminalLandingContinuationResult.Refused("terminal_continuation_not_landing_failed");
            if (judge.HeldForOperatorReview) return TerminalLandingContinuationResult.Refused("terminal_continuation_operator_hold");
            if (judge.ProcessId.HasValue) return TerminalLandingContinuationResult.Refused("terminal_continuation_process_running");
            if (ParseJudgeVerdict(judge.AgentOutput) != JudgeVerdict.Pass)
                return TerminalLandingContinuationResult.Refused("terminal_continuation_no_pass");
            if (String.IsNullOrWhiteSpace(judge.CommitHash) || String.IsNullOrWhiteSpace(judge.BranchName))
                return TerminalLandingContinuationResult.Refused("terminal_continuation_source_changed");

            Voyage? voyage = String.IsNullOrWhiteSpace(judge.VoyageId) ? null
                : await _Database.Voyages.ReadAsync(judge.VoyageId, token).ConfigureAwait(false);
            if (voyage == null) return TerminalLandingContinuationResult.Refused("terminal_continuation_no_voyage");
            if (voyage.Status == VoyageStatusEnum.Cancelled) return TerminalLandingContinuationResult.Refused("terminal_continuation_voyage_cancelled");
            if (voyage.Status != VoyageStatusEnum.Failed) return TerminalLandingContinuationResult.Refused("terminal_continuation_voyage_not_failed");

            if (await _Database.DispatchHolds.ReadAsync(token).ConfigureAwait(false) != null
                || (await _Database.DispatchHolds.ReadVesselsAsync(token).ConfigureAwait(false))
                    .Any(hold => String.Equals(hold.VesselId, judge.VesselId, StringComparison.Ordinal)))
                return TerminalLandingContinuationResult.Refused("terminal_continuation_dispatch_hold");

            Vessel? vessel = String.IsNullOrWhiteSpace(judge.VesselId) ? null
                : await _Database.Vessels.ReadAsync(judge.VesselId, token).ConfigureAwait(false);
            if (vessel == null || _Git == null || String.IsNullOrWhiteSpace(vessel.LocalPath))
                return TerminalLandingContinuationResult.Refused("terminal_continuation_repository_unavailable");
            if (vessel.GeneratedArtifacts == null)
                return TerminalLandingContinuationResult.Refused("terminal_continuation_no_generated_declaration");
            Dock? dock = String.IsNullOrWhiteSpace(judge.DockId) ? null
                : await _Database.Docks.ReadAsync(judge.DockId, token).ConfigureAwait(false);
            if (dock?.HostCheckout != null || dock?.HostCheckoutLifecycle != null)
                return TerminalLandingContinuationResult.Refused("terminal_continuation_harbor_unsupported");
            string? tip = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath, "refs/heads/" + judge.BranchName, token).ConfigureAwait(false);
            if (!String.Equals(tip, judge.CommitHash, StringComparison.Ordinal))
                return TerminalLandingContinuationResult.Refused("terminal_continuation_source_changed");

            List<TerminalLandingContinuationPredecessor> named = new List<TerminalLandingContinuationPredecessor>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal) { judge.Id };
            string? nextId = judge.DependsOnMissionId;
            while (!String.IsNullOrWhiteSpace(nextId) && seen.Add(nextId))
            {
                Mission? stage = await _Database.Missions.ReadAsync(nextId, token).ConfigureAwait(false);
                if (stage == null || !String.Equals(stage.VoyageId, judge.VoyageId, StringComparison.Ordinal))
                    return TerminalLandingContinuationResult.Refused("terminal_continuation_upstream_failure:" + nextId);
                if (!IsDependencySatisfyingStatus(stage.Status))
                {
                    if (stage.Status != MissionStatusEnum.Failed
                        || !String.Equals(stage.ReconciledReason, TerminalVoyageMissionRule.ReasonWorkUnlanded, StringComparison.Ordinal)
                        || String.IsNullOrWhiteSpace(stage.CommitHash))
                        return TerminalLandingContinuationResult.Refused("terminal_continuation_upstream_failure:" + stage.Id);
                    bool? contained = await _Git.TryIsAncestorAsync(vessel.LocalPath, stage.CommitHash, judge.CommitHash, token).ConfigureAwait(false);
                    if (contained == null) return TerminalLandingContinuationResult.Refused("terminal_continuation_repository_unavailable");
                    if (contained == false) return TerminalLandingContinuationResult.Refused("terminal_continuation_predecessor_not_contained:" + stage.Id);
                    named.Add(new TerminalLandingContinuationPredecessor
                    {
                        MissionId = stage.Id, Status = stage.Status.ToString(), CommitHash = stage.CommitHash
                    });
                }
                nextId = stage.DependsOnMissionId;
            }

            TerminalLandingContinuation grant = new TerminalLandingContinuation
            {
                GrantedBy = grantedBy.Trim(), GrantedUtc = DateTime.UtcNow, VoyageId = voyage.Id, Predecessors = named
            };
            GeneratedArtifactLandingResult prepared;
            try { prepared = await PrepareGeneratedArtifactsCoreAsync(judge, null, grant, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "terminal landing continuation preparation failed for " + judge.Id + ": " + ex.Message);
                return TerminalLandingContinuationResult.Refused("terminal_continuation_not_prepared: generated_preparation_failed");
            }
            Mission? after = await _Database.Missions.ReadAsync(judge.Id, token).ConfigureAwait(false);
            GeneratedArtifactReviewBinding? binding = after?.GeneratedArtifactReview;
            if (prepared.Outcome != "ReviewRequired" || binding?.TerminalContinuation == null
                || binding.TerminalContinuation.GrantedUtc != grant.GrantedUtc)
                return TerminalLandingContinuationResult.Refused("terminal_continuation_not_prepared: "
                    + (prepared.Reason ?? prepared.Outcome ?? "unknown"));

            ArmadaEvent granted = new ArmadaEvent(TerminalContinuationGrantedEventType,
                "Operator " + grant.GrantedBy + " granted a terminal landing continuation for Judge " + judge.Id + " at " + binding.CandidateCommit)
            {
                TenantId = judge.TenantId, MissionId = judge.Id, EntityId = judge.Id, VesselId = judge.VesselId, VoyageId = judge.VoyageId,
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    grant.GrantedBy, grant.VoyageId, binding.SourceCommit, binding.CandidateCommit,
                    Predecessors = named.Select(p => new { p.MissionId, p.Status, p.CommitHash }).ToArray()
                })
            };
            EventOwnerScope.ApplyFromMission(granted, judge);
            await _Database.Events.CreateAsync(granted, token).ConfigureAwait(false);
            await AppendMissionActivityAsync(judge.Id, "Terminal landing continuation granted by " + grant.GrantedBy
                + "; the Judge reruns at " + binding.CandidateCommit, token).ConfigureAwait(false);
            return new TerminalLandingContinuationResult { Granted = true, Binding = binding };
        }

        /// <summary>
        /// Decide whether a granted continuation admits this Pending Judge past the terminal-voyage cancellation.
        /// Returns null when no grant applies, so the ordinary rule runs. Otherwise returns true when admitted (the
        /// admission is recorded once per prepared candidate) or false when the mission was held with a named reason.
        /// </summary>
        private async Task<bool?> TryAdmitTerminalContinuationAsync(Mission mission, Voyage voyage, CancellationToken token)
        {
            GeneratedArtifactReviewBinding? binding = mission.GeneratedArtifactReview;
            TerminalLandingContinuation? grant = binding?.TerminalContinuation;
            if (binding == null || grant == null || voyage.Status != VoyageStatusEnum.Failed
                || !String.Equals(grant.VoyageId, voyage.Id, StringComparison.Ordinal)
                || !PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge)
                || binding.ApprovedUtc != null || !String.Equals(mission.CommitHash, binding.CandidateCommit, StringComparison.Ordinal))
                return null;

            if (grant.AdmittedAttempt >= binding.PreparationAttempt && mission.StartedUtc != null)
            {
                await HoldForAssignmentAsync(mission, "terminal_continuation_replayed", mission.Id,
                    "The continuation already admitted candidate " + binding.CandidateCommit + " once.", token).ConfigureAwait(false);
                return false;
            }
            string? changed = await FindChangedContinuationPredecessorAsync(grant, token).ConfigureAwait(false);
            if (changed != null)
            {
                await HoldForAssignmentAsync(mission, "terminal_continuation_predecessor_changed", changed,
                    "Predecessor " + changed + " changed status or commit after the continuation was granted.", token).ConfigureAwait(false);
                return false;
            }
            if (grant.AdmittedAttempt >= binding.PreparationAttempt) return true;

            GeneratedArtifactReviewBinding next = System.Text.Json.JsonSerializer.Deserialize<GeneratedArtifactReviewBinding>(
                System.Text.Json.JsonSerializer.Serialize(binding))!;
            next.TerminalContinuation!.AdmittedAttempt = binding.PreparationAttempt;
            next.TerminalContinuation.AdmittedUtc = DateTime.UtcNow;
            if (!await _Database.Missions.TryRecordTerminalContinuationAdmissionAsync(mission, next, token).ConfigureAwait(false))
            {
                await HoldForAssignmentAsync(mission, "terminal_continuation_replayed", mission.Id,
                    "The continuation admission for candidate " + binding.CandidateCommit + " could not be recorded.", token).ConfigureAwait(false);
                return false;
            }
            mission.GeneratedArtifactReview = next;
            await AppendMissionActivityAsync(mission.Id, "Terminal landing continuation admitted candidate " + binding.CandidateCommit
                + " (preparation attempt " + binding.PreparationAttempt + ")", token).ConfigureAwait(false);
            return true;
        }

        /// <summary>True when an admitted continuation names this Failed predecessor with its granted status and commit.</summary>
        private static bool ContinuationCoversDependency(Mission mission, Mission dependency)
        {
            GeneratedArtifactReviewBinding? binding = mission.GeneratedArtifactReview;
            TerminalLandingContinuation? grant = binding?.TerminalContinuation;
            return binding != null && grant != null && binding.ApprovedUtc == null
                && grant.AdmittedAttempt == binding.PreparationAttempt
                && dependency.Status == MissionStatusEnum.Failed
                && grant.Predecessors.Any(p => String.Equals(p.MissionId, dependency.Id, StringComparison.Ordinal)
                    && String.Equals(p.Status, dependency.Status.ToString(), StringComparison.Ordinal)
                    && String.Equals(p.CommitHash, dependency.CommitHash, StringComparison.Ordinal));
        }

        private async Task<string?> FindChangedContinuationPredecessorAsync(TerminalLandingContinuation grant, CancellationToken token)
        {
            foreach (TerminalLandingContinuationPredecessor named in grant.Predecessors)
            {
                Mission? current = await _Database.Missions.ReadAsync(named.MissionId, token).ConfigureAwait(false);
                if (current == null || !String.Equals(current.Status.ToString(), named.Status, StringComparison.Ordinal)
                    || !String.Equals(current.CommitHash, named.CommitHash, StringComparison.Ordinal))
                    return named.MissionId;
            }
            return null;
        }
    }
}
