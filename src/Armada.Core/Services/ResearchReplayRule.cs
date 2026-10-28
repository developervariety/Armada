namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Strict inventory and ownership rule for explicit full Research chain replay.</summary>
    public static class ResearchReplayRule
    {
        /// <summary>
        /// Validate one failed voyage and its complete six-stage linear Research inventory.
        /// This does not make any completed mission eligible for ordinary restart.
        /// </summary>
        /// <param name="voyage">Current owned voyage.</param>
        /// <param name="missions">Every mission in the voyage, without historical filtering.</param>
        /// <param name="tenantId">Authenticated operator tenant.</param>
        /// <param name="userId">Authenticated operator user.</param>
        /// <param name="sourceCommit">Exact source commit requested by the operator.</param>
        /// <returns>The six original missions in their existing stage order.</returns>
        public static List<Mission> Validate(Voyage voyage, IReadOnlyList<Mission> missions,
            string tenantId, string userId, string sourceCommit)
        {
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));
            if (missions == null) throw new ArgumentNullException(nameof(missions));
            if (!OwnershipPolicy.SameTenant(voyage.TenantId, tenantId)
                || !String.Equals(OwnershipPolicy.UserOfRecord(voyage.UserId), userId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The Research voyage belongs to another owner.");
            if (voyage.Status != VoyageStatusEnum.Failed || voyage.CompletedUtc == null)
                throw new InvalidOperationException("Only a finalized Failed Research voyage can be replayed.");
            if (sourceCommit == null || (sourceCommit.Length != 40 && sourceCommit.Length != 64)
                || sourceCommit.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
                throw new ArgumentException("An exact lowercase Git object ID is required.", nameof(sourceCommit));
            if (missions.Count != 6) throw new InvalidOperationException("Replay requires the exact six original Research duties.");
            List<Mission> ordered = missions.OrderBy(m => m.StageOrder).ToList();
            string[] roles = { PersonaCatalog.Worker, "PortingReferenceAnalyst", PersonaCatalog.TestEngineer,
                PersonaCatalog.Linter, PersonaCatalog.Worker, PersonaCatalog.Judge };
            for (int index = 0; index < ordered.Count; index++)
            {
                Mission mission = ordered[index];
                if (!OwnershipPolicy.SameTenant(mission.TenantId, tenantId)
                    || !String.Equals(OwnershipPolicy.UserOfRecord(mission.UserId), userId, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("A Research stage belongs to another owner.");
                if (mission.VoyageId != voyage.Id || mission.VesselId != ordered[0].VesselId
                    || String.IsNullOrWhiteSpace(mission.VesselId) || mission.Mode != MissionModeEnum.Research
                    || PersonaCatalog.NormalizeName(mission.Persona) != roles[index]
                    || !mission.StageOrder.HasValue
                    || (index > 0 && mission.StageOrder <= ordered[index - 1].StageOrder)
                    || (index == 0 ? !String.IsNullOrEmpty(mission.DependsOnMissionId) : mission.DependsOnMissionId != ordered[index - 1].Id))
                    throw new InvalidOperationException("The original six Research roles, source vessel and dependency order must be intact.");
                if ((mission.Status != MissionStatusEnum.Complete && mission.Status != MissionStatusEnum.Failed
                    && mission.Status != MissionStatusEnum.Cancelled) || mission.CompletedUtc == null
                    || mission.IntegrationBarrier != null || mission.IntegrationBarrierRawJsonPresent || mission.IntegrationBarrierInvalid
                    || mission.ProcessId.HasValue || mission.ProcessStartedUtc.HasValue
                    || mission.HeldForOperatorReview || !String.IsNullOrEmpty(mission.HarborJobId)
                    || !String.IsNullOrEmpty(mission.DockProvisionAttemptId))
                    throw new InvalidOperationException("Every Research stage must be terminal, unheld and native before replay.");
                if (!String.Equals(mission.CommitHash, sourceCommit, StringComparison.Ordinal)
                    || (!String.IsNullOrEmpty(mission.StartFromRef) && mission.StartFromRef != sourceCommit))
                    throw new InvalidOperationException("Every prior Research stage must retain the exact requested source commit.");
                if (mission.AgentOutput == null || mission.AgentOutput.StartsWith(MissionOutputArtifact.StreamTruncationMarker, StringComparison.Ordinal))
                    throw new InvalidOperationException("Every prior attempt must have a complete retained output capture.");
            }
            if (ordered[5].Status != MissionStatusEnum.Failed)
                throw new InvalidOperationException("The original Judge must retain a failed verdict before full-chain replay.");
            return ordered;
        }
    }
}
