namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MergeQueueService
    {
        private async Task<HarborQueueContext?> ReadHarborQueueContextAsync(MergeEntry entry, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(entry.MissionId)) return null;
            Mission? mission = !String.IsNullOrWhiteSpace(entry.TenantId)
                ? await _Database.Missions.ReadAsync(entry.TenantId!, entry.MissionId!, token).ConfigureAwait(false)
                : await _Database.Missions.ReadAsync(entry.MissionId!, token).ConfigureAwait(false);
            if (mission == null || String.IsNullOrWhiteSpace(mission.DockId) || String.IsNullOrWhiteSpace(entry.VesselId)) return null;
            Dock? dock = await _Database.Docks.ReadAsync(mission.DockId!, token).ConfigureAwait(false);
            if (dock == null) return null;
            if (dock.HostCheckout == null)
            {
                if (dock.HostCheckoutLifecycle != null) throw new InvalidOperationException("harbor_checkout_lifecycle_incomplete");
                return null;
            }
            Vessel? vessel = !String.IsNullOrWhiteSpace(entry.TenantId)
                ? await _Database.Vessels.ReadAsync(entry.TenantId!, entry.VesselId!, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(entry.VesselId!, token).ConfigureAwait(false);
            if (!dock.HostCheckout.IsComplete || !String.Equals(dock.Id, mission.DockId, StringComparison.Ordinal)
                || !String.Equals(dock.VesselId, entry.VesselId, StringComparison.Ordinal)
                || !String.Equals(dock.TenantId, entry.TenantId, StringComparison.Ordinal)
                || !String.Equals(dock.UserId, entry.UserId, StringComparison.Ordinal)
                || !String.Equals(mission.VesselId, entry.VesselId, StringComparison.Ordinal)
                || !String.Equals(mission.TenantId, entry.TenantId, StringComparison.Ordinal)
                || !String.Equals(mission.UserId, entry.UserId, StringComparison.Ordinal)
                || vessel == null || !String.Equals(vessel.TenantId, dock.TenantId, StringComparison.Ordinal)
                || !String.Equals(vessel.UserId, dock.UserId, StringComparison.Ordinal))
                throw new InvalidOperationException("harbor_merge_entry_binding_invalid");
            return new HarborQueueContext(mission, dock, dock.HostCheckout, vessel);
        }

        private static string HarborQueueAttemptId(string entryId, int retryCount)
        {
            string material = entryId + "\n" + retryCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "mq_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        }

        private static bool SameHarborOwner(HarborCheckoutBinding expected, HarborCheckoutBinding? actual)
        {
            return actual != null && actual.IsComplete
                && String.Equals(expected.RunnerId, actual.RunnerId, StringComparison.Ordinal)
                && expected.EnrollmentGeneration == actual.EnrollmentGeneration
                && String.Equals(expected.TenantId, actual.TenantId, StringComparison.Ordinal)
                && String.Equals(expected.UserId, actual.UserId, StringComparison.Ordinal)
                && String.Equals(expected.RepositoryGrantId, actual.RepositoryGrantId, StringComparison.Ordinal)
                && String.Equals(expected.RepositoryFingerprint, actual.RepositoryFingerprint, StringComparison.Ordinal);
        }

        private sealed record HarborQueueContext(Mission Mission, Dock SourceDock, HarborCheckoutBinding Binding, Vessel Vessel);
    }
}
