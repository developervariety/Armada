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
        /// <inheritdoc />
        public async Task<HarborJobRecoveryResult> TryAdoptAsync(HarborJobSnapshot job, HarborRunnerSession session, CancellationToken token = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (!String.Equals(job.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)
                || job.EnrollmentGeneration != session.EnrollmentGeneration
                || !String.Equals(job.OwnerTenantId, session.Identity.TenantId, StringComparison.Ordinal)
                || !String.Equals(job.OwnerUserId, session.Identity.UserId, StringComparison.Ordinal)
                || !HasMissionAttempt(job)
                || !job.AdmiralProcessId.HasValue
                || !_RestoredHarborEvents.TryGetValue(job.JobId, out IHarborProcessEvents? events)
                || !_ProcessStateOwners.ContainsKey(job.AdmiralProcessId.Value))
                return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain);

            Mission? mission = await _Database.Missions.ReadAsync(job.MissionId!, token).ConfigureAwait(false);
            Captain? captain = await _Database.Captains.ReadAsync(job.CaptainId!, token).ConfigureAwait(false);
            Dock? dock = await _Database.Docks.ReadAsync(job.DockId!, token).ConfigureAwait(false);
            if (!MatchesActiveAttempt(job, mission, captain, dock))
                return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain);
            return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Adopted,
                new RestoredHarborObserver(this, job.JobId, job.AdmiralProcessId.Value, events));
        }
    }
}
