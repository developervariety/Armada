namespace Armada.Core.Services
{
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Builds a bounded, evidence-based production summary from durable Armada records.
    /// </summary>
    public sealed class VerifiedProductionSummaryService
    {
        private const int _PageSize = 500;
        private const int _RecordLimit = 100000;
        private static readonly TimeSpan _DefaultWindow = TimeSpan.FromDays(7);
        private static readonly TimeSpan _MaximumWindow = TimeSpan.FromDays(90);
        private static readonly JsonSerializerOptions _IncidentJson = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        private readonly DatabaseDriver _Database;

        /// <summary>Instantiate.</summary>
        public VerifiedProductionSummaryService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>Build one tenant-scoped production summary.</summary>
        public async Task<ProductionSummaryResult> SummarizeAsync(
            AuthContext auth,
            ProductionSummaryQuery query,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (!auth.IsAuthenticated) throw new UnauthorizedAccessException("Authentication is required.");
            if (!auth.IsAdmin && (String.IsNullOrWhiteSpace(auth.TenantId)
                || (!auth.IsTenantAdmin && String.IsNullOrWhiteSpace(auth.UserId))))
                throw new UnauthorizedAccessException("A complete tenant scope is required.");

            DateTime toUtc = (query.ToUtc ?? DateTime.UtcNow.Date).ToUniversalTime();
            DateTime fromUtc = (query.FromUtc ?? toUtc.Subtract(_DefaultWindow)).ToUniversalTime();
            if (fromUtc >= toUtc) throw new ArgumentException("fromUtc must be earlier than toUtc.", nameof(query));
            if (toUtc - fromUtc > _MaximumWindow) throw new ArgumentException("The production summary window cannot exceed 90 days.", nameof(query));

            ProductionSummaryResult result = new ProductionSummaryResult
            {
                FromUtc = fromUtc,
                ToUtc = toUtc,
                GeneratedUtc = DateTime.UtcNow,
                Scan = new ProductionSummaryScan { RecordLimit = _RecordLimit }
            };

            List<Objective> objectives = await ReadObjectivesAsync(auth, result, token).ConfigureAwait(false);
            HashSet<string> parentIds = objectives
                .Where(item => !String.IsNullOrWhiteSpace(item.ParentObjectiveId))
                .Select(item => item.ParentObjectiveId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<Objective> cohort = objectives
                .Where(item => item.Status == ObjectiveStatusEnum.Completed
                    && item.CompletedUtc.HasValue
                    && item.CompletedUtc.Value >= fromUtc
                    && item.CompletedUtc.Value < toUtc
                    && !parentIds.Contains(item.Id))
                .ToList();
            result.RawCompletedSlices = cohort.Count(item => MatchesFilter(item, query));
            for (DateTime day = fromUtc.Date; day < toUtc; day = day.AddDays(1))
            {
                DateTime next = day.AddDays(1);
                if (day < fromUtc || next > toUtc) continue;
                result.RawByDay.Add(new ProductionDailyCount
                {
                    DayUtc = DateTime.SpecifyKind(day, DateTimeKind.Utc),
                    Count = cohort.Count(item => MatchesFilter(item, query)
                        && item.CompletedUtc >= day && item.CompletedUtc < next)
                });
            }
            result.CompleteDayCount = result.RawByDay.Count;
            result.RawCompletedSlicesPerDay = result.CompleteDayCount > 0
                ? (double)result.RawCompletedSlices / result.CompleteDayCount
                : null;

            List<MissionSummary> missions = await ReadMissionSummariesAsync(auth, result, token).ConfigureAwait(false);
            List<Voyage> voyages = await ReadVoyagesAsync(auth, result, token).ConfigureAwait(false);
            List<MergeEntry> merges = await ReadMergeEntriesAsync(auth, result, token).ConfigureAwait(false);
            List<CheckRun> checks = await ReadChecksAsync(auth, result, token).ConfigureAwait(false);
            List<ArmadaEvent> events = await ReadEventsAsync(auth, fromUtc.Subtract(_MaximumWindow), toUtc, result, token).ConfigureAwait(false);
            List<Incident> incidents = await ReadIncidentsAsync(auth, result, token).ConfigureAwait(false);
            Dictionary<string, RegressionSliceTarget> regressionTargets = new Dictionary<string, RegressionSliceTarget>(StringComparer.OrdinalIgnoreCase);
            AttemptFactIndex attemptFacts = new AttemptFactIndex(await ReadAttemptFactsAsync(auth, fromUtc.Subtract(_MaximumWindow), toUtc, result, token).ConfigureAwait(false));
            bool verificationSourcesComplete = !result.Scan.Truncated;

            Dictionary<string, MissionSummary> missionById = missions.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Voyage> voyageById = voyages.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<MergeEntry>> mergesByMission = merges
                .Where(item => !String.IsNullOrWhiteSpace(item.MissionId))
                .GroupBy(item => item.MissionId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<CheckRun>> checksByMission = checks
                .Where(item => !String.IsNullOrWhiteSpace(item.MissionId))
                .GroupBy(item => item.MissionId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<CheckRun>> checksByVoyage = checks
                .Where(item => !String.IsNullOrWhiteSpace(item.VoyageId))
                .GroupBy(item => item.VoyageId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<(string SourceFamily, string WorkType, string Category, string Kind), Objective> grouping in cohort
                .Where(item => MatchesFilter(item, query))
                .GroupBy(item => (SourceFamily(item), WorkType(item), Category(item), item.Kind.ToString())))
            {
                ProductionSummaryGroup group = new ProductionSummaryGroup
                {
                    SourceFamily = grouping.Key.SourceFamily,
                    WorkType = grouping.Key.WorkType,
                    Category = grouping.Key.Category,
                    Kind = grouping.Key.Kind
                };
                List<long> readyDelays = new List<long>();
                List<long> closeoutDelays = new List<long>();
                List<long> armedDelays = new List<long>();
                List<long> executionDurations = new List<long>();
                HashSet<string> timedCheckIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> runtimeMissionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<DateTime, int> daily = new Dictionary<DateTime, int>();

                foreach (Objective objective in grouping)
                {
                    SliceEvidence evidence = verificationSourcesComplete
                        ? EvaluateSlice(objective, missionById, voyageById, mergesByMission, checksByMission, checksByVoyage)
                        : SliceEvidence.Failed("incomplete_source_scan");
                    regressionTargets[objective.Id] = new RegressionSliceTarget(group, evidence);
                    if (!evidence.Verified)
                    {
                        group.VerifiedLandedSlices.Unknown++;
                        Increment(result.ExclusionsByReason, evidence.Reason);
                    }
                    else
                    {
                        group.VerifiedLandedSlices.Count++;
                        DateTime day = objective.CompletedUtc!.Value.Date;
                        daily[day] = daily.GetValueOrDefault(day) + 1;
                        if (evidence.LastLandingUtc.HasValue && objective.CompletedUtc.Value >= evidence.LastLandingUtc.Value)
                            closeoutDelays.Add((long)(objective.CompletedUtc.Value - evidence.LastLandingUtc.Value).TotalMilliseconds);
                    }

                    List<MissionSummary> sliceMissions = ResolveMissions(objective, missions);
                    List<MissionSummary> chainMissions = attemptFacts.ResolveChain(sliceMissions, missionById);
                    AttemptChainClassification chain = attemptFacts.Classify(chainMissions);
                    group.RescueRuntime.CompletedSlices++;
                    if (chain.Rescued) group.RescueRuntime.RescuedSlices++;
                    else if (chain.HistoricalRuns > 0) group.RescueRuntime.RescueUnknownSlices++;
                    if (evidence.Verified)
                    {
                        if (chain.HistoricalRuns > 0)
                        {
                            group.FirstPassAcceptance.Unknown++;
                            Increment(group.FirstPassAcceptance.UnknownByReason, "attempt_facts_not_recorded");
                        }
                        else
                        {
                            group.FirstPassAcceptance.Eligible++;
                            if (!chain.FirstPassDisqualified) group.FirstPassAcceptance.Accepted++;
                        }
                    }
                    foreach (MissionSummary mission in chainMissions)
                    {
                        if (!runtimeMissionIds.Add(mission.Id)) continue;
                        if (!attemptFacts.HasAttempt(mission.Id))
                        {
                            if (!AttemptFactIndex.Ran(mission)) continue;
                            group.RescueRuntime.HistoricalUnclassifiedMissionCount++;
                            group.RescueRuntime.HistoricalUnclassifiedMs += mission.TotalRuntimeMs ?? 0;
                            continue;
                        }
                        group.RescueRuntime.ClassifiedMissionCount++;
                        bool isRescue = attemptFacts.IsRescue(mission.Id);
                        if (isRescue) group.RescueRuntime.RescueMissionCount++;
                        if (mission.TotalRuntimeMs.HasValue)
                        {
                            group.RescueRuntime.TotalMissionMs += mission.TotalRuntimeMs.Value;
                            if (isRescue) group.RescueRuntime.RescueMs += mission.TotalRuntimeMs.Value;
                        }
                        else group.RescueRuntime.UnknownMissionCount++;
                    }

                    List<CheckRun> sliceChecks = ResolveChecks(objective, checks, sliceMissions);
                    foreach (CheckRun check in sliceChecks)
                    {
                        if (!timedCheckIds.Add(check.Id)) continue;
                        if (check.StartedUtc.HasValue && check.StartedUtc.Value >= check.CreatedUtc)
                            armedDelays.Add((long)(check.StartedUtc.Value - check.CreatedUtc).TotalMilliseconds);
                        if (check.DurationMs.HasValue && check.DurationMs.Value >= 0)
                            executionDurations.Add(check.DurationMs.Value);
                    }

                    DateTime? dispatchUtc = objective.VoyageIds
                        .Select(id => voyageById.GetValueOrDefault(id)?.CreatedUtc)
                        .Where(value => value.HasValue)
                        .OrderBy(value => value)
                        .FirstOrDefault();
                    DateTime? readyUtc = FindReadyUtc(objective.Id, dispatchUtc, events);
                    if (readyUtc.HasValue && dispatchUtc.HasValue && dispatchUtc.Value >= readyUtc.Value)
                        readyDelays.Add((long)(dispatchUtc.Value - readyUtc.Value).TotalMilliseconds);
                }

                group.VerifiedLandedSlices.Availability = group.VerifiedLandedSlices.Unknown == 0 ? "available" : "partial";
                for (DateTime day = fromUtc.Date; day < toUtc; day = day.AddDays(1))
                {
                    DateTime next = day.AddDays(1);
                    if (day < fromUtc || next > toUtc) continue;
                    group.ByDay.Add(new ProductionDailyCount
                    {
                        DayUtc = DateTime.SpecifyKind(day, DateTimeKind.Utc),
                        Count = daily.GetValueOrDefault(day)
                    });
                }
                group.ReadyToDispatchDelayMs = Distribution(readyDelays, grouping.Count() - readyDelays.Count, "partial");
                group.LandedToVerifiedCloseoutMs = Distribution(closeoutDelays, group.VerifiedLandedSlices.Count - closeoutDelays.Count, null);
                int totalChecks = timedCheckIds.Count;
                group.CheckTiming.ArmedToStartMs = Distribution(armedDelays, Math.Max(0, totalChecks - armedDelays.Count), null);
                group.CheckTiming.HostQueueMs = ProductionDistributionMetric.Unavailable();
                group.CheckTiming.HostQueueMs.Unknown = totalChecks;
                group.CheckTiming.ExecutionMs = Distribution(executionDurations, Math.Max(0, totalChecks - executionDurations.Count), null);
                group.RescueRuntime.Share = group.RescueRuntime.TotalMissionMs > 0
                    ? (double)group.RescueRuntime.RescueMs / group.RescueRuntime.TotalMissionMs
                    : null;
                FinishRescueRuntime(group.RescueRuntime);
                FinishRate(group.FirstPassAcceptance);
                group.RepeatedResearch.Unknown = group.VerifiedLandedSlices.Count;
                result.Groups.Add(group);
            }

            AttributeRegressions(result, regressionTargets, incidents, checks, fromUtc, toUtc);
            int verified = result.Groups.Sum(group => group.VerifiedLandedSlices.Count);
            result.VerifiedLandedSlicesPerDay = result.CompleteDayCount > 0
                ? (double)verified / result.CompleteDayCount
                : null;

            AddAvailabilityWarnings(result);
            ProductionRegressionCoverage regressionCoverage = result.RegressionCoverage;
            if (regressionCoverage.Unknown + regressionCoverage.Unlinked + regressionCoverage.UnreadableRecords > 0)
                result.Warnings.Add("post_land_regressions_partial: " + regressionCoverage.Unknown + " unattributed, "
                    + regressionCoverage.Unlinked + " unlinked, and " + regressionCoverage.UnreadableRecords + " unreadable regression record(s)");
            int firstPassUnknown = result.Groups.Sum(item => item.FirstPassAcceptance.Unknown);
            if (firstPassUnknown > 0)
                result.Warnings.Add("first_pass_acceptance_partial: " + firstPassUnknown + " verified slice(s) ran before attempt facts were recorded");
            int historicalMissions = result.Groups.Sum(item => item.RescueRuntime.HistoricalUnclassifiedMissionCount);
            if (historicalMissions > 0)
                result.Warnings.Add("rescue_classification_partial: " + historicalMissions + " mission(s) ran before attempt facts were recorded and are excluded from rescue share");
            return result;
        }

        private static SliceEvidence EvaluateSlice(
            Objective objective,
            Dictionary<string, MissionSummary> missionById,
            Dictionary<string, Voyage> voyageById,
            Dictionary<string, List<MergeEntry>> mergesByMission,
            Dictionary<string, List<CheckRun>> checksByMission,
            Dictionary<string, List<CheckRun>> checksByVoyage)
        {
            List<MissionSummary> linked = objective.MissionIds
                .Select(id => missionById.GetValueOrDefault(id))
                .Where(item => item != null)
                .Cast<MissionSummary>()
                .Concat(missionById.Values.Where(item => item.VoyageId != null && objective.VoyageIds.Contains(item.VoyageId, StringComparer.OrdinalIgnoreCase)))
                .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (objective.VoyageIds.Count == 0) return SliceEvidence.Failed("missing_voyage_link");
            if (objective.MissionIds.Any(id => !missionById.ContainsKey(id)))
                return SliceEvidence.Failed("missing_linked_mission");
            // Final rows do not preserve enough chain history to prove that every independent
            // chain of a failed voyage was rescued. Stay conservative instead of repeating the
            // former objective-reconciliation bug that let one rescue satisfy a multi-chain voyage.
            if (objective.VoyageIds.Any(id => !voyageById.TryGetValue(id, out Voyage? voyage) || voyage.Status != VoyageStatusEnum.Complete))
                return SliceEvidence.Failed("non_complete_voyage_requires_chain_evidence");
            if (linked.Any(item => item.Status != MissionStatusEnum.Complete))
                return SliceEvidence.Failed("mission_chain_not_complete");
            List<MissionSummary> implementation = linked.Where(item => item.Status == MissionStatusEnum.Complete && !String.IsNullOrWhiteSpace(item.CommitHash)).ToList();
            if (implementation.Count == 0) return SliceEvidence.Failed("missing_completed_implementation");

            HashSet<string> supersededImplementationIds = implementation
                .SelectMany(item => new[] { item.DependsOnMissionId, item.ParentMissionId })
                .Where(id => !String.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<MissionSummary> deliveryTips = implementation
                .Where(item => !supersededImplementationIds.Contains(item.Id))
                .ToList();
            if (deliveryTips.Count == 0) return SliceEvidence.Failed("invalid_mission_chain");

            List<DateTime> landingTimes = new List<DateTime>();
            foreach (MissionSummary tip in deliveryTips)
            {
                List<MergeEntry> tipLandings = (mergesByMission.GetValueOrDefault(tip.Id) ?? new List<MergeEntry>())
                    .Where(item => item.Status == MergeStatusEnum.Landed && item.CompletedUtc.HasValue)
                    .ToList();
                if (tipLandings.Count == 0) return SliceEvidence.Failed("missing_landing_evidence");
                landingTimes.Add(tipLandings.Max(item => item.CompletedUtc!.Value));

                List<CheckRun> tipChecks = new List<CheckRun>();
                tipChecks.AddRange(checksByMission.GetValueOrDefault(tip.Id) ?? new List<CheckRun>());
                if (!String.IsNullOrWhiteSpace(tip.VoyageId))
                    tipChecks.AddRange(checksByVoyage.GetValueOrDefault(tip.VoyageId) ?? new List<CheckRun>());
                tipChecks = tipChecks
                    .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .Where(CheckRunGateRules.ParticipatesInRealSignalGate)
                    .Where(item => CheckRunGateRules.SameCommit(item.CommitHash, tip.CommitHash))
                    .ToList();
                if (tipChecks.Count == 0) return SliceEvidence.Failed("missing_check_evidence");
                if (tipChecks.Any(item => item.Status != CheckRunStatusEnum.Passed))
                    return SliceEvidence.Failed("check_not_passed");
            }

            return new SliceEvidence(true, String.Empty, landingTimes.Max(), deliveryTips.Select(item => item.CommitHash!).ToList());
        }

        private static List<MissionSummary> ResolveMissions(Objective objective, List<MissionSummary> missions)
        {
            return missions.Where(item => objective.MissionIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)
                    || (item.VoyageId != null && objective.VoyageIds.Contains(item.VoyageId, StringComparer.OrdinalIgnoreCase)))
                .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<CheckRun> ResolveChecks(Objective objective, List<CheckRun> checks, List<MissionSummary> missions)
        {
            HashSet<string> missionIds = missions.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return checks.Where(item => objective.CheckRunIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)
                    || (item.MissionId != null && missionIds.Contains(item.MissionId))
                    || (item.VoyageId != null && objective.VoyageIds.Contains(item.VoyageId, StringComparer.OrdinalIgnoreCase)))
                .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static DateTime? FindReadyUtc(string objectiveId, DateTime? dispatchUtc, List<ArmadaEvent> events)
        {
            DateTime? readyUtc = null;
            bool wasReady = false;
            foreach (ArmadaEvent evt in events.Where(item =>
                String.Equals(item.EventType, "objective.snapshot", StringComparison.OrdinalIgnoreCase)
                && String.Equals(item.EntityId, objectiveId, StringComparison.OrdinalIgnoreCase)
                && (!dispatchUtc.HasValue || item.CreatedUtc <= dispatchUtc.Value))
                .OrderBy(item => item.CreatedUtc).ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                if (String.IsNullOrWhiteSpace(evt.Payload)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(evt.Payload);
                    JsonElement root = doc.RootElement;
                    bool isReady = TryProperty(root, "backlogState", out JsonElement value)
                        && String.Equals(value.ToString(), "ReadyForDispatch", StringComparison.OrdinalIgnoreCase);
                    if (isReady && !wasReady) readyUtc = evt.CreatedUtc;
                    if (!isReady) readyUtc = null;
                    wasReady = isReady;
                }
                catch (JsonException) { }
            }
            return readyUtc;
        }

        private static bool TryProperty(JsonElement root, string name, out JsonElement value)
        {
            if (root.TryGetProperty(name, out value)) return true;
            string alternate = Char.ToUpperInvariant(name[0]) + name.Substring(1);
            return root.TryGetProperty(alternate, out value);
        }

        private static ProductionDistributionMetric Distribution(List<long> values, int unknown, string? forcedAvailability)
        {
            values.Sort();
            return new ProductionDistributionMetric
            {
                Availability = forcedAvailability ?? (unknown > 0 ? "partial" : values.Count > 0 ? "available" : "unavailable"),
                Observed = values.Count,
                Unknown = Math.Max(0, unknown),
                P50 = Percentile(values, 0.50),
                P90 = Percentile(values, 0.90),
                P95 = Percentile(values, 0.95)
            };
        }

        private static long? Percentile(List<long> values, double percentile)
        {
            if (values.Count == 0) return null;
            int index = (int)Math.Ceiling(percentile * values.Count) - 1;
            return values[Math.Clamp(index, 0, values.Count - 1)];
        }

        /// <summary>
        /// Attribute typed regression records to verified slices. An incident that names a failed
        /// Check classifies that Check, so the Check is not counted a second time. Records linked to a
        /// cohort slice count whenever they were detected; unlinked records count only when detected
        /// inside the window.
        /// </summary>
        private static void AttributeRegressions(
            ProductionSummaryResult result,
            Dictionary<string, RegressionSliceTarget> targets,
            List<Incident> incidents,
            List<CheckRun> checks,
            DateTime fromUtc,
            DateTime toUtc)
        {
            HashSet<string> classifiedCheckIds = incidents
                .Where(item => item.RegressionPurpose != RegressionPurposeEnum.None && !String.IsNullOrWhiteSpace(item.CheckRunId))
                .Select(item => item.CheckRunId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<RegressionRecord> records = incidents
                .Where(item => item.RegressionPurpose != RegressionPurposeEnum.None)
                .Select(item => new RegressionRecord(item.RegressionPurpose, item.RegressionCause, item.RegressionObjectiveId, item.RegressionLandedCommit, item.DetectedUtc))
                .ToList();
            records.AddRange(checks
                .Where(item => item.RegressionPurpose != RegressionPurposeEnum.None
                    && item.Status == CheckRunStatusEnum.Failed
                    && !classifiedCheckIds.Contains(item.Id))
                .Select(item => new RegressionRecord(item.RegressionPurpose, RegressionCauseEnum.Unclassified, item.RegressionObjectiveId, item.RegressionLandedCommit, item.CreatedUtc)));

            Dictionary<ProductionSummaryGroup, RegressionTally> tallies = result.Groups.ToDictionary(group => group, group => new RegressionTally());
            ProductionRegressionCoverage coverage = result.RegressionCoverage;
            foreach (RegressionRecord record in records)
            {
                bool detectedInWindow = record.DetectedUtc >= fromUtc && record.DetectedUtc < toUtc;
                RegressionSliceTarget? target = record.ObjectiveId != null ? targets.GetValueOrDefault(record.ObjectiveId) : null;
                if (target == null && !detectedInWindow) continue;
                coverage.RecordsRead++;
                if (record.Cause == RegressionCauseEnum.PreExisting || record.Cause == RegressionCauseEnum.Environment || record.Cause == RegressionCauseEnum.NotRegression)
                {
                    coverage.NotRegression++;
                    continue;
                }
                if (record.ObjectiveId == null) { coverage.Unlinked++; continue; }
                if (target == null) { coverage.OutsideCohort++; continue; }

                string? reason = record.Cause == RegressionCauseEnum.Unclassified ? "cause_unclassified"
                    : !target.Evidence.Verified ? "slice_not_verified"
                    : record.LandedCommit != null && !(target.Evidence.DeliveryCommits ?? new List<string>()).Any(commit => CheckRunGateRules.SameCommit(commit, record.LandedCommit)) ? "landed_commit_mismatch"
                    : null;
                if (reason != null)
                {
                    target.Group.PostLandRegressions.Unknown++;
                    Increment(target.Group.PostLandRegressions.UnknownByReason, reason);
                    coverage.Unknown++;
                    continue;
                }
                coverage.Attributed++;
                RegressionTally tally = tallies[target.Group];
                if (record.Purpose == RegressionPurposeEnum.Consumer) tally.Consumer.Add(record.ObjectiveId);
                else tally.Ledger.Add(record.ObjectiveId);
            }

            foreach (ProductionSummaryGroup group in result.Groups)
            {
                RegressionTally tally = tallies[group];
                ProductionRegressionMetric metric = group.PostLandRegressions;
                metric.VerifiedSlices = group.VerifiedLandedSlices.Count;
                metric.Consumer = tally.Consumer.Count;
                metric.Ledger = tally.Ledger.Count;
                metric.AffectedSlices = tally.Consumer.Union(tally.Ledger, StringComparer.OrdinalIgnoreCase).Count();
                metric.ConsumerRate = metric.VerifiedSlices > 0 ? (double)tally.Consumer.Count / metric.VerifiedSlices : null;
                metric.LedgerRate = metric.VerifiedSlices > 0 ? (double)tally.Ledger.Count / metric.VerifiedSlices : null;
                metric.Availability = metric.VerifiedSlices == 0 ? "unavailable" : metric.Unknown > 0 ? "partial" : "available";
            }
        }

        private static void FinishRate(ProductionRateMetric metric)
        {
            metric.Rate = metric.Eligible > 0 ? (double)metric.Accepted / metric.Eligible : null;
            int total = metric.Eligible + metric.Unknown;
            metric.Coverage = total > 0 ? (double)metric.Eligible / total : null;
            metric.Availability = metric.Eligible == 0 ? "unavailable" : metric.Unknown > 0 ? "partial" : "available";
        }

        private static void FinishRescueRuntime(ProductionRescueRuntimeMetric metric)
        {
            metric.Share = metric.TotalMissionMs > 0 ? (double)metric.RescueMs / metric.TotalMissionMs : null;
            int observed = metric.ClassifiedMissionCount + metric.HistoricalUnclassifiedMissionCount;
            metric.Coverage = observed > 0 ? (double)metric.ClassifiedMissionCount / observed : null;
            metric.RescuedSliceRate = metric.CompletedSlices > 0 ? (double)metric.RescuedSlices / metric.CompletedSlices : null;
            if (metric.ClassifiedMissionCount == 0) metric.Availability = "unavailable";
            else if (metric.UnknownMissionCount > 0 || metric.HistoricalUnclassifiedMissionCount > 0 || metric.RescueUnknownSlices > 0) metric.Availability = "partial";
            else metric.Availability = "available";
        }

        private static string SourceFamily(Objective objective)
        {
            List<string> tags = objective.Tags.Where(item => item.StartsWith("port:", StringComparison.OrdinalIgnoreCase)).ToList();
            return tags.Count == 1 ? tags[0].Substring("port:".Length).Trim().ToLowerInvariant()
                : tags.Count > 1 ? "invalid" : "unknown";
        }

        private static string WorkType(Objective objective)
        {
            return !String.IsNullOrWhiteSpace(objective.Category) ? objective.Category.Trim() : objective.Kind.ToString();
        }

        private static string Category(Objective objective) =>
            String.IsNullOrWhiteSpace(objective.Category) ? "unknown" : objective.Category.Trim();

        private static bool MatchesFilter(Objective objective, ProductionSummaryQuery query)
        {
            return (String.IsNullOrWhiteSpace(query.SourceFamily) || String.Equals(SourceFamily(objective), query.SourceFamily, StringComparison.OrdinalIgnoreCase))
                && (String.IsNullOrWhiteSpace(query.WorkType) || String.Equals(WorkType(objective), query.WorkType, StringComparison.OrdinalIgnoreCase));
        }

        private static void Increment(Dictionary<string, int> values, string key) => values[key] = values.GetValueOrDefault(key) + 1;

        private static void AddAvailabilityWarnings(ProductionSummaryResult result)
        {
            result.Warnings.Add("ready_to_dispatch_delay_partial: historical snapshots do not prove full dispatch-preflight readiness");
            result.Warnings.Add("host_slot_queue_unavailable: command-slot request time is not recorded");
            result.Warnings.Add("repeated_research_unavailable: research activity is not linked to preparation claims");
            result.Warnings.Add("eligible_idle_lane_minutes_unavailable: historical lane eligibility intervals are not recorded");
        }

        private async Task<List<Objective>> ReadObjectivesAsync(AuthContext auth, ProductionSummaryResult report, CancellationToken token)
        {
            List<Objective> values = auth.IsAdmin
                ? await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false)
                : auth.IsTenantAdmin
                    ? await _Database.Objectives.EnumerateAsync(auth.TenantId!, token).ConfigureAwait(false)
                    : await _Database.Objectives.EnumerateAsync(auth.TenantId!, auth.UserId!, token).ConfigureAwait(false);
            return Bound(values, "objectives", report);
        }

        private async Task<List<Incident>> ReadIncidentsAsync(AuthContext auth, ProductionSummaryResult report, CancellationToken token)
        {
            List<ArmadaEvent> snapshots = await ReadPagesAsync<ArmadaEvent>(async query =>
            {
                query.EventType = "incident.snapshot";
                return auth.IsAdmin
                    ? await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false)
                    : auth.IsTenantAdmin
                        ? await _Database.Events.EnumerateAsync(auth.TenantId!, query, token).ConfigureAwait(false)
                        : await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false);
            }, "incident_snapshots", report, token).ConfigureAwait(false);

            List<Incident> incidents = new List<Incident>();
            foreach (IGrouping<string, ArmadaEvent> history in snapshots
                .Where(item => String.Equals(item.EntityType, "incident", StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(item.EntityId))
                .GroupBy(item => item.EntityId!, StringComparer.OrdinalIgnoreCase))
            {
                ArmadaEvent latest = history.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id, StringComparer.Ordinal).First();
                Incident? incident = null;
                if (!String.IsNullOrWhiteSpace(latest.Payload))
                {
                    try { incident = JsonSerializer.Deserialize<Incident>(latest.Payload, _IncidentJson); }
                    catch (JsonException) { incident = null; }
                }
                if (incident == null) report.RegressionCoverage.UnreadableRecords++;
                else incidents.Add(incident);
            }
            return incidents;
        }

        private async Task<List<MissionAttemptFact>> ReadAttemptFactsAsync(AuthContext auth, DateTime fromUtc, DateTime toUtc, ProductionSummaryResult report, CancellationToken token)
        {
            ProductionFactPage<MissionAttemptFact> page = await _Database.MissionAttemptFacts.EnumerateAsync(new ProductionFactQuery
            {
                TenantId = auth.IsAdmin ? null : auth.TenantId,
                UserId = auth.IsAdmin || auth.IsTenantAdmin ? null : auth.UserId,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Limit = _RecordLimit
            }, token).ConfigureAwait(false);
            CompleteScan(page.Items.Count, page.Items.Count + (page.Truncated ? 1 : 0), page.Truncated, "mission_attempt_facts", report);
            return page.Items;
        }

        private async Task<List<MissionSummary>> ReadMissionSummariesAsync(AuthContext auth, ProductionSummaryResult report, CancellationToken token)
        {
            return await ReadPagesAsync<MissionSummary>(async query => auth.IsAdmin
                ? await _Database.Missions.EnumerateMissionSummariesAsync(query, token).ConfigureAwait(false)
                : auth.IsTenantAdmin
                    ? await _Database.Missions.EnumerateMissionSummariesAsync(auth.TenantId!, query, token).ConfigureAwait(false)
                    : await _Database.Missions.EnumerateMissionSummariesAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false), "missions", report, token).ConfigureAwait(false);
        }

        private async Task<List<Voyage>> ReadVoyagesAsync(AuthContext auth, ProductionSummaryResult report, CancellationToken token)
        {
            return await ReadPagesAsync<Voyage>(async query => auth.IsAdmin
                ? await _Database.Voyages.EnumerateAsync(query, token).ConfigureAwait(false)
                : auth.IsTenantAdmin
                    ? await _Database.Voyages.EnumerateAsync(auth.TenantId!, query, token).ConfigureAwait(false)
                    : await _Database.Voyages.EnumerateAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false), "voyages", report, token).ConfigureAwait(false);
        }

        private async Task<List<MergeEntry>> ReadMergeEntriesAsync(AuthContext auth, ProductionSummaryResult report, CancellationToken token)
        {
            return await ReadPagesAsync<MergeEntry>(async query => auth.IsAdmin
                ? await _Database.MergeEntries.EnumerateAsync(query, token).ConfigureAwait(false)
                : auth.IsTenantAdmin
                    ? await _Database.MergeEntries.EnumerateAsync(auth.TenantId!, query, token).ConfigureAwait(false)
                    : await _Database.MergeEntries.EnumerateAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false), "merge_entries", report, token).ConfigureAwait(false);
        }

        private async Task<List<CheckRun>> ReadChecksAsync(AuthContext auth, ProductionSummaryResult report, CancellationToken token)
        {
            CheckRunQuery query = new CheckRunQuery
            {
                TenantId = auth.IsAdmin ? null : auth.TenantId,
                UserId = auth.IsAdmin || auth.IsTenantAdmin ? null : auth.UserId,
                PageNumber = 1,
                PageSize = _PageSize
            };
            List<CheckRun> values = new List<CheckRun>();
            long totalRecords = 0;
            while (values.Count < _RecordLimit)
            {
                token.ThrowIfCancellationRequested();
                EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
                totalRecords = page.TotalRecords;
                values.AddRange(page.Objects.Take(_RecordLimit - values.Count));
                if (page.PageNumber >= page.TotalPages || page.Objects.Count == 0) break;
                query.PageNumber++;
            }
            CompleteScan(values.Count, totalRecords, values.Count < totalRecords, "checks", report);
            return values;
        }

        private async Task<List<ArmadaEvent>> ReadEventsAsync(AuthContext auth, DateTime fromUtc, DateTime toUtc, ProductionSummaryResult report, CancellationToken token)
        {
            return await ReadPagesAsync<ArmadaEvent>(async query =>
            {
                query.CreatedAfter = fromUtc;
                query.CreatedBefore = toUtc;
                return auth.IsAdmin
                    ? await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false)
                    : auth.IsTenantAdmin
                        ? await _Database.Events.EnumerateAsync(auth.TenantId!, query, token).ConfigureAwait(false)
                        : await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false);
            }, "events", report, token).ConfigureAwait(false);
        }

        private static async Task<List<T>> ReadPagesAsync<T>(
            Func<EnumerationQuery, Task<EnumerationResult<T>>> read,
            string source,
            ProductionSummaryResult report,
            CancellationToken token)
        {
            List<T> values = new List<T>();
            int pageNumber = 1;
            bool truncated = false;
            long totalRecords = 0;
            while (values.Count < _RecordLimit)
            {
                token.ThrowIfCancellationRequested();
                EnumerationResult<T> page = await read(new EnumerationQuery
                {
                    PageNumber = pageNumber,
                    PageSize = _PageSize,
                    Order = EnumerationOrderEnum.CreatedDescending
                }).ConfigureAwait(false);
                totalRecords = page.TotalRecords;
                values.AddRange(page.Objects.Take(_RecordLimit - values.Count));
                if (page.PageNumber >= page.TotalPages || page.Objects.Count == 0) break;
                if (values.Count >= _RecordLimit) { truncated = true; break; }
                pageNumber++;
            }
            CompleteScan(values.Count, totalRecords, truncated || values.Count < totalRecords, source, report);
            return values;
        }

        private static List<T> Bound<T>(List<T> values, string source, ProductionSummaryResult report)
        {
            bool truncated = values.Count > _RecordLimit;
            List<T> bounded = truncated ? values.Take(_RecordLimit).ToList() : values;
            CompleteScan(bounded.Count, values.Count, truncated, source, report);
            return bounded;
        }

        private static void CompleteScan(int count, long totalRecords, bool truncated, string source, ProductionSummaryResult report)
        {
            report.Scan.RecordsRead += count;
            report.Scan.Sources.Add(new ProductionSourceScan
            {
                Source = source,
                RecordsRead = count,
                TotalRecords = totalRecords,
                Truncated = truncated
            });
            if (!truncated) return;
            report.Scan.Truncated = true;
            report.IsComplete = false;
            report.Warnings.Add(source + "_scan_truncated");
        }

        private sealed class RegressionSliceTarget
        {
            internal RegressionSliceTarget(ProductionSummaryGroup group, SliceEvidence evidence)
            {
                Group = group;
                Evidence = evidence;
            }

            internal ProductionSummaryGroup Group { get; }

            internal SliceEvidence Evidence { get; }
        }

        private sealed class RegressionTally
        {
            internal HashSet<string> Consumer { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal HashSet<string> Ledger { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class RegressionRecord
        {
            internal RegressionRecord(RegressionPurposeEnum purpose, RegressionCauseEnum cause, string? objectiveId, string? landedCommit, DateTime detectedUtc)
            {
                Purpose = purpose;
                Cause = cause;
                ObjectiveId = String.IsNullOrWhiteSpace(objectiveId) ? null : objectiveId;
                LandedCommit = String.IsNullOrWhiteSpace(landedCommit) ? null : landedCommit;
                DetectedUtc = detectedUtc;
            }

            internal RegressionPurposeEnum Purpose { get; }

            internal RegressionCauseEnum Cause { get; }

            internal string? ObjectiveId { get; }

            internal string? LandedCommit { get; }

            internal DateTime DetectedUtc { get; }
        }

        private sealed class AttemptChainClassification
        {
            internal int HistoricalRuns { get; set; }

            internal bool Rescued { get; set; }

            internal bool FirstPassDisqualified { get; set; }
        }

        /// <summary>
        /// Index of durable attempt facts. Chain identity, rescue classification, and first-pass
        /// disqualification come only from these facts, never from titles or final mission state.
        /// </summary>
        private sealed class AttemptFactIndex
        {
            private readonly Dictionary<string, List<MissionAttemptFact>> _ByMission;
            private readonly Dictionary<string, HashSet<string>> _MissionIdsByRoot;

            internal AttemptFactIndex(List<MissionAttemptFact> facts)
            {
                _ByMission = facts
                    .GroupBy(item => item.MissionId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
                _MissionIdsByRoot = facts
                    .GroupBy(item => item.RootMissionId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Select(item => item.MissionId).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            }

            internal static bool Ran(MissionSummary mission)
            {
                if (mission.StartedUtc.HasValue || mission.TotalRuntimeMs.HasValue) return true;
                return mission.Status == MissionStatusEnum.InProgress
                    || mission.Status == MissionStatusEnum.WorkProduced
                    || mission.Status == MissionStatusEnum.PullRequestOpen
                    || mission.Status == MissionStatusEnum.Complete
                    || mission.Status == MissionStatusEnum.Failed
                    || mission.Status == MissionStatusEnum.LandingFailed;
            }

            internal bool HasAttempt(string missionId) =>
                _ByMission.TryGetValue(missionId, out List<MissionAttemptFact>? facts)
                && facts.Any(item => item.FactType == MissionAttemptFactTypeEnum.AttemptStarted);

            internal bool IsRescue(string missionId) =>
                _ByMission.TryGetValue(missionId, out List<MissionAttemptFact>? facts) && facts.Any(item => item.IsRescue);

            internal List<MissionSummary> ResolveChain(List<MissionSummary> sliceMissions, Dictionary<string, MissionSummary> missionById)
            {
                Dictionary<string, MissionSummary> chain = sliceMissions.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
                HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (MissionSummary mission in sliceMissions)
                {
                    roots.Add(mission.Id);
                    if (_ByMission.TryGetValue(mission.Id, out List<MissionAttemptFact>? facts))
                        foreach (MissionAttemptFact fact in facts) roots.Add(fact.RootMissionId);
                }
                foreach (string root in roots)
                {
                    if (missionById.TryGetValue(root, out MissionSummary? rootMission)) chain.TryAdd(rootMission.Id, rootMission);
                    if (!_MissionIdsByRoot.TryGetValue(root, out HashSet<string>? members)) continue;
                    foreach (string memberId in members)
                        if (missionById.TryGetValue(memberId, out MissionSummary? member)) chain.TryAdd(member.Id, member);
                }
                return chain.Values.ToList();
            }

            internal AttemptChainClassification Classify(List<MissionSummary> chainMissions)
            {
                AttemptChainClassification result = new AttemptChainClassification();
                foreach (MissionSummary mission in chainMissions)
                {
                    if (!HasAttempt(mission.Id))
                    {
                        if (Ran(mission)) result.HistoricalRuns++;
                        continue;
                    }
                    List<MissionAttemptFact> facts = _ByMission[mission.Id];
                    if (facts.Any(item => item.IsRescue)) result.Rescued = true;
                    if (facts.Any(MissionAttemptFactRules.DisqualifiesFirstPass)) result.FirstPassDisqualified = true;
                    int attempts = facts.Count(item => item.FactType == MissionAttemptFactTypeEnum.AttemptStarted);
                    int explainedReRuns = facts.Count(item => item.FactType == MissionAttemptFactTypeEnum.Retried);
                    // A second launch with no recorded reason is still a second attempt.
                    if (attempts - 1 > explainedReRuns) result.FirstPassDisqualified = true;
                }
                return result;
            }
        }

        private sealed record SliceEvidence(bool Verified, string Reason, DateTime? LastLandingUtc, List<string>? DeliveryCommits = null)
        {
            public static SliceEvidence Failed(string reason) => new SliceEvidence(false, reason, null);
        }
    }
}
