namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Storage operations for native captain memory records and their tags. The whole-list reads fence by tenant
    /// only; the paged read also applies the caller's visibility, the filters, the order and the page.
    /// </summary>
    public interface IMemoryMethods
    {
        /// <summary>
        /// Insert a record and its tag rows in one transaction. A second record with the same
        /// non-null key in the same tenant is rejected by the storage unique index.
        /// </summary>
        Task<Memory> CreateAsync(Memory memory, CancellationToken token = default);

        /// <summary>
        /// Read a record by identifier in any tenant, or null.
        /// </summary>
        Task<Memory?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Read a record by identifier inside one tenant, or null.
        /// </summary>
        Task<Memory?> ReadAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Read the record with a key inside one tenant, or null.
        /// </summary>
        Task<Memory?> ReadByKeyAsync(string tenantId, string key, CancellationToken token = default);

        /// <summary>
        /// Replace a record's mutable fields and tags only when its stored version equals
        /// <paramref name="expectedVersion"/>. The memory must carry its tenant. Returns false, and
        /// changes nothing, when the record is absent, belongs to another tenant, or changed since
        /// it was read.
        /// </summary>
        Task<bool> UpdateAsync(Memory memory, int expectedVersion, CancellationToken token = default);

        /// <summary>
        /// Delete a record and its tags inside one tenant. Returns false when nothing was deleted.
        /// </summary>
        Task<bool> DeleteAsync(string tenantId, string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate every record in every tenant, newest first.
        /// </summary>
        Task<List<Memory>> EnumerateAsync(CancellationToken token = default);

        /// <summary>
        /// Enumerate the records of one tenant, newest first.
        /// </summary>
        Task<List<Memory>> EnumerateAsync(string tenantId, CancellationToken token = default);

        /// <summary>
        /// Read one page of records. The database applies the tenant and viewer fence, the filters of the request,
        /// the order (salience descending, then newest first, then identifier) and the page, so only the page's
        /// records are read. <see cref="EnumerationResult{T}.TotalRecords"/> counts every record that matches.
        /// </summary>
        /// <param name="request">Paging, range, vessel, voyage, mission, search, type and topic filters.</param>
        /// <param name="tenantId">Tenant to read; null reads every tenant.</param>
        /// <param name="viewerUserId">User whose view applies: a user-specific record of another user is left out.
        /// Null applies no per-record fence, for a global or tenant administrator.</param>
        /// <param name="token">Cancellation token.</param>
        Task<EnumerationResult<Memory>> EnumeratePageAsync(MemoryEnumerateRequest request, string? tenantId, string? viewerUserId, CancellationToken token = default);

        /// <summary>
        /// Enumerate the records of one tenant that concern one vessel: records about or originating from it,
        /// and records with no vessel association. Newest first.
        /// </summary>
        Task<List<Memory>> EnumerateForVesselAsync(string tenantId, string vesselId, CancellationToken token = default);
    }
}
