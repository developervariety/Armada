namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for vessel pack-curate hints (v2-F1).
    /// </summary>
    public interface IVesselPackHintMethods
    {
        /// <summary>Create a hint row.</summary>
        Task<VesselPackHint> CreateAsync(VesselPackHint hint, CancellationToken token = default);

        /// <summary>Read a hint row by id.</summary>
        Task<VesselPackHint?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>Update a hint row in place.</summary>
        Task<VesselPackHint> UpdateAsync(VesselPackHint hint, CancellationToken token = default);

        /// <summary>Mark a hint inactive (soft delete).</summary>
        Task DeactivateAsync(string id, CancellationToken token = default);

        /// <summary>Hard-delete a hint row by id.</summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>Enumerate all hint rows for a vessel (active and inactive).</summary>
        Task<List<VesselPackHint>> EnumerateByVesselAsync(string vesselId, CancellationToken token = default);

        /// <summary>Enumerate active hint rows for a vessel.</summary>
        Task<List<VesselPackHint>> EnumerateActiveByVesselAsync(string vesselId, CancellationToken token = default);
    }
}
