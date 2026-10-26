namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;

    /// <summary>Creates a safe pending barrier copy for an integration mission rescue.</summary>
    public static class MissionIntegrationBarrierRescue
    {
        /// <summary>Install an empty preparing barrier on a rescue before its mission row is created.</summary>
        /// <param name="rescue">The new rescue mission.</param>
        /// <param name="failedIntegration">The failed mission that owns the accepted barrier.</param>
        /// <returns>True when a barrier was installed; false when the source mission has no barrier.</returns>
        public static bool Prepare(Mission rescue, Mission failedIntegration)
        {
            if (rescue == null) throw new ArgumentNullException(nameof(rescue));
            if (failedIntegration == null) throw new ArgumentNullException(nameof(failedIntegration));
            if (failedIntegration.IntegrationBarrierInvalid)
                throw new InvalidOperationException("The failed integration mission has an invalid barrier.");
            MissionIntegrationBarrier? source = failedIntegration.IntegrationBarrier;
            if (source == null) return false;
            if (source.Phase != MissionIntegrationBarrierPhaseEnum.Accepted)
                throw new InvalidOperationException("Only an accepted integration barrier can be rescued.");

            rescue.IntegrationBarrier = Bind(source, rescue, MissionIntegrationBarrierPhaseEnum.Preparing, false);
            return true;
        }

        /// <summary>Build the accepted barrier for a stored rescue mission from its source barrier.</summary>
        /// <param name="failedIntegration">The failed mission that owns the accepted barrier.</param>
        /// <param name="rescue">The stored rescue mission.</param>
        /// <returns>The accepted barrier rebound to the rescue identity.</returns>
        public static MissionIntegrationBarrier Accept(Mission failedIntegration, Mission rescue)
        {
            if (failedIntegration == null) throw new ArgumentNullException(nameof(failedIntegration));
            if (rescue == null) throw new ArgumentNullException(nameof(rescue));
            MissionIntegrationBarrier source = failedIntegration.IntegrationBarrier
                ?? throw new InvalidOperationException("The failed integration mission has no accepted barrier.");
            if (source.Phase != MissionIntegrationBarrierPhaseEnum.Accepted)
                throw new InvalidOperationException("Only an accepted integration barrier can be copied.");
            MissionIntegrationBarrier copy = Bind(source, rescue, MissionIntegrationBarrierPhaseEnum.Accepted, true);
            return copy;
        }

        private static MissionIntegrationBarrier Bind(
            MissionIntegrationBarrier source, Mission target, MissionIntegrationBarrierPhaseEnum phase, bool includeMembers)
        {
            return new MissionIntegrationBarrier
            {
                Version = source.Version,
                Phase = phase,
                ArchitectMissionId = source.ArchitectMissionId,
                PlanHash = source.PlanHash,
                IntegrationMissionId = target.Id,
                TenantId = target.TenantId,
                UserId = target.UserId,
                VesselId = target.VesselId,
                VoyageId = target.VoyageId,
                OriginVoyageId = source.OriginVoyageId,
                ExpectedBranchCount = source.ExpectedBranchCount,
                Members = includeMembers
                    ? source.Members.Select(member => new MissionIntegrationBarrierMember
                    {
                        BranchKey = member.BranchKey,
                        RootMissionId = member.RootMissionId,
                        AcceptedTerminalMissionId = member.AcceptedTerminalMissionId,
                        CurrentTerminalMissionId = member.CurrentTerminalMissionId
                    }).ToList()
                    : new List<MissionIntegrationBarrierMember>()
            };
        }
    }
}
