namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// The one active-work footprint query every database provider runs. It selects only the
    /// identifier columns of missions that occupy fleet capacity, so its cost follows active work,
    /// not retained mission and voyage history.
    /// </summary>
    internal static class ActiveWorkFootprintQuery
    {
        /// <summary>Tenant parameter name used when the query is tenant-scoped.</summary>
        internal const string TenantParameter = "@tenant_id";

        /// <summary>
        /// Build the provider-neutral SQL text. When <paramref name="tenantScoped"/> is true the
        /// command must bind <see cref="TenantParameter"/>.
        /// </summary>
        internal static string CommandText(bool tenantScoped)
        {
            string voyageStatuses = String.Join(",", ActiveWorkFootprint.ActiveVoyageStatuses.Select(status => "'" + status + "'"));
            string missionStatuses = String.Join(",", ActiveWorkFootprint.ActiveStandaloneMissionStatuses.Select(status => "'" + status + "'"));
            string sql =
                "SELECT m.id AS mission_id, m.voyage_id AS voyage_id, m.vessel_id AS vessel_id FROM missions m " +
                "LEFT JOIN voyages v ON v.id = m.voyage_id" + (tenantScoped ? " AND v.tenant_id = " + TenantParameter : String.Empty) + " " +
                "WHERE m.vessel_id IS NOT NULL AND m.vessel_id <> '' " +
                (tenantScoped ? "AND m.tenant_id = " + TenantParameter + " " : String.Empty) +
                "AND ((m.voyage_id IS NOT NULL AND m.voyage_id <> '' AND v.status IN (" + voyageStatuses + ")) " +
                "OR ((m.voyage_id IS NULL OR m.voyage_id = '') AND m.status IN (" + missionStatuses + ")));";
            return sql;
        }

        /// <summary>
        /// Read every footprint row the command returns.
        /// </summary>
        internal static async Task<List<ActiveWorkFootprint>> ReadAsync(DbDataReader reader, CancellationToken token)
        {
            List<ActiveWorkFootprint> results = new List<ActiveWorkFootprint>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string? voyageId = reader["voyage_id"] as string;
                results.Add(new ActiveWorkFootprint
                {
                    MissionId = reader["mission_id"] as string ?? "",
                    VoyageId = String.IsNullOrWhiteSpace(voyageId) ? null : voyageId,
                    VesselId = reader["vessel_id"] as string ?? ""
                });
            }
            return results;
        }
    }
}
