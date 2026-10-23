namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Paginated result wrapper for enumeration queries.
    /// </summary>
    /// <typeparam name="T">Entity type.</typeparam>
    public class EnumerationResult<T>
    {
        #region Public-Members

        /// <summary>
        /// Whether the operation succeeded.
        /// </summary>
        public bool Success { get; set; } = true;

        /// <summary>
        /// Current page number (1-based).
        /// </summary>
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// Page size (number of items per page).
        /// </summary>
        public int PageSize { get; set; } = 100;

        /// <summary>
        /// Total number of pages.
        /// </summary>
        public int TotalPages { get; set; }

        /// <summary>
        /// Total number of records matching the query.
        /// </summary>
        public long TotalRecords { get; set; }

        /// <summary>
        /// The result objects for this page.
        /// </summary>
        public List<T> Objects { get; set; } = new List<T>();

        /// <summary>
        /// Query execution time in milliseconds.
        /// </summary>
        public double TotalMs { get; set; }

        /// <summary>
        /// Page size the caller asked for, present only when it exceeded <see cref="MaxPageSize"/> and the
        /// server served <see cref="PageSize"/> instead.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RequestedPageSize { get; set; } = null;

        /// <summary>
        /// Largest page size this list serves, present only when <see cref="RequestedPageSize"/> is.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxPageSize { get; set; } = null;

        /// <summary>
        /// Default largest page size of an in-memory list page built by <see cref="Page"/>.
        /// </summary>
        public const int DefaultMaxPageSize = 500;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public EnumerationResult()
        {
        }

        /// <summary>
        /// Create an EnumerationResult from query parameters, data, and total count.
        /// </summary>
        /// <param name="query">The enumeration query.</param>
        /// <param name="data">Result objects for this page.</param>
        /// <param name="totalCount">Total number of matching records.</param>
        /// <returns>Populated EnumerationResult.</returns>
        public static EnumerationResult<T> Create(EnumerationQuery query, List<T> data, long totalCount)
        {
            EnumerationResult<T> result = new EnumerationResult<T>();
            result.PageNumber = query.PageNumber;
            result.PageSize = query.PageSize;
            result.TotalRecords = totalCount;
            result.TotalPages = query.PageSize > 0
                ? (int)Math.Ceiling((double)totalCount / query.PageSize)
                : 0;
            result.Objects = data ?? new List<T>();
            if (query.RequestedPageSize.HasValue) result.ReportPageSizeLimit(query.RequestedPageSize.Value, EnumerationQuery.MaxPageSize);
            return result;
        }

        /// <summary>
        /// Build one page over an already filtered and ordered list. A page size below 1 reads as the default; a
        /// page size above the maximum is served at the maximum and reported through
        /// <see cref="RequestedPageSize"/> and <see cref="MaxPageSize"/>.
        /// </summary>
        /// <param name="ordered">Filtered, ordered records.</param>
        /// <param name="pageNumber">Requested one-based page number.</param>
        /// <param name="pageSize">Requested page size.</param>
        /// <param name="defaultPageSize">Page size used when the request gives none.</param>
        /// <param name="maxPageSize">Largest page size served.</param>
        /// <returns>The page.</returns>
        public static EnumerationResult<T> Page(List<T> ordered, int pageNumber, int pageSize, int defaultPageSize = 50, int maxPageSize = DefaultMaxPageSize)
        {
            if (ordered == null) throw new ArgumentNullException(nameof(ordered));
            int effectiveSize = pageSize < 1 ? defaultPageSize : Math.Min(pageSize, maxPageSize);
            int effectiveNumber = pageNumber < 1 ? 1 : pageNumber;
            int offset = (effectiveNumber - 1) * effectiveSize;
            EnumerationResult<T> result = new EnumerationResult<T>
            {
                PageNumber = effectiveNumber,
                PageSize = effectiveSize,
                TotalRecords = ordered.Count,
                TotalPages = effectiveSize > 0 ? (int)Math.Ceiling((double)ordered.Count / effectiveSize) : 0,
                Objects = ordered.Skip(offset).Take(effectiveSize).ToList(),
                TotalMs = 0
            };
            result.ReportPageSizeLimit(pageSize, maxPageSize);
            return result;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record that the caller asked for more than <paramref name="maxPageSize"/> rows per page; does nothing
        /// when the request was within the limit.
        /// </summary>
        /// <param name="requestedPageSize">Page size the caller asked for.</param>
        /// <param name="maxPageSize">Largest page size served.</param>
        public void ReportPageSizeLimit(int requestedPageSize, int maxPageSize)
        {
            if (requestedPageSize <= maxPageSize) return;
            RequestedPageSize = requestedPageSize;
            MaxPageSize = maxPageSize;
        }

        #endregion
    }
}
