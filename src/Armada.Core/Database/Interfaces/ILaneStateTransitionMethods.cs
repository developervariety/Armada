namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Append-only durable lane state transitions.</summary>
    public interface ILaneStateTransitionMethods
    {
        /// <summary>Append one transition.</summary>
        /// <param name="transition">Transition to store.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored transition.</returns>
        Task<LaneStateTransition> CreateAsync(LaneStateTransition transition, CancellationToken token = default);

        /// <summary>Read fleet-wide transitions created inside a bounded window. Tenant filters are ignored.</summary>
        /// <param name="query">Window and limit.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Transitions in ascending creation order and a truncation flag.</returns>
        Task<ProductionFactPage<LaneStateTransition>> EnumerateAsync(ProductionFactQuery query, CancellationToken token = default);
    }
}
