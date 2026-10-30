namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;

    /// <summary>The persisted dispatch hold: at most one hold, kept while it is engaged.</summary>
    public interface IDispatchHoldMethods
    {
        /// <summary>Store the engaged hold, replacing any stored hold.</summary>
        /// <param name="hold">Hold to store.</param>
        /// <param name="token">Cancellation token.</param>
        Task SaveAsync(DispatchHoldSnapshot hold, CancellationToken token = default);

        /// <summary>Remove the stored hold. Does nothing when none is stored.</summary>
        /// <param name="token">Cancellation token.</param>
        Task ClearAsync(CancellationToken token = default);

        /// <summary>Read the stored hold.</summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored hold, or null when none is stored.</returns>
        Task<DispatchHoldSnapshot?> ReadAsync(CancellationToken token = default);

        /// <summary>
        /// Persist a vessel hold, replacing any stored hold of the same vessel.
        /// </summary>
        /// <param name="hold">The hold; <see cref="DispatchHoldSnapshot.VesselId"/> is required.</param>
        /// <param name="token">Cancellation token.</param>
        Task SaveVesselAsync(DispatchHoldSnapshot hold, CancellationToken token = default);

        /// <summary>
        /// Remove the stored hold of one vessel.
        /// </summary>
        /// <param name="vesselId">The vessel.</param>
        /// <param name="token">Cancellation token.</param>
        Task ClearVesselAsync(string vesselId, CancellationToken token = default);

        /// <summary>
        /// Read every stored vessel hold.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The holds, each with its vessel id.</returns>
        Task<List<DispatchHoldSnapshot>> ReadVesselsAsync(CancellationToken token = default);
    }
}
