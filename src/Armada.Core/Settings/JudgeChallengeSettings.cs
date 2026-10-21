namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Settings for the challenge round: before a Judge NEEDS_REVISION becomes a rescue, a read-only
    /// Challenger captain on a different model rules UPHOLD or REFUTE on each blocking finding, and only
    /// the upheld findings reach the rescue. A block whose every finding is refuted waits for an operator.
    /// </summary>
    public class JudgeChallengeSettings
    {
        #region Public-Members

        /// <summary>
        /// Global switch. Off keeps every Judge NEEDS_REVISION on the unchanged rescue path.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Vessels whose Judge verdicts are never challenged. A listed vessel keeps the unchanged rescue path.
        /// </summary>
        public List<string> ExcludedVesselIds
        {
            get => _ExcludedVesselIds;
            set => _ExcludedVesselIds = value ?? new List<string>();
        }

        /// <summary>
        /// A REFUTE below this confidence counts as UPHOLD. A wrong refute sends a real defect onward,
        /// while a wrong uphold costs one more revision, so the floor sits above an even call.
        /// Clamped to 0.5-1.0.
        /// </summary>
        public double RefuteMinimumConfidence
        {
            get => _RefuteMinimumConfidence;
            set => _RefuteMinimumConfidence = Math.Max(0.5, Math.Min(1.0, value));
        }

        /// <summary>
        /// Minutes a Challenger may take from dispatch to a terminal state. After that the challenge is
        /// unavailable and the rescue proceeds with every finding. Clamped to 10-1440.
        /// </summary>
        public int TimeoutMinutes
        {
            get => _TimeoutMinutes;
            set => _TimeoutMinutes = Math.Max(10, Math.Min(1440, value));
        }

        /// <summary>
        /// Most findings one challenge rules on. A review with more blocking findings is not challenged.
        /// Clamped to 1-50.
        /// </summary>
        public int MaxFindings
        {
            get => _MaxFindings;
            set => _MaxFindings = Math.Max(1, Math.Min(50, value));
        }

        /// <summary>
        /// Case-insensitive text markers that make a finding unchallengeable. A finding containing one is
        /// always upheld, whatever the Challenger rules. Use it for guardrails a model must never argue
        /// away. A finding that names one of the vessel's protected paths is always upheld as well.
        /// </summary>
        public List<string> UnchallengeableMarkers
        {
            get => _UnchallengeableMarkers;
            set => _UnchallengeableMarkers = value ?? new List<string>();
        }

        #endregion

        #region Private-Members

        private List<string> _ExcludedVesselIds = new List<string>();
        private double _RefuteMinimumConfidence = 0.70;
        private int _TimeoutMinutes = 180;
        private int _MaxFindings = 20;
        private List<string> _UnchallengeableMarkers = new List<string>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a vessel's Judge verdicts may be challenged.
        /// </summary>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <returns>True when the round is enabled and the vessel is not excluded.</returns>
        public bool AppliesTo(string? vesselId)
        {
            if (!Enabled || String.IsNullOrWhiteSpace(vesselId)) return false;
            foreach (string excluded in _ExcludedVesselIds)
            {
                if (String.Equals(excluded?.Trim(), vesselId.Trim(), StringComparison.Ordinal)) return false;
            }
            return true;
        }

        #endregion
    }
}
