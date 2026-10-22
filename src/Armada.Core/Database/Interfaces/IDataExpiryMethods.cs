namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Retention purge of expired operational records. One provider-neutral implementation serves
    /// every database provider, so the retention rules are defined once.
    /// </summary>
    public interface IDataExpiryMethods
    {
        /// <summary>
        /// Delete every expired row the retention rules select at the given cutoffs.
        /// </summary>
        /// <param name="cutoffs">Retention cutoffs. A null cutoff leaves its tables untouched.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Rows deleted per table, in purge order.</returns>
        Task<DataExpiryResult> PurgeExpiredAsync(DataExpiryCutoffs cutoffs, CancellationToken token = default);

        /// <summary>
        /// Delete every expired row the retention rules select at the given cutoffs, handing each
        /// delete statement's rows to the exporter first.
        /// </summary>
        /// <param name="cutoffs">Retention cutoffs. A null cutoff leaves its tables untouched.</param>
        /// <param name="exporter">Receives the rows of each delete before it runs; null deletes without an export.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Rows deleted per table, in purge order.</returns>
        Task<DataExpiryResult> PurgeExpiredAsync(DataExpiryCutoffs cutoffs, IDataExpiryRowExporter? exporter, CancellationToken token);
    }
}
