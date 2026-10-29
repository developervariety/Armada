namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one read path for per-attempt provenance. REST and MCP both call it, so a caller sees the attempts of a
    /// mission exactly when the caller-scoped mission read returns that mission.
    /// </summary>
    public static class MissionAttemptProvenanceReader
    {
        /// <summary>Attempts returned when the caller names no limit.</summary>
        public const int DefaultLimit = 100;

        /// <summary>Most attempts one read returns.</summary>
        public const int MaximumLimit = 1000;

        /// <summary>
        /// Read the attempts of a mission the caller may see.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="limit">Most attempts to return; clamped to 1..<see cref="MaximumLimit"/>.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The report, or null when the mission is absent or outside the caller's scope or credential.</returns>
        public static async Task<MissionAttemptProvenanceReport?> ReadAsync(
            DatabaseDriver database,
            AuthContext caller,
            string? missionId,
            int limit = DefaultLimit,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (caller == null) throw new ArgumentNullException(nameof(caller));

            // A captain's mission credential reads only the mission it was issued to.
            if (!String.IsNullOrWhiteSpace(caller.MissionId)
                && !String.Equals(caller.MissionId, missionId, StringComparison.Ordinal))
                return null;

            Mission? mission = await CallerScopedRead.ReadMissionAsync(database, caller, missionId, token).ConfigureAwait(false);
            if (mission == null) return null;
            int bounded = Math.Max(1, Math.Min(MaximumLimit, limit));
            ProductionFactPage<MissionAttemptProvenance> page =
                await database.MissionAttempts.EnumerateByMissionAsync(mission.TenantId, mission.Id, bounded, token).ConfigureAwait(false);
            return new MissionAttemptProvenanceReport
            {
                MissionId = mission.Id,
                Attempts = page.Items,
                Truncated = page.Truncated
            };
        }
    }
}
