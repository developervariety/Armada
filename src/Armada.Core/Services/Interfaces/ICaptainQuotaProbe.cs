namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Probes whether a quarantined captain's provider quota has recovered so it can be
    /// returned to service early, before the full bench window elapses.
    /// </summary>
    public interface ICaptainQuotaProbe
    {
        /// <summary>
        /// Returns true when the captain's provider quota appears to have recovered.
        /// </summary>
        /// <param name="captain">Quarantined captain to probe.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the quota has recovered and the captain may be restored.</returns>
        Task<bool> HasRecoveredAsync(Captain captain, CancellationToken token = default);
    }
}
