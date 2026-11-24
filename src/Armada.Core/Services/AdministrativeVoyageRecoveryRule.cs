namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    /// <summary>Fail-closed stored-graph eligibility for correcting a planning-only completion.</summary>
    public static class AdministrativeVoyageRecoveryRule
    {
        /// <summary>Return every required graph or source-evidence gap. This rule never changes state.</summary>
        public static List<string> FindBlockers(Voyage voyage, IReadOnlyList<Mission> missions,
            string tenantId, string userId, string sourceCommit, HistoricalWorkDisposition? historicalWork = null, bool historicalEvidence = false)
        {
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));
            if (missions == null) throw new ArgumentNullException(nameof(missions));
            if (sourceCommit == null) throw new ArgumentNullException(nameof(sourceCommit));
            if (!OwnershipPolicy.SameTenant(voyage.TenantId, tenantId)
                || OwnershipPolicy.UserOfRecord(voyage.UserId) != userId)
                throw new UnauthorizedAccessException("The voyage belongs to another owner.");
            List<string> blockers = new List<string>();
            if (voyage.Status != VoyageStatusEnum.Complete || !voyage.CompletedUtc.HasValue)
                blockers.Add("voyage_not_finalized_complete");
            if (sourceCommit.Length != 40 || sourceCommit.Any(c => !Uri.IsHexDigit(c)))
                blockers.Add("exact_source_commit_required");
            List<Mission> stages = missions.OrderBy(m => m.StageOrder).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
            if (stages.Count < 2) { blockers.Add("required_stage_graph_missing"); return blockers; }
            Mission planner = stages[0];
            if (!PersonaCatalog.Matches(planner.Persona, "Architect") || planner.Status != MissionStatusEnum.Complete
                || planner.Mode != MissionModeEnum.Implementation || !String.IsNullOrEmpty(planner.DependsOnMissionId)
                || !String.Equals(planner.StartFromRef, sourceCommit, StringComparison.OrdinalIgnoreCase)
                || !String.Equals(planner.CommitHash, sourceCommit, StringComparison.OrdinalIgnoreCase))
                blockers.Add("planning_only_anchor_unproved");
            string? chain = planner.StageChainId;
            foreach (Mission stage in stages)
            {
                if (!OwnershipPolicy.SameTenant(stage.TenantId, tenantId)
                    || OwnershipPolicy.UserOfRecord(stage.UserId) != userId)
                    throw new UnauthorizedAccessException("A stage belongs to another owner.");
                int index = stages.IndexOf(stage);
                if (stage.VoyageId != voyage.Id || String.IsNullOrEmpty(stage.VesselId) || stage.VesselId != planner.VesselId
                    || String.IsNullOrEmpty(chain) || stage.StageChainId != chain || !stage.StageOrder.HasValue
                    || (index > 0 && (stage.StageOrder <= stages[index - 1].StageOrder || stage.DependsOnMissionId != stages[index - 1].Id)))
                    blockers.Add("exact_linear_stage_graph_required:" + stage.Id);
                if (stage.ProcessId.HasValue || (stage.ProcessStartedUtc.HasValue && !historicalEvidence) || stage.HeldForOperatorReview
                    || !String.IsNullOrEmpty(stage.HarborJobId) || !String.IsNullOrEmpty(stage.DockProvisionAttemptId)
                    || stage.IntegrationBarrier != null || stage.IntegrationBarrierInvalid || stage.IntegrationBarrierRawJsonPresent)
                    blockers.Add("stage_runtime_or_integration_bound:" + stage.Id);
                if (!String.IsNullOrEmpty(stage.PrUrl)) blockers.Add("pull_request_acceptance_conflict:" + stage.Id);
                if ((!String.IsNullOrEmpty(stage.DiffSnapshot) || !String.IsNullOrEmpty(stage.PreservedRunOutput)) && !historicalEvidence)
                    blockers.Add("accepted_or_prior_work_evidence_required:" + stage.Id);
                if (historicalEvidence && index > 0 && !String.IsNullOrEmpty(stage.CommitHash)
                    && !String.Equals(stage.CommitHash, historicalWork!.PreservedSourceCommit, StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(stage.CommitHash, sourceCommit, StringComparison.OrdinalIgnoreCase))
                    blockers.Add("conflicting_historical_commit:" + stage.Id);
                if (index == 0) continue;
                bool cancelledByOperator = OperatorStop.IsOperatorStopped(stage.FailureReason)
                    || String.Equals(stage.FailureReason, "Mission cancelled by operator.", StringComparison.Ordinal)
                    || (stage.FailureReason ?? String.Empty).StartsWith(
                        MissionDependentCancellation.BlockedByFailedDependencyPrefix, StringComparison.Ordinal)
                    || (stage.FailureReason ?? String.Empty).StartsWith("Blocked by cancelled dependency ", StringComparison.Ordinal);
                if (stage.Status != MissionStatusEnum.Cancelled || !cancelledByOperator)
                    blockers.Add("operator_cancelled_required_stage_required:" + stage.Id);
                // Absence of a commit after execution is not evidence that no work was produced or accepted.
                // Recovery of such a history needs separate retained source proof, not an override boolean.
                if (stage.CompletedUtc == null || (!historicalEvidence && (stage.StartedUtc.HasValue || !String.IsNullOrEmpty(stage.CommitHash)
                    || !String.IsNullOrEmpty(stage.BranchName) || !String.IsNullOrEmpty(stage.AgentOutput)
                    || stage.ReviewedUtc.HasValue)))
                    blockers.Add("cancelled_stage_execution_evidence_required:" + stage.Id);
            }
            return blockers;
        }
    }
}
