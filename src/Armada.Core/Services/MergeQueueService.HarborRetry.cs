namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class MergeQueueService
    {
        private async Task<bool> RebuildStaleHarborCandidateAsync(MergeEntry entry, HarborQueueContext context,
            string stale, CancellationToken token)
        {
            Dock? integration = await ReadHarborIntegrationAsync(entry, token).ConfigureAwait(false);
            if (integration != null)
            {
                if (!String.Equals(integration.VesselId, entry.VesselId, StringComparison.Ordinal)
                    || !String.Equals(integration.TenantId, entry.TenantId, StringComparison.Ordinal)
                    || !String.Equals(integration.UserId, entry.UserId, StringComparison.Ordinal)
                    || !SameHarborOwner(context.Binding, integration.HostCheckout))
                    throw new InvalidOperationException("harbor_integration_checkout_owner_mismatch");
                if (integration.Active)
                    await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
            }
            return await RebuildStaleCandidateAsync(entry, stale, token).ConfigureAwait(false);
        }
    }
}
