namespace Armada.Core.Models
{
    /// <summary>Query for one bounded verified-production summary.</summary>
    public sealed class ProductionSummaryQuery
    {
        /// <summary>Inclusive UTC window start. Defaults to seven days before the end.</summary>
        public DateTime? FromUtc { get; set; }

        /// <summary>Exclusive UTC window end. Defaults to the current time.</summary>
        public DateTime? ToUtc { get; set; }

        /// <summary>Optional exact source-family group filter.</summary>
        public string? SourceFamily { get; set; }

        /// <summary>Optional exact work-type group filter.</summary>
        public string? WorkType { get; set; }
    }

    /// <summary>One verified-production report.</summary>
    public sealed class ProductionSummaryResult
    {
        /// <summary>Inclusive UTC window start.</summary>
        public DateTime FromUtc { get; set; }

        /// <summary>Exclusive UTC window end.</summary>
        public DateTime ToUtc { get; set; }

        /// <summary>UTC report creation time.</summary>
        public DateTime GeneratedUtc { get; set; }

        /// <summary>True when every source scan completed within its bound.</summary>
        public bool IsComplete { get; set; } = true;

        /// <summary>Warnings about unavailable, partial, or truncated data.</summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>Scan coverage.</summary>
        public ProductionSummaryScan Scan { get; set; } = new ProductionSummaryScan();

        /// <summary>Results grouped by source family and work type.</summary>
        public List<ProductionSummaryGroup> Groups { get; set; } = new List<ProductionSummaryGroup>();

        /// <summary>Raw completed leaf slices in the selected cohort.</summary>
        public int RawCompletedSlices { get; set; }

        /// <summary>Raw completed leaf slices per complete day.</summary>
        public double? RawCompletedSlicesPerDay { get; set; }

        /// <summary>Verified landed slices per complete day.</summary>
        public double? VerifiedLandedSlicesPerDay { get; set; }

        /// <summary>Complete UTC days in the selected window.</summary>
        public int CompleteDayCount { get; set; }

        /// <summary>Raw completed leaf counts for every UTC day in the window.</summary>
        public List<ProductionDailyCount> RawByDay { get; set; } = new List<ProductionDailyCount>();

        /// <summary>Fleet-wide lane eligibility, occupancy, and capacity time with coverage.</summary>
        public ProductionLaneTimeSummary LaneTime { get; set; } = new ProductionLaneTimeSummary();

        /// <summary>Preparation claim observations in the window grouped by the objective source family.</summary>
        public Dictionary<string, ProductionClaimObservationCounts> ClaimObservationsBySourceFamily { get; set; } = new Dictionary<string, ProductionClaimObservationCounts>(StringComparer.Ordinal);

        /// <summary>Coverage of typed post-land regression records.</summary>
        public ProductionRegressionCoverage RegressionCoverage { get; set; } = new ProductionRegressionCoverage();

        /// <summary>Counts excluded by stable reason code.</summary>
        public Dictionary<string, int> ExclusionsByReason { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>Bounded scan diagnostics.</summary>
    public sealed class ProductionSummaryScan
    {
        /// <summary>Total records inspected.</summary>
        public int RecordsRead { get; set; }

        /// <summary>Maximum records permitted for each paged source.</summary>
        public int RecordLimit { get; set; }

        /// <summary>True when at least one source exceeded its bound.</summary>
        public bool Truncated { get; set; }

        /// <summary>Per-source scan coverage.</summary>
        public List<ProductionSourceScan> Sources { get; set; } = new List<ProductionSourceScan>();
    }

    /// <summary>Coverage for one durable record source.</summary>
    public sealed class ProductionSourceScan
    {
        /// <summary>Stable source name.</summary>
        public string Source { get; set; } = String.Empty;

        /// <summary>Records inspected.</summary>
        public int RecordsRead { get; set; }

        /// <summary>Total records reported by the source.</summary>
        public long TotalRecords { get; set; }

        /// <summary>True when the scan stopped at its bound.</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>Metrics for one classification group.</summary>
    public sealed class ProductionSummaryGroup
    {
        /// <summary>Explicit source family, or unclassified.</summary>
        public string SourceFamily { get; set; } = "unknown";

        /// <summary>Explicit work type, category, or objective kind.</summary>
        public string WorkType { get; set; } = "unknown";

        /// <summary>Objective category as an independent dimension.</summary>
        public string Category { get; set; } = "unknown";

        /// <summary>Objective kind as an independent dimension.</summary>
        public string Kind { get; set; } = "unknown";

        /// <summary>Verified landed objective count.</summary>
        public ProductionCountMetric VerifiedLandedSlices { get; set; } = new ProductionCountMetric();

        /// <summary>Ready-to-dispatch delay. Current durable data makes this partial.</summary>
        public ProductionDistributionMetric ReadyToDispatchDelayMs { get; set; } = ProductionDistributionMetric.Unavailable();

        /// <summary>Check timing distributions.</summary>
        public ProductionCheckTimingMetric CheckTiming { get; set; } = new ProductionCheckTimingMetric();

        /// <summary>First-pass acceptance. Current durable data makes this partial.</summary>
        public ProductionRateMetric FirstPassAcceptance { get; set; } = ProductionRateMetric.Unavailable();

        /// <summary>Rescue runtime share.</summary>
        public ProductionRescueRuntimeMetric RescueRuntime { get; set; } = new ProductionRescueRuntimeMetric();

        /// <summary>Delay from last landing to objective verification.</summary>
        public ProductionDistributionMetric LandedToVerifiedCloseoutMs { get; set; } = new ProductionDistributionMetric();

        /// <summary>Post-land consumer and ledger regressions.</summary>
        public ProductionRegressionMetric PostLandRegressions { get; set; } = new ProductionRegressionMetric();

        /// <summary>Repeated research after dispatch.</summary>
        public ProductionRepeatedResearchMetric RepeatedResearch { get; set; } = new ProductionRepeatedResearchMetric();

        /// <summary>Eligible idle lane-minutes.</summary>
        public ProductionIdleLaneMetric EligibleIdleLaneMinutes { get; set; } = new ProductionIdleLaneMetric();

        /// <summary>Daily verified slice counts.</summary>
        public List<ProductionDailyCount> ByDay { get; set; } = new List<ProductionDailyCount>();
    }

    /// <summary>Metric count and availability.</summary>
    public sealed class ProductionCountMetric
    {
        /// <summary>Availability: available, partial, or unavailable.</summary>
        public string Availability { get; set; } = "available";

        /// <summary>Observed count.</summary>
        public int Count { get; set; }

        /// <summary>Records that could not be classified.</summary>
        public int Unknown { get; set; }
    }

    /// <summary>Observed distribution with coverage.</summary>
    public sealed class ProductionDistributionMetric
    {
        /// <summary>Availability: available, partial, or unavailable.</summary>
        public string Availability { get; set; } = "available";

        /// <summary>Observed values.</summary>
        public int Observed { get; set; }

        /// <summary>Unknown values.</summary>
        public int Unknown { get; set; }

        /// <summary>Median value.</summary>
        public long? P50 { get; set; }

        /// <summary>Ninetieth percentile value.</summary>
        public long? P90 { get; set; }

        /// <summary>Ninety-fifth percentile value.</summary>
        public long? P95 { get; set; }

        /// <summary>Create an unavailable metric.</summary>
        public static ProductionDistributionMetric Unavailable() => new ProductionDistributionMetric { Availability = "unavailable" };
    }

    /// <summary>Check timing split by lifecycle segment.</summary>
    public sealed class ProductionCheckTimingMetric
    {
        /// <summary>Time from durable creation to command start.</summary>
        public ProductionDistributionMetric ArmedToStartMs { get; set; } = new ProductionDistributionMetric();

        /// <summary>Time from durable creation to the host command-slot request: preparation and eligibility.</summary>
        public ProductionDistributionMetric PreparationDelayMs { get; set; } = ProductionDistributionMetric.Unavailable();

        /// <summary>Pure host command-slot wait: from the slot request to command start.</summary>
        public ProductionDistributionMetric HostQueueMs { get; set; } = ProductionDistributionMetric.Unavailable();

        /// <summary>Measured command execution duration.</summary>
        public ProductionDistributionMetric ExecutionMs { get; set; } = new ProductionDistributionMetric();
    }

    /// <summary>Rate metric with explicit unavailable data.</summary>
    public sealed class ProductionRateMetric
    {
        /// <summary>Availability: available, partial, or unavailable.</summary>
        public string Availability { get; set; } = "available";

        /// <summary>Successful records.</summary>
        public int Accepted { get; set; }

        /// <summary>Eligible records.</summary>
        public int Eligible { get; set; }

        /// <summary>Unknown records.</summary>
        public int Unknown { get; set; }

        /// <summary>Accepted divided by eligible.</summary>
        public double? Rate { get; set; }

        /// <summary>Eligible divided by eligible plus unknown records.</summary>
        public double? Coverage { get; set; }

        /// <summary>Unknown records by stable reason code.</summary>
        public Dictionary<string, int> UnknownByReason { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Create an unavailable metric.</summary>
        public static ProductionRateMetric Unavailable() => new ProductionRateMetric { Availability = "unavailable" };
    }

    /// <summary>Rescue runtime share.</summary>
    public sealed class ProductionRescueRuntimeMetric
    {
        /// <summary>Availability: available, partial, or unavailable.</summary>
        public string Availability { get; set; } = "available";

        /// <summary>Known rescue runtime.</summary>
        public long RescueMs { get; set; }

        /// <summary>Known total mission runtime.</summary>
        public long TotalMissionMs { get; set; }

        /// <summary>Missions without a complete runtime.</summary>
        public int UnknownMissionCount { get; set; }

        /// <summary>Rescue runtime divided by total runtime.</summary>
        public double? Share { get; set; }

        /// <summary>Missions whose runs are covered by durable attempt facts.</summary>
        public int ClassifiedMissionCount { get; set; }

        /// <summary>Missions classified as rescue work by the durable rescue marker.</summary>
        public int RescueMissionCount { get; set; }

        /// <summary>Missions that ran before attempt facts were recorded. They are excluded from the share.</summary>
        public int HistoricalUnclassifiedMissionCount { get; set; }

        /// <summary>Runtime of missions that ran before attempt facts were recorded.</summary>
        public long HistoricalUnclassifiedMs { get; set; }

        /// <summary>Classified missions divided by classified plus historical unclassified missions.</summary>
        public double? Coverage { get; set; }

        /// <summary>Completed slices in the group.</summary>
        public int CompletedSlices { get; set; }

        /// <summary>Completed slices whose attempt chain contains rescue work.</summary>
        public int RescuedSlices { get; set; }

        /// <summary>Completed slices without rescue facts that also contain historical runs.</summary>
        public int RescueUnknownSlices { get; set; }

        /// <summary>Rescued slices divided by completed slices.</summary>
        public double? RescuedSliceRate { get; set; }
    }

    /// <summary>Post-land consumer and ledger regressions attributed to verified slices.</summary>
    public sealed class ProductionRegressionMetric
    {
        /// <summary>Availability.</summary>
        public string Availability { get; set; } = "unavailable";

        /// <summary>Verified slices in the group; the denominator of both rates.</summary>
        public int VerifiedSlices { get; set; }

        /// <summary>Distinct verified slices with at least one attributed regression.</summary>
        public int? AffectedSlices { get; set; }

        /// <summary>Distinct verified slices with an attributed consumer regression.</summary>
        public int? Consumer { get; set; }

        /// <summary>Distinct verified slices with an attributed ledger regression.</summary>
        public int? Ledger { get; set; }

        /// <summary>Consumer regression slices divided by verified slices.</summary>
        public double? ConsumerRate { get; set; }

        /// <summary>Ledger regression slices divided by verified slices.</summary>
        public double? LedgerRate { get; set; }

        /// <summary>Regression records linked to a slice in this group that could not be attributed.</summary>
        public int Unknown { get; set; }

        /// <summary>Unattributed records by stable reason code.</summary>
        public Dictionary<string, int> UnknownByReason { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>Report-wide coverage for typed regression records.</summary>
    public sealed class ProductionRegressionCoverage
    {
        /// <summary>Regression records read: incidents with a purpose and failed Checks with a purpose.</summary>
        public int RecordsRead { get; set; }

        /// <summary>Records attributed to a verified slice in the report.</summary>
        public int Attributed { get; set; }

        /// <summary>Landed-change records with no objective link.</summary>
        public int Unlinked { get; set; }

        /// <summary>Records linked to an objective outside the report cohort.</summary>
        public int OutsideCohort { get; set; }

        /// <summary>Records whose cause is pre-existing, environment, or not a regression.</summary>
        public int NotRegression { get; set; }

        /// <summary>Records linked to a slice in the cohort that could not be attributed.</summary>
        public int Unknown { get; set; }

        /// <summary>Incident snapshots whose payload could not be read.</summary>
        public int UnreadableRecords { get; set; }
    }

    /// <summary>Repeated research measured from durable preparation claim observations.</summary>
    public sealed class ProductionRepeatedResearchMetric
    {
        /// <summary>Availability.</summary>
        public string Availability { get; set; } = "unavailable";

        /// <summary>Completed slices in the group.</summary>
        public int Slices { get; set; }

        /// <summary>Completed slices with at least one recorded claim observation.</summary>
        public int CoveredSlices { get; set; }

        /// <summary>Covered slices divided by slices.</summary>
        public double? Coverage { get; set; }

        /// <summary>Covered slices with at least one re-established claim.</summary>
        public int? AffectedSlices { get; set; }

        /// <summary>Distinct claims that were re-established with unchanged statement, evidence, and anchors.</summary>
        public int? RepeatedClaims { get; set; }

        /// <summary>Re-establishment observations, counting each repeat.</summary>
        public int ReestablishedObservations { get; set; }

        /// <summary>Distinct claims established or changed.</summary>
        public int EstablishedClaims { get; set; }

        /// <summary>Distinct stale claims that were revalidated. This is not repeated research.</summary>
        public int RevalidatedClaims { get; set; }

        /// <summary>Distinct verified claims delivered to dispatched work.</summary>
        public int ReusedClaims { get; set; }

        /// <summary>Repeated research duration. Always null: research duration is not recorded.</summary>
        public long? RepeatedMinutes { get; set; }

        /// <summary>Slices without claim observations.</summary>
        public int Unknown { get; set; }

        /// <summary>Uncovered slices by stable reason code.</summary>
        public Dictionary<string, int> UnknownByReason { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>Claim observation counts for one source family.</summary>
    public sealed class ProductionClaimObservationCounts
    {
        /// <summary>Established observations.</summary>
        public int Established { get; set; }

        /// <summary>Re-established observations.</summary>
        public int Reestablished { get; set; }

        /// <summary>Revalidated observations.</summary>
        public int Revalidated { get; set; }

        /// <summary>Reused observations.</summary>
        public int Reused { get; set; }
    }

    /// <summary>Eligible idle lane-minutes attributed to a group by the eligible objectives' source family.</summary>
    public sealed class ProductionIdleLaneMetric
    {
        /// <summary>Availability.</summary>
        public string Availability { get; set; } = "unavailable";

        /// <summary>Observed eligible idle lane-minutes where this group's source family was eligible.</summary>
        public long? ObservedMinutes { get; set; }

        /// <summary>Fleet-wide expected lane-minutes in the window.</summary>
        public long? ExpectedSampleMinutes { get; set; }

        /// <summary>Fleet-wide observed lane-minutes divided by expected lane-minutes.</summary>
        public double? Coverage { get; set; }
    }

    /// <summary>Fleet-wide lane time reconstructed from durable lane state transitions.</summary>
    public sealed class ProductionLaneTimeSummary
    {
        /// <summary>Availability: available, partial, or unavailable.</summary>
        public string Availability { get; set; } = "unavailable";

        /// <summary>Reason the summary is unavailable, when it is.</summary>
        public string? UnavailableReason { get; set; }

        /// <summary>Lanes with at least one observation that reaches the window.</summary>
        public int Lanes { get; set; }

        /// <summary>Lanes multiplied by the window minutes that have already elapsed.</summary>
        public double ExpectedLaneMinutes { get; set; }

        /// <summary>Lane-minutes covered by a trusted observation.</summary>
        public double ObservedLaneMinutes { get; set; }

        /// <summary>Expected lane-minutes without a trusted observation.</summary>
        public double UnobservedLaneMinutes { get; set; }

        /// <summary>Unobserved intervals: before a lane's first observation or after an observation's trust window.</summary>
        public int IncompleteIntervals { get; set; }

        /// <summary>Observed minutes when a lane had eligible work and spare lane capacity and nothing fleet-wide blocked it.</summary>
        public double EligibleIdleMinutes { get; set; }

        /// <summary>Observed minutes with eligible work and spare lane capacity while the fleet-wide limit was reached.</summary>
        public double FleetBlockedMinutes { get; set; }

        /// <summary>Observed minutes with eligible work and spare lane capacity while a dispatch hold was engaged.</summary>
        public double HoldBlockedMinutes { get; set; }

        /// <summary>Observed lane-minutes divided by expected lane-minutes.</summary>
        public double? Coverage { get; set; }
    }

    /// <summary>Verified slice count for one UTC date.</summary>
    public sealed class ProductionDailyCount
    {
        /// <summary>UTC date at midnight.</summary>
        public DateTime DayUtc { get; set; }

        /// <summary>Verified landed slice count.</summary>
        public int Count { get; set; }
    }
}
