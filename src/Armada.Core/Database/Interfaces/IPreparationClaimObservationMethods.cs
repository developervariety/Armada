namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Append-only durable preparation claim observations.</summary>
    public interface IPreparationClaimObservationMethods
    {
        /// <summary>Append one observation.</summary>
        /// <param name="observation">Observation to store.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored observation.</returns>
        Task<PreparationClaimObservation> CreateAsync(PreparationClaimObservation observation, CancellationToken token = default);

        /// <summary>Read observations created inside a bounded window.</summary>
        /// <param name="query">Window and scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Observations in ascending creation order and a truncation flag.</returns>
        Task<ProductionFactPage<PreparationClaimObservation>> EnumerateAsync(ProductionFactQuery query, CancellationToken token = default);
    }
}
