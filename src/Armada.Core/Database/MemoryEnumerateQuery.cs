namespace Armada.Core.Database
{
    using System.Collections.Generic;

    /// <summary>
    /// Provider-neutral SQL fragments and parameters for one paged memory enumeration.
    /// </summary>
    internal class MemoryEnumerateQuery
    {
        #region Internal-Members

        /// <summary>
        /// FROM clause, including the vessel join when the filter or sort needs the vessel name.
        /// </summary>
        internal string FromClause { get; set; } = string.Empty;

        /// <summary>
        /// WHERE clause (empty when nothing is filtered).
        /// </summary>
        internal string WhereClause { get; set; } = string.Empty;

        /// <summary>
        /// ORDER BY clause, always ending in a unique tie-breaker so pages are stable.
        /// </summary>
        internal string OrderByClause { get; set; } = string.Empty;

        /// <summary>
        /// Bound parameters.
        /// </summary>
        internal List<QueryParameter> Parameters { get; } = new List<QueryParameter>();

        #endregion
    }
}
