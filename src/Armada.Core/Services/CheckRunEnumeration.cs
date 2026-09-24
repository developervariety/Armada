namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Reads complete Check scopes for lifecycle gates.
    /// </summary>
    internal static class CheckRunEnumeration
    {
        private const int PageSize = 100;

        /// <summary>
        /// Read every Check matching the queries, restricted to the tenant that owns the mission or voyage being
        /// gated. A Check is linked by id alone, so without the tenant a record written in another tenant could
        /// count in this tenant's gate. A null tenant (a record created before tenancy) reads unrestricted.
        /// </summary>
        public static async Task<Dictionary<string, CheckRun>> ReadAllAsync(
            DatabaseDriver database,
            string? tenantId,
            IReadOnlyList<CheckRunQuery> queries,
            CancellationToken token)
        {
            Dictionary<string, CheckRun> checks = new Dictionary<string, CheckRun>(StringComparer.Ordinal);
            foreach (CheckRunQuery query in queries)
            {
                if (!String.IsNullOrWhiteSpace(tenantId)) query.TenantId = tenantId;
                int pageNumber = 1;
                while (true)
                {
                    query.PageNumber = pageNumber;
                    query.PageSize = PageSize;
                    EnumerationResult<CheckRun> page = await database.CheckRuns
                        .EnumerateAsync(query, token).ConfigureAwait(false);
                    foreach (CheckRun check in page.Objects) checks[check.Id] = check;

                    if (page.Objects.Count == 0
                        || (page.TotalPages > 0 && pageNumber >= page.TotalPages)
                        || (page.TotalPages <= 0 && page.Objects.Count < PageSize))
                    {
                        break;
                    }
                    pageNumber++;
                }
            }
            return checks;
        }
    }
}
