namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Builds the provider-neutral filter, visibility, and sort SQL for paged memory enumeration. Sort columns come
    /// only from the <see cref="MemorySortEnum"/> whitelist and every user-supplied value is bound as a parameter.
    /// Substring filters lower-case both sides and escape LIKE wildcards with '!', so results match across
    /// providers.
    /// </summary>
    internal static class MemoryEnumerateQueryBuilder
    {
        #region Internal-Members

        /// <summary>
        /// Columns selected for every memory row.
        /// </summary>
        internal static readonly string SelectColumns = "m.*";

        #endregion

        #region Private-Members

        private static readonly string _VesselJoin =
            " LEFT JOIN vessels v ON v.id = COALESCE(NULLIF(m.vessel_id, ''), m.source_vessel_id)";

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Build the query fragments for a memory enumeration.
        /// </summary>
        /// <param name="request">Filters, sort, and paging.</param>
        /// <param name="tenantId">Restrict to this tenant; null for every tenant.</param>
        /// <param name="ownerUserId">Restrict to memories owned by this user; null for every owner.</param>
        /// <param name="viewerUserId">When non-null (even empty), only tenant-wide memories and memories owned by this user are returned.</param>
        /// <returns>The query fragments and parameters.</returns>
        internal static MemoryEnumerateQuery Build(MemoryEnumerateRequest request, string? tenantId, string? ownerUserId, string? viewerUserId)
        {
            if (request == null) request = new MemoryEnumerateRequest();

            MemoryEnumerateQuery query = new MemoryEnumerateQuery();
            bool needsVessel = !String.IsNullOrWhiteSpace(request.VesselName) || request.SortBy == MemorySortEnum.Vessel;
            query.FromClause = " FROM memories m" + (needsVessel ? _VesselJoin : String.Empty);

            List<string> conditions = new List<string>();

            if (!String.IsNullOrEmpty(tenantId))
            {
                conditions.Add("m.tenant_id = @tenant_id");
                query.Parameters.Add(new QueryParameter("@tenant_id", tenantId));
            }

            if (!String.IsNullOrEmpty(ownerUserId))
            {
                conditions.Add("m.user_id = @owner_user_id");
                query.Parameters.Add(new QueryParameter("@owner_user_id", ownerUserId));
            }

            if (viewerUserId != null)
            {
                conditions.Add("(m.scope = @tenant_wide_scope OR m.user_id = @viewer_user_id)");
                query.Parameters.Add(new QueryParameter("@tenant_wide_scope", ScopeEnum.TenantWide.ToString()));
                query.Parameters.Add(new QueryParameter("@viewer_user_id", viewerUserId));
            }

            if (request.CreatedAfter.HasValue)
            {
                conditions.Add("m.created_utc > @created_after");
                query.Parameters.Add(new QueryParameter("@created_after", request.CreatedAfter.Value));
            }

            if (request.CreatedBefore.HasValue)
            {
                conditions.Add("m.created_utc < @created_before");
                query.Parameters.Add(new QueryParameter("@created_before", request.CreatedBefore.Value));
            }

            if (!String.IsNullOrWhiteSpace(request.VesselId))
            {
                conditions.Add("(m.vessel_id = @vessel_id OR m.source_vessel_id = @vessel_id)");
                query.Parameters.Add(new QueryParameter("@vessel_id", request.VesselId!.Trim()));
            }

            if (!String.IsNullOrWhiteSpace(request.VoyageId))
            {
                conditions.Add("m.source_voyage_id = @voyage_id");
                query.Parameters.Add(new QueryParameter("@voyage_id", request.VoyageId!.Trim()));
            }

            if (!String.IsNullOrWhiteSpace(request.MissionId))
            {
                conditions.Add("m.source_mission_id = @mission_id");
                query.Parameters.Add(new QueryParameter("@mission_id", request.MissionId!.Trim()));
            }

            if (request.Type.HasValue)
            {
                conditions.Add("m.type = @type");
                query.Parameters.Add(new QueryParameter("@type", request.Type.Value.ToString()));
            }

            if (request.Scope.HasValue)
            {
                conditions.Add("m.scope = @scope");
                query.Parameters.Add(new QueryParameter("@scope", request.Scope.Value.ToString()));
            }

            if (!String.IsNullOrWhiteSpace(request.Topic))
            {
                conditions.Add("LOWER(m.topic) = @topic");
                query.Parameters.Add(new QueryParameter("@topic", request.Topic!.Trim().ToLowerInvariant()));
            }

            if (!String.IsNullOrWhiteSpace(request.TopicContains))
            {
                conditions.Add("LOWER(m.topic) LIKE @topic_contains ESCAPE '!'");
                query.Parameters.Add(new QueryParameter("@topic_contains", VesselHealthQueryBuilder.ContainsPattern(request.TopicContains!)));
            }

            if (!String.IsNullOrWhiteSpace(request.VesselName))
            {
                conditions.Add("LOWER(v.name) LIKE @vessel_name ESCAPE '!'");
                query.Parameters.Add(new QueryParameter("@vessel_name", VesselHealthQueryBuilder.ContainsPattern(request.VesselName!)));
            }

            if (!String.IsNullOrWhiteSpace(request.SummaryContains))
            {
                conditions.Add("LOWER(COALESCE(NULLIF(m.summary, ''), m.content)) LIKE @summary_contains ESCAPE '!'");
                query.Parameters.Add(new QueryParameter("@summary_contains", VesselHealthQueryBuilder.ContainsPattern(request.SummaryContains!)));
            }

            if (request.MinSalience.HasValue)
            {
                conditions.Add("m.salience >= @min_salience");
                query.Parameters.Add(new QueryParameter("@min_salience", request.MinSalience.Value));
            }

            if (!String.IsNullOrWhiteSpace(request.Search))
            {
                conditions.Add("(LOWER(m.content) LIKE @search ESCAPE '!' OR LOWER(m.topic) LIKE @search ESCAPE '!'"
                    + " OR EXISTS (SELECT 1 FROM memory_tags mt WHERE mt.memory_id = m.id AND LOWER(mt.tag) LIKE @search ESCAPE '!'))");
                query.Parameters.Add(new QueryParameter("@search", VesselHealthQueryBuilder.ContainsPattern(request.Search!)));
            }

            query.WhereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;
            query.OrderByClause = BuildOrderBy(request.SortBy, request.Order);
            return query;
        }

        #endregion

        #region Private-Methods

        private static string BuildOrderBy(MemorySortEnum? sortBy, EnumerationOrderEnum order)
        {
            string direction = order == EnumerationOrderEnum.CreatedAscending ? " ASC" : " DESC";
            string? expression = null;

            switch (sortBy)
            {
                case MemorySortEnum.Updated:
                    expression = "m.last_update_utc";
                    break;
                case MemorySortEnum.Salience:
                    expression = "m.salience";
                    break;
                case MemorySortEnum.Vessel:
                    expression = "LOWER(COALESCE(v.name, ''))";
                    break;
                case MemorySortEnum.Type:
                    expression = "m.type";
                    break;
                case MemorySortEnum.Topic:
                    expression = "LOWER(COALESCE(m.topic, ''))";
                    break;
                case MemorySortEnum.Summary:
                    expression = "LOWER(COALESCE(NULLIF(m.summary, ''), m.content))";
                    break;
                case MemorySortEnum.Visibility:
                    expression = "m.scope";
                    break;
                default:
                    expression = null;
                    break;
            }

            string orderBy = " ORDER BY ";
            if (expression != null) orderBy += expression + direction + ", ";
            orderBy += "m.created_utc" + direction + ", m.id" + direction;
            return orderBy;
        }

        #endregion
    }
}
