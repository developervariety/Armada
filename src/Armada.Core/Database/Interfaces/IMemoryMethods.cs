namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Storage operations for native captain memory records and their tags. Visibility, search,
    /// ordering and paging belong to the memory service; this layer only fences by tenant.
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
    }
}
