namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    public partial class DefinitionOfDoneGate
    {
        /// <summary>Explicit checkout routes used by owned verification.</summary>
        public HarborSettings? HarborSettings { get; set; }

        /// <summary>Owner-bound workspace provider for Harbor-owned gate commands.</summary>
        public HarborCheckoutService? HarborCheckouts { get; set; }

        private bool IsHarborOwnedMission(Mission mission, Dock dock)
        {
            if (dock.HostCheckout != null) return true;
            HarborMissionRoute? captainRoute = null;
            if (HarborSettings?.MissionRoutes != null && !String.IsNullOrWhiteSpace(mission.CaptainId))
                foreach (HarborMissionRoute route in HarborSettings.MissionRoutes)
                    if (route != null && String.Equals(route.CaptainId?.Trim(), mission.CaptainId, StringComparison.Ordinal))
                    {
                        captainRoute = route;
                        break;
                    }
            HarborMissionRoute? routeToUse = captainRoute ?? HarborMissionRouting.ResolveConfiguredForVessel(HarborSettings, mission.VesselId);
            return routeToUse?.CheckoutMode == Armada.Core.Enums.HarborCheckoutModeEnum.HarborOwned;
        }

        private async Task<bool> HasDeclaredConsumersAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.VesselId)) return true;
            Vessel? producer = await ReadVesselAsync(mission.TenantId, mission.VesselId!, token).ConfigureAwait(false);
            if (producer == null) return true;
            List<Vessel> vessels = await EnumerateVesselsAsync(mission.TenantId, token).ConfigureAwait(false);
            return ConsumerVesselResolver.Resolve(producer.Id, producer.Name, vessels).Count > 0;
        }

    }
}
