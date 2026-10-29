namespace Armada.Core.Database.Interfaces
{
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
    }
}
