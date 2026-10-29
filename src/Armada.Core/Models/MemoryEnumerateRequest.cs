namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Enumeration query for native memory records: the standard paging and range fields of
    /// <see cref="EnumerationQuery"/> plus the memory filters. <see cref="EnumerationQuery.VesselId"/> matches a
    /// record's vessel or its source vessel, and <see cref="EnumerationQuery.VoyageId"/> and
    /// <see cref="EnumerationQuery.MissionId"/> match its source voyage and mission. The order is fixed: salience
    /// descending, then newest first, then identifier.
    /// </summary>
    public class MemoryEnumerateRequest : EnumerationQuery
    {
        #region Public-Members

        /// <summary>
        /// Case-insensitive substring matched against content, summary, topic, key and tags.
        /// </summary>
        public string? Search { get; set; } = null;

        /// <summary>
        /// Memory type filter.
        /// </summary>
        public MemoryTypeEnum? Type { get; set; } = null;

        /// <summary>
        /// Exact topic filter, case-insensitive.
        /// </summary>
        public string? Topic { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public MemoryEnumerateRequest()
        {
        }

        /// <summary>
        /// Copy the standard fields of a query and add the memory filters.
        /// </summary>
        /// <param name="query">Source query; null yields defaults.</param>
        /// <param name="search">Free-text filter.</param>
        /// <param name="type">Type filter.</param>
        /// <param name="topic">Topic filter.</param>
        /// <returns>A memory enumeration request.</returns>
        public static MemoryEnumerateRequest From(EnumerationQuery? query, string? search, MemoryTypeEnum? type, string? topic)
        {
            MemoryEnumerateRequest request = new MemoryEnumerateRequest();
            if (query != null)
            {
                request.PageNumber = query.PageNumber;
                request.PageSize = query.RequestedPageSize ?? query.PageSize;
                request.Order = query.Order;
                request.CreatedAfter = query.CreatedAfter;
                request.CreatedBefore = query.CreatedBefore;
                request.Status = query.Status;
                request.FleetId = query.FleetId;
                request.VesselId = query.VesselId;
                request.CaptainId = query.CaptainId;
                request.VoyageId = query.VoyageId;
                request.MissionId = query.MissionId;
            }

            request.Search = search;
            request.Type = type;
            request.Topic = topic;
            return request;
        }

        #endregion
    }
}
