namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Protects Harbor repository identities that queued landing still needs.</summary>
    public partial class DockService
    {
        private async Task<bool> HarborDockHasPendingLandingAsync(Dock dock, CancellationToken token)
        {
            string? missionId = dock.HostCheckoutLifecycle?.MissionId ?? dock.GitAnchorsSnapshot?.MissionId;
            if (String.IsNullOrWhiteSpace(missionId) || String.IsNullOrWhiteSpace(dock.TenantId)) return false;
            System.Collections.Generic.List<MergeEntry> entries = await _Database.MergeEntries
                .EnumerateAsync(dock.TenantId, token).ConfigureAwait(false);
            foreach (MergeEntry entry in entries)
            {
                if (!String.Equals(entry.MissionId, missionId, StringComparison.Ordinal)) continue;
                if (entry.Status != MergeStatusEnum.Landed && entry.Status != MergeStatusEnum.Failed
                    && entry.Status != MergeStatusEnum.Cancelled)
                    return true;
            }
            return false;
        }
    }
}
