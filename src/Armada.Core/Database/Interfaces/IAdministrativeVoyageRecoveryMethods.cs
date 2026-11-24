namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    /// <summary>Atomic evidence capture and nonlaunching correction of an administrative completion.</summary>
    public interface IAdministrativeVoyageRecoveryMethods
    {
        /// <summary>Preview or correct Complete to Failed; preserve all mission rows and original completion time.</summary>
        Task<AdministrativeVoyageRecoveryPlan> ExecuteAsync(string tenantId, string userId, string voyageId,
            AdministrativeVoyageRecoveryRequest request, CancellationToken token = default);

        /// <summary>Atomically consume the owned permit and resume only its original mission on its preserved source.</summary>
        Task<Mission> ConsumeRestartPermitAsync(string tenantId, string userId, string missionId, AdministrativeRestartPermit permit,
            AdministrativeVoyageRecoveryRequest verifiedRequest, FleetCapacityReservation reservation, CancellationToken token = default);
    }
}
