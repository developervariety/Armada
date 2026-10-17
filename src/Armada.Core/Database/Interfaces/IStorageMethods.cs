namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Storage;

    /// <summary>Read-only, bounded and cancellable provider statistics.</summary>
    public interface IStorageMethods
    {
        /// <summary>Measure the configured database. Server databases use catalog metadata by default; SQLite scans table values for counts and logical sizes.</summary>
        Task<StorageDatabase> MeasureAsync(bool exactCounts = false, CancellationToken token = default);
    }
}
