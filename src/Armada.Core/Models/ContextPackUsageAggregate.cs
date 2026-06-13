namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Fleet-level aggregate metrics derived from recent mission.context_pack_usage events.
    /// </summary>
    public sealed class ContextPackUsageAggregate
    {
        #region Public-Members

        /// <summary>
        /// Number of missions included in the aggregate after parsing usage events.
        /// </summary>
        public int MissionsConsidered { get; set; }

        /// <summary>
        /// Share of considered missions that had a staged context pack (0-1).
        /// </summary>
        public double PackStagedShare
        {
            get => _PackStagedShare;
            set => _PackStagedShare = ClampShare(value);
        }

        /// <summary>
        /// Share of pack-staged missions that read the pack before any search tool use (0-1).
        /// </summary>
        public double ReadBeforeSearchShare
        {
            get => _ReadBeforeSearchShare;
            set => _ReadBeforeSearchShare = ClampShare(value);
        }

        /// <summary>
        /// Average search-tool call count across considered missions.
        /// </summary>
        public double AverageSearchToolCalls { get; set; }

        #endregion

        #region Private-Members

        private double _PackStagedShare;
        private double _ReadBeforeSearchShare;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build an aggregate from parsed per-mission usage summaries.
        /// Empty input yields zeroed metrics.
        /// </summary>
        /// <param name="summaries">Usage summaries, typically one per mission.</param>
        public static ContextPackUsageAggregate FromSummaries(IReadOnlyList<ContextPackUsageSummary> summaries)
        {
            ContextPackUsageAggregate aggregate = new ContextPackUsageAggregate();
            if (summaries == null || summaries.Count == 0)
            {
                return aggregate;
            }

            int missionsConsidered = summaries.Count;
            int packStagedCount = 0;
            int readBeforeSearchCount = 0;
            int totalSearchToolCalls = 0;

            for (int i = 0; i < summaries.Count; i++)
            {
                ContextPackUsageSummary summary = summaries[i];
                if (summary == null) continue;

                totalSearchToolCalls += summary.SearchToolCallCount;
                if (!summary.ContextPackStaged) continue;

                packStagedCount++;
                if (CountsAsReadBeforeSearch(summary.ContextPackCompliance))
                {
                    readBeforeSearchCount++;
                }
            }

            aggregate.MissionsConsidered = missionsConsidered;
            aggregate.PackStagedShare = missionsConsidered > 0
                ? (double)packStagedCount / missionsConsidered
                : 0.0;
            aggregate.ReadBeforeSearchShare = packStagedCount > 0
                ? (double)readBeforeSearchCount / packStagedCount
                : 0.0;
            aggregate.AverageSearchToolCalls = missionsConsidered > 0
                ? (double)totalSearchToolCalls / missionsConsidered
                : 0.0;

            return aggregate;
        }

        #endregion

        #region Private-Methods

        private static double ClampShare(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        private static bool CountsAsReadBeforeSearch(string compliance)
        {
            return String.Equals(compliance, "ReadBeforeSearch", StringComparison.Ordinal)
                || String.Equals(compliance, "PackReadNoSearch", StringComparison.Ordinal);
        }

        #endregion
    }
}
