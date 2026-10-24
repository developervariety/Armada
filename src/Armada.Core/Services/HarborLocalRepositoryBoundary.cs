namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Prevents local-only repository controls from acting as an implicit Harbor fallback.</summary>
    public static class HarborLocalRepositoryBoundary
    {
        /// <summary>Named refusal for a local-only operation that lacks a specific checkout owner.</summary>
        public const string ReasonExplicitCheckoutRequired = "harbor_repository_requires_explicit_checkout";

        /// <summary>Whether a vessel has explicit configured or durable Harbor checkout ownership.</summary>
        public static async Task<bool> HasOwnedCheckoutAsync(DatabaseDriver database, HarborSettings? settings,
            string vesselId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            HarborMissionRoute? route = HarborMissionRouting.ResolveConfiguredForVessel(settings, vesselId);
            if (route?.CheckoutMode == HarborCheckoutModeEnum.HarborOwned) return true;
            List<Dock> docks = await database.Docks.EnumerateByVesselAsync(vesselId, token).ConfigureAwait(false);
            foreach (Dock dock in docks)
                if (dock.HostCheckout != null) return true;
            return false;
        }
    }
}
