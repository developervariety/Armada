namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Applies <see cref="OwnershipPolicy"/> to lists and name lookups of owned configuration records.
    /// Paging runs over the caller-visible set, so totals never count a record the caller cannot read.
    /// </summary>
    public static class OwnedRecordScope
    {
        #region Public-Methods

        /// <summary>
        /// Filter a full list to the records the caller may read, apply the date filters and order,
        /// and return one page.
        /// </summary>
        /// <typeparam name="T">Record type.</typeparam>
        /// <param name="records">Every stored record.</param>
        /// <param name="auth">Caller.</param>
        /// <param name="query">Paging, order and date filters.</param>
        /// <returns>One page of visible records.</returns>
        public static EnumerationResult<T> Page<T>(IEnumerable<T> records, AuthContext auth, EnumerationQuery? query) where T : IOwnedRecord
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (query == null) query = new EnumerationQuery();

            IEnumerable<T> visible = records.Where(record => OwnershipPolicy.CanView(auth, record));
            if (query.CreatedAfter.HasValue) visible = visible.Where(record => record.CreatedUtc > query.CreatedAfter.Value);
            if (query.CreatedBefore.HasValue) visible = visible.Where(record => record.CreatedUtc < query.CreatedBefore.Value);

            List<T> ordered = query.Order == EnumerationOrderEnum.CreatedAscending
                ? visible.OrderBy(record => record.CreatedUtc).ToList()
                : visible.OrderByDescending(record => record.CreatedUtc).ToList();

            List<T> page = ordered
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToList();

            return EnumerationResult<T>.Create(query, page, ordered.Count);
        }

        /// <summary>
        /// Find a record by name as the caller sees it. A record in the caller's own tenant wins when
        /// the caller may read it; otherwise a shared record of the same name is returned, and a
        /// global administrator also reaches every tenant.
        /// </summary>
        /// <typeparam name="T">Record type.</typeparam>
        /// <param name="auth">Caller.</param>
        /// <param name="name">Record name.</param>
        /// <param name="readByTenantAndName">Reads a record by tenant and name.</param>
        /// <param name="readAll">Reads every stored record.</param>
        /// <param name="nameOf">Returns a record's name.</param>
        /// <returns>The visible record, or null.</returns>
        public static async Task<T?> ReadByNameAsync<T>(
            AuthContext auth,
            string name,
            Func<string, string, Task<T?>> readByTenantAndName,
            Func<Task<List<T>>> readAll,
            Func<T, string> nameOf) where T : class, IOwnedRecord
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (!auth.IsAuthenticated) return null;

            T? own = await readByTenantAndName(OwnershipPolicy.TenantOf(auth), name).ConfigureAwait(false);
            if (own != null && OwnershipPolicy.CanView(auth, own)) return own;

            List<T> all = await readAll().ConfigureAwait(false);
            List<T> sameName = all.Where(record => String.Equals(nameOf(record), name, StringComparison.Ordinal)).ToList();
            T? shared = sameName.FirstOrDefault(record => record.IsBuiltIn && OwnershipPolicy.CanView(auth, record));
            if (shared != null) return shared;
            return sameName.FirstOrDefault(record => OwnershipPolicy.CanView(auth, record));
        }

        /// <summary>
        /// Find a record by name that may be used for another owner's work, such as the persona or
        /// pipeline a mission or vessel names. A usable record in the owner's own tenant wins, then a
        /// usable shared record. Records of that name the owner may not use are counted, never returned.
        /// </summary>
        /// <typeparam name="T">Record type.</typeparam>
        /// <param name="ownerTenantId">Tenant that owns the work.</param>
        /// <param name="ownerUserId">User that owns the work.</param>
        /// <param name="name">Record name.</param>
        /// <param name="readAll">Reads every stored record.</param>
        /// <param name="nameOf">Returns a record's name.</param>
        /// <returns>The usable record and the number of refused same-name records.</returns>
        public static async Task<OwnedRecordLookup<T>> ReadUsableByNameAsync<T>(
            string? ownerTenantId,
            string? ownerUserId,
            string name,
            Func<Task<List<T>>> readAll,
            Func<T, string> nameOf) where T : class, IOwnedRecord
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            List<T> sameName = (await readAll().ConfigureAwait(false))
                .Where(record => String.Equals(nameOf(record), name, StringComparison.Ordinal))
                .ToList();

            string ownerTenant = String.IsNullOrWhiteSpace(ownerTenantId) ? Constants.DefaultTenantId : ownerTenantId!;
            List<T> usable = sameName
                .Where(record => OwnershipPolicy.CanUseFor(ownerTenantId, ownerUserId, record))
                .OrderByDescending(record => String.Equals(record.TenantId, ownerTenant, StringComparison.Ordinal))
                .ThenByDescending(record => record.IsBuiltIn)
                .ToList();

            return new OwnedRecordLookup<T>(usable.FirstOrDefault(), sameName.Count - usable.Count);
        }

        /// <summary>
        /// Return the record when it may be used for the given owner's work, otherwise null.
        /// </summary>
        /// <typeparam name="T">Record type.</typeparam>
        /// <param name="record">Record, or null.</param>
        /// <param name="ownerTenantId">Tenant that owns the work.</param>
        /// <param name="ownerUserId">User that owns the work.</param>
        /// <returns>The record, or null.</returns>
        public static T? UsableFor<T>(T? record, string? ownerTenantId, string? ownerUserId) where T : class, IOwnedRecord
        {
            if (record == null) return null;
            return OwnershipPolicy.CanUseFor(ownerTenantId, ownerUserId, record) ? record : null;
        }

        #endregion
    }
}
