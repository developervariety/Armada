namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Read-only service that detects auto-rescue mission landings suspected of being false positives --
    /// rescues that reconciled to Complete without performing real corrective work.
    /// No mutations are performed; callers decide any remediation.
    /// </summary>
    public sealed class RescueLandingBackfillDetector
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private string _Header = "[RescueLandingBackfillDetector] ";

        private const string _RescueMarker = "<!-- ARMADA:AUTO-RESCUE -->";

        private static readonly string[] _ReviewerPersonas =
        {
            "judge",
            "testengineer",
            "usabilityengineer",
            "diagnosticprotocolreviewer",
            "tenantsecurityreviewer",
            "migrationdatareviewer",
            "performancememoryreviewer",
            "portingreferenceanalyst",
            "frontendworkflowreviewer"
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver for data access.</param>
        /// <param name="logging">Logging module.</param>
        public RescueLandingBackfillDetector(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Enumerate Complete rescue missions and return those that exhibit one or more
        /// false-positive indicators. Strictly read-only; no status mutations are made.
        /// </summary>
        /// <param name="vesselId">Optional vessel identifier to restrict detection to a single vessel.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of suspect rescue landings with per-record reason codes.</returns>
        public async Task<List<SuspectRescueLanding>> DetectAsync(string? vesselId = null, CancellationToken token = default)
        {
            _Logging.Debug(_Header + "starting backfill detection"
                + (!String.IsNullOrWhiteSpace(vesselId) ? " for vessel " + vesselId : " across all vessels"));

            List<MissionSummary> completeSummaries = await GetCompleteSummariesAsync(vesselId, token).ConfigureAwait(false);

            // First pass: filter to summaries that have a parent (rescue missions always have one)
            List<MissionSummary> parentedSummaries = new List<MissionSummary>();
            foreach (MissionSummary summary in completeSummaries)
            {
                if (!String.IsNullOrWhiteSpace(summary.ParentMissionId))
                    parentedSummaries.Add(summary);
            }

            if (parentedSummaries.Count == 0)
            {
                _Logging.Debug(_Header + "no parented Complete missions found, detection complete");
                return new List<SuspectRescueLanding>();
            }

            // Load merge entries once and index by MissionId to avoid N+1 queries
            List<MergeEntry> allMergeEntries = await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
            Dictionary<string, List<MergeEntry>> mergesByMissionId = BuildMergeEntryLookup(allMergeEntries);

            // Cache of parent mission CommitHash values to avoid re-reading the same parent twice
            Dictionary<string, string?> parentCommitHashCache = new Dictionary<string, string?>(StringComparer.Ordinal);

            List<SuspectRescueLanding> suspects = new List<SuspectRescueLanding>();

            foreach (MissionSummary summary in parentedSummaries)
            {
                bool isRescue = summary.Title.StartsWith("Rescue ", StringComparison.Ordinal);
                Mission? full = null;

                if (!isRescue)
                {
                    // Title alone does not confirm rescue; hydrate to check description marker
                    full = await _Database.Missions.ReadAsync(summary.Id, token).ConfigureAwait(false);
                    if (full == null) continue;
                    isRescue = !String.IsNullOrEmpty(full.Description)
                        && full.Description.Contains(_RescueMarker, StringComparison.Ordinal);
                }

                if (!isRescue) continue;

                // Hydrate full row if not already loaded (when title matched, we skipped hydration above)
                if (full == null)
                {
                    full = await _Database.Missions.ReadAsync(summary.Id, token).ConfigureAwait(false);
                    if (full == null) continue;
                }

                List<string> reasons = BuildBaseReasons(full, mergesByMissionId);

                // (d) commit_hash_equals_parent: CommitHash non-empty AND equals the parent's CommitHash
                if (!String.IsNullOrWhiteSpace(full.CommitHash) && !String.IsNullOrWhiteSpace(full.ParentMissionId))
                {
                    string? parentHash = await GetParentCommitHashAsync(
                        full.ParentMissionId!, parentCommitHashCache, token).ConfigureAwait(false);
                    if (!String.IsNullOrWhiteSpace(parentHash)
                        && String.Equals(full.CommitHash, parentHash, StringComparison.Ordinal))
                    {
                        reasons.Add("commit_hash_equals_parent");
                    }
                }

                if (reasons.Count == 0) continue;

                SuspectRescueLanding suspect = new SuspectRescueLanding();
                suspect.MissionId = full.Id;
                suspect.ParentMissionId = full.ParentMissionId;
                suspect.VesselId = full.VesselId;
                suspect.Persona = full.Persona;
                suspect.CommitHash = full.CommitHash;
                suspect.CompletedUtc = full.CompletedUtc;
                suspect.Reasons = reasons;
                suspects.Add(suspect);
            }

            _Logging.Debug(_Header + "detection complete, found " + suspects.Count + " suspect(s)");
            return suspects;
        }

        #endregion

        #region Private-Methods

        private async Task<List<MissionSummary>> GetCompleteSummariesAsync(string? vesselId, CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(vesselId))
            {
                List<MissionSummary> vesselSummaries = await _Database.Missions
                    .EnumerateMissionSummariesByVesselAsync(vesselId, token).ConfigureAwait(false);

                List<MissionSummary> completed = new List<MissionSummary>();
                foreach (MissionSummary s in vesselSummaries)
                {
                    if (s.Status == MissionStatusEnum.Complete)
                        completed.Add(s);
                }
                return completed;
            }

            // Page through all Complete missions to avoid hydrating the full table at once
            List<MissionSummary> all = new List<MissionSummary>();
            int pageNumber = 1;
            const int pageSize = 1000;
            while (true)
            {
                EnumerationResult<MissionSummary> page = await _Database.Missions.EnumerateMissionSummariesAsync(
                    new EnumerationQuery
                    {
                        Status = MissionStatusEnum.Complete.ToString(),
                        PageNumber = pageNumber,
                        PageSize = pageSize,
                        Order = EnumerationOrderEnum.CreatedDescending
                    }, token).ConfigureAwait(false);

                all.AddRange(page.Objects);
                if (page.Objects.Count < pageSize) break;
                pageNumber++;
            }

            return all;
        }

        private static Dictionary<string, List<MergeEntry>> BuildMergeEntryLookup(List<MergeEntry> entries)
        {
            Dictionary<string, List<MergeEntry>> lookup =
                new Dictionary<string, List<MergeEntry>>(StringComparer.Ordinal);

            foreach (MergeEntry entry in entries)
            {
                if (String.IsNullOrEmpty(entry.MissionId)) continue;

                if (!lookup.TryGetValue(entry.MissionId, out List<MergeEntry>? bucket))
                {
                    bucket = new List<MergeEntry>();
                    lookup[entry.MissionId] = bucket;
                }
                bucket.Add(entry);
            }

            return lookup;
        }

        private static List<string> BuildBaseReasons(Mission mission, Dictionary<string, List<MergeEntry>> mergesByMissionId)
        {
            List<string> reasons = new List<string>();

            // (a) Reviewer persona: a reviewer-type mission should not have been dispatched as a rescue
            if (IsReviewerPersona(mission.Persona))
                reasons.Add("reviewer_persona_rescue_completed");

            // (b) Empty commit hash: rescue completed but left no git commit evidence
            if (String.IsNullOrWhiteSpace(mission.CommitHash))
                reasons.Add("empty_commit_hash");

            // (c) Noop merge entry: Landed with zero diff lines -- no actual changes were merged
            if (mergesByMissionId.TryGetValue(mission.Id, out List<MergeEntry>? missionMerges))
            {
                foreach (MergeEntry entry in missionMerges)
                {
                    if (entry.Status == MergeStatusEnum.Landed && entry.DiffLineCount == 0)
                    {
                        reasons.Add("noop_merge_entry");
                        break;
                    }
                }
            }

            return reasons;
        }

        private static bool IsReviewerPersona(string? persona)
        {
            if (String.IsNullOrWhiteSpace(persona)) return false;

            string normalized = persona.Trim().ToLowerInvariant().Replace(" ", "");

            foreach (string reviewer in _ReviewerPersonas)
            {
                if (String.Equals(reviewer, normalized, StringComparison.Ordinal))
                    return true;
            }

            return normalized.EndsWith("reviewer", StringComparison.Ordinal)
                || normalized.EndsWith("analyst", StringComparison.Ordinal);
        }

        private async Task<string?> GetParentCommitHashAsync(
            string parentMissionId,
            Dictionary<string, string?> cache,
            CancellationToken token)
        {
            if (cache.TryGetValue(parentMissionId, out string? cached))
                return cached;

            Mission? parent = await _Database.Missions.ReadAsync(parentMissionId, token).ConfigureAwait(false);
            string? hash = parent?.CommitHash;
            cache[parentMissionId] = hash;
            return hash;
        }

        #endregion
    }
}
