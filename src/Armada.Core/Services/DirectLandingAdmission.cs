namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Serializes direct publication with operator MissionCancellation and VoyageCancellation
    /// writes in this process only. Internal cancellation writers do not join this protocol.
    /// The lease covers publication or durable state changes, never validation or captain recall.
    /// Keys include the tenant and entity kind; every mission of a voyage uses its voyage id.
    /// Publication is refused when cancellation becomes durable first; cancellation after
    /// publication does not undo a ref that was already advanced.
    /// </summary>
    public static class DirectLandingAdmission
    {
        private static readonly KeyedAsyncLock _Locks = new KeyedAsyncLock();

        /// <summary>Acquire the publication boundary for a voyage, or a standalone mission.</summary>
        public static async Task<IDisposable> AcquireAsync(DatabaseDriver database, string? tenantId, string? voyageId, string missionId, CancellationToken token = default)
        {
            // Resolve the owning row before choosing the key. Input objects may still have a
            // null tenant even when persistence supplied the default tenant. A voyage's row is
            // authoritative for every mission in it, including cancellation called on the voyage.
            if (!String.IsNullOrEmpty(missionId))
            {
                Mission? stored = await database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
                if (stored != null)
                {
                    tenantId = stored.TenantId;
                    voyageId = stored.VoyageId;
                }
            }
            if (!String.IsNullOrEmpty(voyageId))
            {
                Voyage? voyage = await database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                if (voyage != null) tenantId = voyage.TenantId;
            }
            string tenant = tenantId ?? String.Empty;
            string entity = !String.IsNullOrEmpty(voyageId) ? "voyage:" + voyageId : "mission:" + missionId;
            return await _Locks.AcquireAsync(tenant.Length + ":" + tenant + ":" + entity, token).ConfigureAwait(false);
        }

        /// <summary>Read durable cancellation state while the caller holds the admission lease.</summary>
        public static async Task<bool> IsCancelledAsync(DatabaseDriver database, Mission mission, CancellationToken token = default)
        {
            Mission? stored = await database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            string? voyageId = stored != null ? stored.VoyageId : mission.VoyageId;
            bool cancelled = (stored?.Status ?? mission.Status) == MissionStatusEnum.Cancelled;
            if (!String.IsNullOrEmpty(voyageId))
            {
                Voyage? voyage = await database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                cancelled |= voyage?.Status == VoyageStatusEnum.Cancelled;
            }
            if (cancelled && stored != null)
            {
                mission.Status = stored.Status;
                mission.FailureReason = stored.FailureReason;
                mission.CompletedUtc = stored.CompletedUtc;
            }
            return cancelled;
        }
    }
}
