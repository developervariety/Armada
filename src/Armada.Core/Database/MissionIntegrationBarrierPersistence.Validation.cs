namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal static partial class MissionIntegrationBarrierPersistence
    {
        private static bool IsValid(MissionIntegrationBarrier barrier, Mission mission)
        {
            try
            {
                ValidateDocument(barrier, mission);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void ValidateDocument(MissionIntegrationBarrier barrier, Mission mission)
        {
            if (barrier.Version != 1 || !Enum.IsDefined(barrier.Phase)
                || !Same(barrier.IntegrationMissionId, mission.Id)
                || !Same(barrier.TenantId, mission.TenantId) || !Same(barrier.UserId, mission.UserId)
                || !Same(barrier.VoyageId, mission.VoyageId) || !Same(barrier.VesselId, mission.VesselId)
                || !HasValue(barrier.ArchitectMissionId, 128) || !HasValue(barrier.PlanHash, 128)
                || !HasValue(barrier.OriginVoyageId, 128)
                || barrier.ExpectedBranchCount < 0 || barrier.ExpectedBranchCount > MaximumMembers
                || barrier.Members == null || barrier.Members.Count > MaximumMembers)
                throw new InvalidOperationException("Integration barrier identity, scope, or count is invalid.");

            HashSet<string> branchKeys = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> rootIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> acceptedIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> currentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (MissionIntegrationBarrierMember member in barrier.Members)
            {
                if (member == null || !HasValue(member.BranchKey, 64) || !HasValue(member.RootMissionId, 128)
                    || !HasValue(member.AcceptedTerminalMissionId, 128) || !HasValue(member.CurrentTerminalMissionId, 128)
                    || !branchKeys.Add(member.BranchKey) || !rootIds.Add(member.RootMissionId)
                    || !acceptedIds.Add(member.AcceptedTerminalMissionId) || !currentIds.Add(member.CurrentTerminalMissionId)
                    || Same(member.RootMissionId, mission.Id) || Same(member.AcceptedTerminalMissionId, mission.Id)
                    || Same(member.CurrentTerminalMissionId, mission.Id))
                    throw new InvalidOperationException("Integration barrier members must have unique bounded branch and mission identifiers.");
            }

            if (barrier.Phase == MissionIntegrationBarrierPhaseEnum.Preparing && barrier.Members.Count != 0)
                throw new InvalidOperationException("A preparing integration barrier cannot contain accepted members.");
            if (barrier.Phase == MissionIntegrationBarrierPhaseEnum.Accepted
                && barrier.Members.Count != barrier.ExpectedBranchCount)
                throw new InvalidOperationException("An accepted integration barrier must contain every expected branch.");
        }

        private static void ValidateTransition(MissionIntegrationBarrier current, MissionIntegrationBarrier next)
        {
            if (current.Phase == MissionIntegrationBarrierPhaseEnum.Preparing)
            {
                if (next.Phase != MissionIntegrationBarrierPhaseEnum.Accepted
                    || !Same(current.ArchitectMissionId, next.ArchitectMissionId)
                    || !Same(current.PlanHash, next.PlanHash)
                    || !Same(current.OriginVoyageId, next.OriginVoyageId)
                    || current.ExpectedBranchCount != next.ExpectedBranchCount)
                    throw new InvalidOperationException("A preparing barrier can only accept its original plan.");
                return;
            }

            if (current.Phase != MissionIntegrationBarrierPhaseEnum.Accepted
                || next.Phase != MissionIntegrationBarrierPhaseEnum.Accepted
                || !Same(current.ArchitectMissionId, next.ArchitectMissionId)
                || !Same(current.PlanHash, next.PlanHash)
                || !Same(current.OriginVoyageId, next.OriginVoyageId)
                || current.ExpectedBranchCount != next.ExpectedBranchCount
                || current.Members.Count != next.Members.Count)
                throw new InvalidOperationException("Accepted barrier membership is immutable.");

            int changedCurrentIds = 0;
            for (int i = 0; i < current.Members.Count; i++)
            {
                MissionIntegrationBarrierMember before = current.Members[i];
                MissionIntegrationBarrierMember after = next.Members[i];
                if (!Same(before.BranchKey, after.BranchKey) || !Same(before.RootMissionId, after.RootMissionId)
                    || !Same(before.AcceptedTerminalMissionId, after.AcceptedTerminalMissionId))
                    throw new InvalidOperationException("Accepted branch membership cannot change.");
                if (!Same(before.CurrentTerminalMissionId, after.CurrentTerminalMissionId)) changedCurrentIds++;
            }
            if (changedCurrentIds > 1)
                throw new InvalidOperationException("An accepted barrier update can change only one current terminal ID.");
        }

        private static bool HasValue(string? value, int maximumLength) =>
            !String.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

        private static bool Same(string? first, string? second) =>
            String.Equals(first, second, StringComparison.Ordinal);
    }
}
