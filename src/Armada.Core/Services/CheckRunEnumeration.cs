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

        public static async Task<Dictionary<string, CheckRun>> ReadAllAsync(
            DatabaseDriver database,
            IReadOnlyList<CheckRunQuery> queries,
            CancellationToken token)
        {
            Dictionary<string, CheckRun> checks = new Dictionary<string, CheckRun>(StringComparer.Ordinal);
            foreach (CheckRunQuery query in queries)
            {
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
