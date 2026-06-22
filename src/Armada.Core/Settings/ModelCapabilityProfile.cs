namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Per-model capability profile used by the tier selector to rank idle captains within
    /// a tier by best fit for an optional capability hint. All dimension scores are integers
    /// clamped to [0, 100]; higher always means more of the named quality, except Cost where
    /// higher means more expensive. Profiles are stored as config data in
    /// ModelTierSettings.ModelCapabilityProfiles, keyed by concrete model name, and are
    /// never referenced by name inside routing logic.
    /// </summary>
    public class ModelCapabilityProfile
    {
        #region Public-Members

        /// <summary>
        /// Richness and readability of structured telemetry produced by this model.
        /// Higher scores indicate more diagnostically useful, structured log output that
        /// supports audit trails and incident investigation. Clamped to [0, 100].
        /// </summary>
        public int TelemetryRichness
        {
            get => _TelemetryRichness;
            set => _TelemetryRichness = Math.Max(0, Math.Min(100, value));
        }

        /// <summary>
        /// Suitability for audit-grade reasoning and cross-repository analysis. Higher
        /// scores indicate stronger correctness, traceability, and depth of reasoning
        /// across large or multi-file contexts. Clamped to [0, 100].
        /// </summary>
        public int AuditReasoningFit
        {
            get => _AuditReasoningFit;
            set => _AuditReasoningFit = Math.Max(0, Math.Min(100, value));
        }

        /// <summary>
        /// Throughput efficiency for repetitive, mechanical coding tasks where volume and
        /// speed matter more than deep reasoning or rich diagnostics. Higher scores indicate
        /// faster, higher-volume mechanical execution. Clamped to [0, 100].
        /// </summary>
        public int MechanicalThroughput
        {
            get => _MechanicalThroughput;
            set => _MechanicalThroughput = Math.Max(0, Math.Min(100, value));
        }

        /// <summary>
        /// Relative cost score for this model. Higher values mean higher per-token or
        /// per-request cost. Available as a profile dimension operators can map capability
        /// hints to via CapabilityHintDimensionMap. Clamped to [0, 100].
        /// </summary>
        public int Cost
        {
            get => _Cost;
            set => _Cost = Math.Max(0, Math.Min(100, value));
        }

        #endregion

        #region Private-Members

        private int _TelemetryRichness = 50;
        private int _AuditReasoningFit = 50;
        private int _MechanicalThroughput = 50;
        private int _Cost = 50;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with neutral mid-range scores (50) across all capability dimensions.
        /// </summary>
        public ModelCapabilityProfile()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns the score for the named capability dimension. Matching is case-insensitive.
        /// Returns -1 when the dimension name is null, empty, whitespace, or not recognized.
        /// </summary>
        /// <param name="dimensionName">
        /// Dimension to query: TelemetryRichness, AuditReasoningFit, MechanicalThroughput, or Cost.
        /// </param>
        public int GetDimensionScore(string? dimensionName)
        {
            if (String.IsNullOrWhiteSpace(dimensionName)) return -1;
            if (String.Equals(dimensionName, "TelemetryRichness", StringComparison.OrdinalIgnoreCase))
                return _TelemetryRichness;
            if (String.Equals(dimensionName, "AuditReasoningFit", StringComparison.OrdinalIgnoreCase))
                return _AuditReasoningFit;
            if (String.Equals(dimensionName, "MechanicalThroughput", StringComparison.OrdinalIgnoreCase))
                return _MechanicalThroughput;
            if (String.Equals(dimensionName, "Cost", StringComparison.OrdinalIgnoreCase))
                return _Cost;
            return -1;
        }

        #endregion
    }
}
