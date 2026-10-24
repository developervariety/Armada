namespace Armada.Server
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;

    public partial class AgentLifecycleHandler
    {
        private static bool HasMissionAttempt(HarborJobSnapshot job)
        {
            return !String.IsNullOrWhiteSpace(job.MissionId)
                && !String.IsNullOrWhiteSpace(job.CaptainId)
                && !String.IsNullOrWhiteSpace(job.DockId)
                && job.AdmiralProcessId.HasValue
                && job.AdmiralProcessId.Value > ProcessSupervisor.SyntheticProcessIdFloor
                && job.AdmiralProcessStartedUtc.HasValue;
        }

        private static bool MatchesDurableJobOwnership(HarborJobSnapshot job, Mission? mission, Captain? captain, Dock? dock)
        {
            if (mission == null || captain == null || dock == null || !HasMissionAttempt(job)) return false;
            return String.Equals(mission.HarborJobId, job.JobId, StringComparison.Ordinal)
                && String.Equals(mission.Id, job.MissionId, StringComparison.Ordinal)
                && String.Equals(mission.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                && String.Equals(mission.UserId, job.OwnerUserId, StringComparison.Ordinal)
                && (String.IsNullOrWhiteSpace(mission.CaptainId) || String.Equals(mission.CaptainId, job.CaptainId, StringComparison.Ordinal))
                && (String.IsNullOrWhiteSpace(mission.DockId) || String.Equals(mission.DockId, job.DockId, StringComparison.Ordinal))
                && String.Equals(captain.Id, job.CaptainId, StringComparison.Ordinal)
                && String.Equals(captain.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                && String.Equals(captain.UserId, job.OwnerUserId, StringComparison.Ordinal)
                && String.Equals(dock.Id, job.DockId, StringComparison.Ordinal)
                && String.Equals(dock.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                && String.Equals(dock.VesselId, mission.VesselId, StringComparison.Ordinal)
                && (String.IsNullOrWhiteSpace(dock.CaptainId) || String.Equals(dock.CaptainId, job.CaptainId, StringComparison.Ordinal))
                && !String.IsNullOrWhiteSpace(dock.WorktreePath);
        }

        private static bool MatchesActiveAttempt(HarborJobSnapshot job, Mission? mission, Captain? captain, Dock? dock)
        {
            if (!MatchesAttemptOwnership(job, mission, captain, dock)) return false;
            return (mission!.Status == MissionStatusEnum.Assigned || mission.Status == MissionStatusEnum.InProgress)
                && captain!.State == CaptainStateEnum.Working
                && String.Equals(captain.CurrentMissionId, job.MissionId, StringComparison.Ordinal)
                && String.Equals(captain.CurrentDockId, job.DockId, StringComparison.Ordinal)
                && mission.ProcessId == job.AdmiralProcessId
                && mission.ProcessStartedUtc == job.AdmiralProcessStartedUtc
                && captain.ProcessId == job.AdmiralProcessId
                && captain.ProcessStartedUtc == job.AdmiralProcessStartedUtc;
        }

        private static bool MatchesAttemptOwnership(HarborJobSnapshot job, Mission? mission, Captain? captain, Dock? dock)
        {
            if (mission == null || captain == null || dock == null || !HasMissionAttempt(job)) return false;
            return String.Equals(mission.HarborJobId, job.JobId, StringComparison.Ordinal)
                && String.Equals(mission.Id, job.MissionId, StringComparison.Ordinal)
                && String.Equals(mission.CaptainId, job.CaptainId, StringComparison.Ordinal)
                && String.Equals(mission.DockId, job.DockId, StringComparison.Ordinal)
                && String.Equals(captain.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                && String.Equals(captain.UserId, job.OwnerUserId, StringComparison.Ordinal)
                && String.Equals(mission.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                && String.Equals(mission.UserId, job.OwnerUserId, StringComparison.Ordinal)
                && dock.Active
                && String.Equals(dock.Id, job.DockId, StringComparison.Ordinal)
                && String.Equals(dock.CaptainId, job.CaptainId, StringComparison.Ordinal)
                && String.Equals(dock.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                && String.Equals(dock.VesselId, mission.VesselId, StringComparison.Ordinal)
                && !String.IsNullOrWhiteSpace(dock.WorktreePath);
        }
    }
}
