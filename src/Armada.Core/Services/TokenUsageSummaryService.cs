namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Builds the token-usage summary for a window: the table records inside it, plus the mission token-usage events
    /// inside it that have no table record. The read costs a fixed number of statements plus one per batch of
    /// missions, whatever the window holds, so a long window reads in about the time of a short one.
    /// </summary>
    public sealed class TokenUsageSummaryService
    {
        #region Private-Members

        private readonly ITokenUsageMethods _TokenUsage;
        private readonly JsonSerializerOptions _JsonOptions;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="tokenUsage">Token-usage method set.</param>
        /// <param name="jsonOptions">JSON options used for event payloads.</param>
        public TokenUsageSummaryService(ITokenUsageMethods tokenUsage, JsonSerializerOptions jsonOptions)
        {
            _TokenUsage = tokenUsage ?? throw new ArgumentNullException(nameof(tokenUsage));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Summarize a window. The query must carry its start and end, and its tenant and user scope.
        /// </summary>
        /// <param name="query">Window, bucket width, filters and scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The summary.</returns>
        public async Task<TokenUsageSummaryResult> SummarizeAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (!query.FromUtc.HasValue || !query.ToUtc.HasValue) throw new ArgumentException("A summary needs a window start and end.", nameof(query));
            CollectionLimits.RequireInclusiveBuckets(query.FromUtc.Value, query.ToUtc.Value, query.BucketMinutes <= 0 ? 15 : query.BucketMinutes);

            List<TokenUsageRecord> records = await _TokenUsage.EnumerateForSummaryAsync(query, token).ConfigureAwait(false);
            List<ArmadaEvent> legacyEvents = await _TokenUsage.EnumerateLegacyEventsAsync(query, token).ConfigureAwait(false);

            // An event matches its table record by mission identity at any creation time, so the usage is counted
            // where its table record lies even when the two fall on opposite sides of a window edge.
            HashSet<string> missionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ArmadaEvent armadaEvent in legacyEvents)
            {
                string? missionId = armadaEvent.MissionId ?? armadaEvent.EntityId;
                if (!String.IsNullOrWhiteSpace(missionId)) missionIds.Add(missionId);
            }

            List<TokenUsageRecord> identityRecords = missionIds.Count == 0
                ? new List<TokenUsageRecord>()
                : await _TokenUsage.EnumerateBySourceIdsAsync(query, "mission", missionIds, token).ConfigureAwait(false);

            List<TokenUsageRecord> merged = TokenUsageCompatibility.MergeLegacyEvents(records, legacyEvents, identityRecords, _JsonOptions);
            return TokenUsageSummaryBuilder.Build(merged, query);
        }

        #endregion
    }
}
