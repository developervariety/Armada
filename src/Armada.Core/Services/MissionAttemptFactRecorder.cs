namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The single rule that turns a mission lifecycle transition into a durable attempt fact. Every
    /// call site that launches, retries, restarts, denies, fails, or lands a mission calls this
    /// recorder, so the chain identity and rescue marker are resolved in one place.
    /// </summary>
    public static class MissionAttemptFactRecorder
    {
        private const int _MaximumLineageDepth = 32;

        /// <summary>
        /// Record one fact. A recording failure is logged with its reason and never fails the
        /// lifecycle transition that called it; the production summary reports the missing fact
        /// as uncovered history.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="mission">Mission the fact describes.</param>
        /// <param name="factType">Fact type.</param>
        /// <param name="reasonCode">Optional bounded machine reason code.</param>
        /// <param name="logging">Optional logger.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored fact, or null when it could not be stored.</returns>
        public static async Task<MissionAttemptFact?> RecordAsync(
            DatabaseDriver database,
            Mission mission,
            MissionAttemptFactTypeEnum factType,
            string? reasonCode = null,
            LoggingModule? logging = null,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            try
            {
                LineageResult lineage = await ResolveLineageAsync(database, mission, token).ConfigureAwait(false);
                MissionAttemptFact fact = new MissionAttemptFact
                {
                    TenantId = mission.TenantId,
                    UserId = mission.UserId,
                    MissionId = mission.Id,
                    VoyageId = mission.VoyageId,
                    VesselId = mission.VesselId,
                    RootMissionId = lineage.RootMissionId,
                    ParentMissionId = lineage.ParentMissionId,
                    FactType = factType,
                    IsRescue = RescueMissionMarker.CarriesDescriptionMarker(mission.Description),
                    ReasonCode = reasonCode ?? lineage.ReasonCode,
                    CreatedUtc = DateTime.UtcNow
                };
                return await database.MissionAttemptFacts.CreateAsync(fact, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logging?.Warn("[MissionAttemptFactRecorder] could not record " + factType + " for mission " + mission.Id
                    + " (" + ex.GetType().Name + "): " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolve the original mission of a recovery chain. A rescue follows its parent; a chained
        /// rescue review stage that has no parent follows the rescue stage it depends on.
        /// </summary>
        internal static async Task<LineageResult> ResolveLineageAsync(DatabaseDriver database, Mission mission, CancellationToken token)
        {
            string? parentId = ParentOf(mission);
            if (parentId == null) return new LineageResult(mission.Id, null, null);

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal) { mission.Id };
            string rootId = parentId;
            Mission? current = mission;
            for (int depth = 0; depth < _MaximumLineageDepth; depth++)
            {
                string? next = ParentOf(current!);
                if (next == null) return new LineageResult(current!.Id, parentId, null);
                if (!seen.Add(next)) return new LineageResult(rootId, parentId, "lineage_cycle");
                rootId = next;
                current = await database.Missions.ReadAsync(next, token).ConfigureAwait(false);
                if (current == null) return new LineageResult(rootId, parentId, "lineage_parent_missing");
            }
            return new LineageResult(rootId, parentId, "lineage_depth_exceeded");
        }

        private static string? ParentOf(Mission mission)
        {
            if (!String.IsNullOrWhiteSpace(mission.ParentMissionId)) return mission.ParentMissionId;
            if (!String.IsNullOrWhiteSpace(mission.DependsOnMissionId)
                && RescueMissionMarker.CarriesDescriptionMarker(mission.Description))
                return mission.DependsOnMissionId;
            return null;
        }

        internal sealed class LineageResult
        {
            internal LineageResult(string rootMissionId, string? parentMissionId, string? reasonCode)
            {
                RootMissionId = rootMissionId;
                ParentMissionId = parentMissionId;
                ReasonCode = reasonCode;
            }

            internal string RootMissionId { get; }

            internal string? ParentMissionId { get; }

            internal string? ReasonCode { get; }
        }
    }
}
