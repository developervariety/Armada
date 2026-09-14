namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Services;

    /// <summary>
    /// The instants before which retention deletes records. Built from the retention settings at one
    /// moment so every table in a run is purged against the same clock.
    /// </summary>
    public sealed class DataExpiryCutoffs
    {
        /// <summary>
        /// The append-only production metric fact tables purged by <see cref="ProductionFactCutoffUtc"/>.
        /// </summary>
        public static readonly IReadOnlyList<string> ProductionFactTables = new[] { "mission_attempt_facts", "preparation_claim_observations", "lane_state_transitions" };

        /// <summary>
        /// Cutoff for completed voyages and missions, read signals, events, released docks and finished
        /// merge entries. Null when operational expiry is disabled.
        /// </summary>
        public DateTime? RecordCutoffUtc { get; set; }

        /// <summary>
        /// Objective dispatch attempt events at or after this instant are kept whatever the record
        /// cutoff, so reconciliation can still find an attempt that never closed.
        /// </summary>
        public DateTime DispatchAttemptCutoffUtc { get; set; }

        /// <summary>
        /// Cutoff for the append-only production metric facts. Null when facts are kept forever.
        /// </summary>
        public DateTime? ProductionFactCutoffUtc { get; set; }

        /// <summary>
        /// Build the cutoffs for a run at <paramref name="nowUtc"/>.
        /// </summary>
        /// <param name="nowUtc">The run's clock.</param>
        /// <param name="dataRetentionDays">Operational record retention in days; 0 disables it.</param>
        /// <param name="productionFactRetentionDays">Production metric fact retention in days; 0 keeps facts forever.</param>
        /// <returns>The cutoffs.</returns>
        public static DataExpiryCutoffs FromRetention(DateTime nowUtc, int dataRetentionDays, int productionFactRetentionDays)
        {
            if (dataRetentionDays < 0) throw new ArgumentOutOfRangeException(nameof(dataRetentionDays), "Must be >= 0");
            if (productionFactRetentionDays < 0) throw new ArgumentOutOfRangeException(nameof(productionFactRetentionDays), "Must be >= 0");
            DateTime now = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : DateTime.SpecifyKind(nowUtc.ToUniversalTime(), DateTimeKind.Utc);
            return new DataExpiryCutoffs
            {
                RecordCutoffUtc = dataRetentionDays > 0 ? now.AddDays(-dataRetentionDays) : null,
                DispatchAttemptCutoffUtc = now - ObjectiveDispatchAdmission.ReconciliationLookBack,
                ProductionFactCutoffUtc = productionFactRetentionDays > 0 ? now.AddDays(-productionFactRetentionDays) : null
            };
        }
    }
}
