namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Storage;

    /// <summary>Read-only provider statistics. Exact counts are explicitly requested and cancellable.</summary>
    public interface IStorageMethods
    {
        /// <summary>Measure the configured database. Metadata is the default; exact counts may scan tables.</summary>
        Task<StorageDatabase> MeasureAsync(bool exactCounts = false, CancellationToken token = default);
    }
}
