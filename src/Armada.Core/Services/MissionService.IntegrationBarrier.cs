namespace Armada.Core.Services
{
    using Armada.Core.Models;

    public partial class MissionService
    {
        private const string IntegrationBarrierWaiting = "integration_barrier_waiting";
        private const string IntegrationBarrierUnverifiable = "integration_barrier_unverifiable";

        private static bool HasIntegrationBarrierRecord(Mission mission)
        {
            return mission.IntegrationBarrier != null || mission.IntegrationBarrierInvalid
                || mission.IntegrationBarrierRawJsonPresent;
        }
    }
}
