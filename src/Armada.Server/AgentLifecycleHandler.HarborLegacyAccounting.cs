namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class AgentLifecycleHandler
    {
        private async Task<bool> TryAccountLegacyOperatorStoppedJobAsync(HarborJobSnapshot job, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(job.MissionId)
                || String.IsNullOrWhiteSpace(job.CaptainId)
                || String.IsNullOrWhiteSpace(job.OwnerTenantId)
                || String.IsNullOrWhiteSpace(job.OwnerUserId)
                || String.IsNullOrWhiteSpace(job.FailureReason)
                || !job.FailureReason.StartsWith("operator_confirmed_stopped:", StringComparison.Ordinal))
                return false;

            Mission? mission = await _Database.Missions.ReadAsync(job.MissionId, token).ConfigureAwait(false);
            Captain? captain = await _Database.Captains.ReadAsync(job.CaptainId, token).ConfigureAwait(false);
            if (mission == null || captain == null
                || !String.Equals(mission.Id, job.MissionId, StringComparison.Ordinal)
                || !String.Equals(mission.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                || !String.Equals(mission.UserId, job.OwnerUserId, StringComparison.Ordinal)
                || !String.Equals(captain.Id, job.CaptainId, StringComparison.Ordinal)
                || !String.Equals(captain.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                || !String.Equals(captain.UserId, job.OwnerUserId, StringComparison.Ordinal))
                return false;

            if (!String.IsNullOrWhiteSpace(mission.HarborJobId)
                && !String.Equals(mission.HarborJobId, job.JobId, StringComparison.Ordinal))
            {
                _Logging.Info(_Header + "operator-confirmed legacy Harbor job " + job.JobId
                    + " is superseded by a different mission attempt; leaving current state unchanged");
                return true;
            }

            if (!String.IsNullOrWhiteSpace(mission.HarborJobId)
                || mission.Status != MissionStatusEnum.Pending
                || mission.ProcessId.HasValue
                || (!String.IsNullOrWhiteSpace(mission.CaptainId)
                    && !String.Equals(mission.CaptainId, job.CaptainId, StringComparison.Ordinal))
                || captain.State != CaptainStateEnum.Idle
                || captain.ProcessId.HasValue
                || !String.IsNullOrWhiteSpace(captain.CurrentMissionId))
                return false;

            _Logging.Info(_Header + "operator-confirmed legacy Harbor job " + job.JobId
                + " has no active mission or captain binding; accounting the verified stop without changing mission state");
            return true;
        }
    }
}
