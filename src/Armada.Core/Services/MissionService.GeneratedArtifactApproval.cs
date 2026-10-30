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
        /// <summary>Resume an already produced native terminal stage after its generated candidate is approved.</summary>
        public Func<string, string?, CancellationToken, Task<bool>>? GeneratedArtifactLandingRetry { get; set; }

        private async Task ResumeProducedGeneratedLandingAsync(Mission judge, CancellationToken token)
        {
            if (judge.GeneratedArtifactReview?.ApprovedUtc == null || judge.HeldForOperatorReview
                || String.IsNullOrWhiteSpace(judge.VoyageId) || GeneratedArtifactLandingRetry == null) return;
            Vessel? vessel = String.IsNullOrWhiteSpace(judge.VesselId) ? null
                : await _Database.Vessels.ReadAsync(judge.VesselId, token).ConfigureAwait(false);
            Voyage? voyage = await _Database.Voyages.ReadAsync(judge.VoyageId, token).ConfigureAwait(false);
            if (vessel == null || LandingConfigurationResolver.Resolve(_Settings, vessel, voyage).LandingMode != LandingModeEnum.LocalMerge) return;
            List<Mission> stages = await _Database.Missions.EnumerateByVoyageAsync(judge.VoyageId, token).ConfigureAwait(false);
            foreach (Mission leaf in stages)
            {
                if (leaf.Id == judge.Id || leaf.BranchName != judge.BranchName || leaf.HeldForOperatorReview
                    || leaf.ProcessId.HasValue || (leaf.Status != MissionStatusEnum.WorkProduced && leaf.Status != MissionStatusEnum.LandingFailed)
                    || DefinitionOfDoneGateRuns.IsRunning(leaf.Id) || _GateRerunsInFlight.ContainsKey(leaf.Id)
                    || IsOtherCompletionInFlight(leaf.Id) || stages.Any(stage => stage.DependsOnMissionId == leaf.Id)) continue;
                Mission? owner = await GeneratedArtifactReviewResolver.ResolveJudgeAsync(_Database, leaf, token).ConfigureAwait(false);
                if (owner?.Id != judge.Id || owner.GeneratedArtifactReview?.ApprovedUtc == null) continue;
                bool landed = await GeneratedArtifactLandingRetry(leaf.Id, leaf.TenantId, token).ConfigureAwait(false);
                await AppendMissionActivityAsync(judge.Id, "Approved generated candidate resumed terminal stage " + leaf.Id
                    + (landed ? ": landed" : ": landing remains unresolved; inspect the retained landing reason"), token).ConfigureAwait(false);
            }
        }

        private async Task<bool> ApproveGeneratedCandidateAsync(Mission reviewed, bool validatedPass, CancellationToken token)
        {
            if (reviewed.GeneratedArtifactReview == null) return true;
            Mission? current = await _Database.Missions.ReadAsync(reviewed.Id, token).ConfigureAwait(false);
            GeneratedArtifactReviewBinding? binding = current?.GeneratedArtifactReview;
            if (current == null || binding == null || !validatedPass || current.HeldForOperatorReview
                || current.Status != MissionStatusEnum.WorkProduced || ParseJudgeVerdict(current.AgentOutput) != JudgeVerdict.Pass)
                return false;
            if (binding.ApprovedUtc != null)
            {
                reviewed.GeneratedArtifactReview = binding;
                return true;
            }
            if (!String.Equals(current.AgentOutput, reviewed.AgentOutput, StringComparison.Ordinal))
                return await HoldGeneratedCandidateApprovalAsync(reviewed, "generated_candidate_review_changed", token).ConfigureAwait(false);
            if (current.CommitHash != binding.CandidateCommit || _Git == null || String.IsNullOrWhiteSpace(current.VesselId))
                return await HoldGeneratedCandidateApprovalAsync(reviewed, "generated_candidate_source_changed", token).ConfigureAwait(false);
            Vessel? vessel = await _Database.Vessels.ReadAsync(current.VesselId, token).ConfigureAwait(false);
            if (vessel == null || String.IsNullOrWhiteSpace(vessel.LocalPath) || String.IsNullOrWhiteSpace(current.BranchName)
                || !String.Equals(await _Git.GetRevisionCommitShaAsync(vessel.LocalPath, "refs/heads/" + current.BranchName, token).ConfigureAwait(false),
                    binding.CandidateCommit, StringComparison.Ordinal))
                return await HoldGeneratedCandidateApprovalAsync(reviewed, "generated_candidate_source_changed", token).ConfigureAwait(false);

            List<CheckRunQuery> queries = new List<CheckRunQuery> { new CheckRunQuery { MissionId = current.Id } };
            List<Mission>? stages = null;
            if (!String.IsNullOrWhiteSpace(current.VoyageId))
            {
                queries.Add(new CheckRunQuery { VoyageId = current.VoyageId });
                stages = await _Database.Missions.EnumerateByVoyageAsync(current.VoyageId, token).ConfigureAwait(false);
            }
            Dictionary<string, CheckRun> all = await CheckRunEnumeration.ReadAllAsync(_Database, current.TenantId, queries, token).ConfigureAwait(false);
            List<CheckRun> checks = VoyageLanes.ChecksForLaneOf(current, stages, all.Values);
            if (ClassifyJudgeCheckGate(checks, current.AgentOutput, binding.CandidateCommit) != JudgeCheckGate.GreenChecks)
                return await HoldGeneratedCandidateApprovalAsync(reviewed, "generated_candidate_required_checks_pending", token).ConfigureAwait(false);
            WorkflowProfileService profiles = new WorkflowProfileService(_Database, _Logging);
            AuthContext auth = AuthContext.Authenticated(vessel.TenantId ?? Constants.DefaultTenantId,
                vessel.UserId ?? Constants.DefaultUserId, true, true, "System", principalDisplay: "Generated candidate verification");
            WorkflowProfile? profile = await profiles.ResolveForVesselAsync(auth, vessel, null, token).ConfigureAwait(false);
            if (profile == null || (String.IsNullOrWhiteSpace(profile.BuildCommand) && String.IsNullOrWhiteSpace(profile.UnitTestCommand)))
                return await HoldGeneratedCandidateApprovalAsync(reviewed, "generated_candidate_required_checks_missing", token).ConfigureAwait(false);
            foreach (CheckRunTypeEnum type in new[] { CheckRunTypeEnum.Build, CheckRunTypeEnum.UnitTest })
            {
                string? command = type == CheckRunTypeEnum.Build ? profile.BuildCommand : profile.UnitTestCommand;
                if (String.IsNullOrWhiteSpace(command)) continue;
                if (!checks.Any(check => check.Type == type && check.Status == CheckRunStatusEnum.Passed && check.ExitCode == 0
                    && check.CommitHash == binding.CandidateCommit && check.WorkflowProfileId == profile.Id
                    && String.Equals(check.Command?.Trim(), command.Trim(), StringComparison.Ordinal)))
                    return await HoldGeneratedCandidateApprovalAsync(reviewed, "generated_candidate_required_checks_missing", token).ConfigureAwait(false);
            }
            GeneratedArtifactReviewBinding approved = new GeneratedArtifactReviewBinding
            {
                SourceCommit = binding.SourceCommit, TargetBaseCommit = binding.TargetBaseCommit,
                CandidateCommit = binding.CandidateCommit, PreparationAttempt = binding.PreparationAttempt,
                RequestedUtc = binding.RequestedUtc, ApprovedUtc = DateTime.UtcNow
            };
            if (!await _Database.Missions.TryWriteGeneratedArtifactReviewAsync(current, approved, false, token).ConfigureAwait(false))
                return false;
            reviewed.GeneratedArtifactReview = approved;
            ArmadaEvent approvedEvent = new ArmadaEvent("mission.generated_candidate_review_approved",
                "Judge PASS and required Checks verified at " + binding.CandidateCommit)
            {
                TenantId = current.TenantId, MissionId = current.Id, EntityId = current.Id,
                VesselId = current.VesselId, VoyageId = current.VoyageId,
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                { binding.CandidateCommit, CheckIds = checks.Where(check => check.Status == CheckRunStatusEnum.Passed).Select(check => check.Id).ToArray() })
            };
            EventOwnerScope.ApplyFromMission(approvedEvent, current);
            await _Database.Events.CreateAsync(approvedEvent, token).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> HoldGeneratedCandidateApprovalAsync(Mission mission, string reason, CancellationToken token)
        {
            Mission? current = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            if (current == null) return false;
            if (current.HeldForOperatorReview)
            {
                mission.HeldForOperatorReview = true;
                mission.HeldForOperatorReviewReason = current.HeldForOperatorReviewReason;
                return false;
            }
            current.HeldForOperatorReview = true;
            current.HeldForOperatorReviewReason = reason == "generated_candidate_required_checks_pending"
                ? JudgeCheckWaitHold.BuildReason(_UtcNow(), current.CommitHash, reason, null) : reason;
            await _Database.Missions.UpdateAsync(current, token).ConfigureAwait(false);
            mission.HeldForOperatorReview = true;
            mission.HeldForOperatorReviewReason = current.HeldForOperatorReviewReason;
            await AppendMissionActivityAsync(mission.Id, "Candidate approval refused: " + reason, token).ConfigureAwait(false);
            return false;
        }
    }
}
