namespace Armada.Core.Models
{
    using System;
    using System.Globalization;
    using Armada.Core.Enums;

    /// <summary>
    /// Enumeration query for durable memories: every standard <see cref="EnumerationQuery"/> field plus the
    /// memory-specific filters and the sortable column. <see cref="EnumerationQuery.VesselId"/> matches either the
    /// memory's vessel or its source vessel; <see cref="EnumerationQuery.VoyageId"/> and
    /// <see cref="EnumerationQuery.MissionId"/> match the source voyage and mission.
    /// </summary>
    public class MemoryEnumerateRequest : EnumerationQuery
    {
        #region Public-Members

        /// <summary>
        /// Case-insensitive substring matched against content, topic, and tags.
        /// </summary>
        public string? Search { get; set; } = null;

        /// <summary>
        /// Memory type filter.
        /// </summary>
        public MemoryTypeEnum? Type { get; set; } = null;

        /// <summary>
        /// Exact topic filter (case-insensitive).
        /// </summary>
        public string? Topic { get; set; } = null;

        /// <summary>
        /// Case-insensitive substring of the topic.
        /// </summary>
        public string? TopicContains { get; set; } = null;

        /// <summary>
        /// Case-insensitive substring of the vessel name (the memory's vessel, else its source vessel).
        /// </summary>
        public string? VesselName { get; set; } = null;

        /// <summary>
        /// Case-insensitive substring of the summary, or of the content when the summary is empty.
        /// </summary>
        public string? SummaryContains { get; set; } = null;

        /// <summary>
        /// Visibility filter (TenantWide or UserSpecific).
        /// </summary>
        public ScopeEnum? Scope { get; set; } = null;

        /// <summary>
        /// Minimum salience (inclusive).
        /// </summary>
        public double? MinSalience { get; set; } = null;

        /// <summary>
        /// Column to sort by; the direction comes from <see cref="EnumerationQuery.Order"/>. Null sorts by created
        /// time. When neither SortBy nor Order is supplied, the REST and MCP surfaces use the historical relevance
        /// order (salience descending, then newest first); see <see cref="ApplyDefaultSort"/>.
        /// </summary>
        public MemorySortEnum? SortBy { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public MemoryEnumerateRequest()
        {
        }

        /// <summary>
        /// Copy the standard fields of an existing query.
        /// </summary>
        /// <param name="query">Source query; null yields defaults.</param>
        /// <returns>A memory enumeration request.</returns>
        public static MemoryEnumerateRequest FromQuery(EnumerationQuery? query)
        {
            MemoryEnumerateRequest request = new MemoryEnumerateRequest();
            if (query == null) return request;
            if (query is MemoryEnumerateRequest source)
            {
                request.Search = source.Search;
                request.Type = source.Type;
                request.Topic = source.Topic;
                request.TopicContains = source.TopicContains;
                request.VesselName = source.VesselName;
                request.SummaryContains = source.SummaryContains;
                request.Scope = source.Scope;
                request.MinSalience = source.MinSalience;
                request.SortBy = source.SortBy;
            }

            request.PageNumber = query.PageNumber;
            request.PageSize = query.PageSize;
            request.Order = query.Order;
            request.CreatedAfter = query.CreatedAfter;
            request.CreatedBefore = query.CreatedBefore;
            request.Status = query.Status;
            request.FleetId = query.FleetId;
            request.VesselId = query.VesselId;
            request.CaptainId = query.CaptainId;
            request.VoyageId = query.VoyageId;
            request.MissionId = query.MissionId;
            request.EventType = query.EventType;
            request.SignalType = query.SignalType;
            request.ToCaptainId = query.ToCaptainId;
            request.UnreadOnly = query.UnreadOnly;
            request.UserId = query.UserId;
            request.TenantId = query.TenantId;
            return request;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Apply the standard querystring overrides plus the memory-specific keys: search, type (memory type),
        /// topic, topicContains, vesselName, summaryContains, scope, minSalience, and sortBy. Unparseable enum and
        /// number values are ignored, like the standard keys.
        /// </summary>
        /// <param name="queryGetter">Function that retrieves a querystring value by key.</param>
        public void ApplyMemoryQuerystringOverrides(Func<string, string?> queryGetter)
        {
            if (queryGetter == null) throw new ArgumentNullException(nameof(queryGetter));

            ApplyQuerystringOverrides(queryGetter);

            // The shared parser maps "type" to EventType; for memories it is the memory type.
            EventType = null;

            string? val;

            val = queryGetter("search");
            if (!String.IsNullOrWhiteSpace(val)) Search = val.Trim();

            val = queryGetter("type");
            if (!String.IsNullOrEmpty(val) && Enum.TryParse(val, true, out MemoryTypeEnum type)) Type = type;

            val = queryGetter("topic");
            if (!String.IsNullOrWhiteSpace(val)) Topic = val.Trim();

            val = queryGetter("topicContains");
            if (!String.IsNullOrWhiteSpace(val)) TopicContains = val.Trim();

            val = queryGetter("vesselName");
            if (!String.IsNullOrWhiteSpace(val)) VesselName = val.Trim();

            val = queryGetter("summaryContains");
            if (!String.IsNullOrWhiteSpace(val)) SummaryContains = val.Trim();

            val = queryGetter("scope");
            if (!String.IsNullOrEmpty(val) && Enum.TryParse(val, true, out ScopeEnum scope)) Scope = scope;

            val = queryGetter("minSalience");
            if (!String.IsNullOrEmpty(val) && Double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double minSalience)) MinSalience = minSalience;

            val = queryGetter("sortBy");
            if (!String.IsNullOrEmpty(val) && Enum.TryParse(val, true, out MemorySortEnum sortBy)) SortBy = sortBy;
        }

        /// <summary>
        /// Select the historical relevance order (salience descending, then newest first) when the caller supplied
        /// neither <see cref="SortBy"/> nor an explicit <see cref="EnumerationQuery.Order"/>.
        /// </summary>
        /// <param name="orderSpecified">Whether the caller explicitly supplied an order.</param>
        public void ApplyDefaultSort(bool orderSpecified)
        {
            if (SortBy.HasValue || orderSpecified) return;
            SortBy = MemorySortEnum.Salience;
            Order = EnumerationOrderEnum.CreatedDescending;
        }

        #endregion
    }
}
