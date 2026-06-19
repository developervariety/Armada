namespace Armada.Core.Memory
{
    using System;

    /// <summary>
    /// Configuration for pruning the canonical per-vessel learned-facts file.
    /// </summary>
    public sealed class LearnedFactsPruneOptions
    {
        #region Public-Members

        /// <summary>
        /// Maximum number of confidence-tagged fact entries to retain in the learned-facts file.
        /// Set to 0 to disable count-based pruning. Must be >= 0.
        /// </summary>
        public int MaxEntries
        {
            get => _MaxEntries;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(MaxEntries), "Must be >= 0");
                _MaxEntries = value;
            }
        }

        /// <summary>
        /// Reserved for future per-entry age-based pruning. The canonical file currently does not
        /// carry durable timestamps, so age-based pruning is not implemented in this version.
        /// </summary>
        public int MaxAgeDays
        {
            get => _MaxAgeDays;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(MaxAgeDays), "Must be >= 0");
                _MaxAgeDays = value;
            }
        }

        /// <summary>
        /// Jaccard 3-gram similarity threshold above which two entries are considered near-duplicate
        /// and merged. Set to 1.0 to require exact equality; set to 0.0 to merge everything.
        /// Must be in [0.0, 1.0]. Default 0.85.
        /// </summary>
        public double DedupeSimilarityThreshold
        {
            get => _DedupeSimilarityThreshold;
            set
            {
                if (value < 0.0 || value > 1.0) throw new ArgumentOutOfRangeException(nameof(DedupeSimilarityThreshold), "Must be in [0.0, 1.0]");
                _DedupeSimilarityThreshold = value;
            }
        }

        /// <summary>
        /// True when any pruning behavior is enabled. Dedupe is enabled when the threshold is
        /// below 1.0 (1.0 means only exact duplicates, which ApplyAsync already prevents).
        /// </summary>
        public bool Enabled => _MaxEntries > 0 || _MaxAgeDays > 0 || _DedupeSimilarityThreshold < 1.0;

        #endregion

        #region Private-Members

        private int _MaxEntries;
        private int _MaxAgeDays;
        private double _DedupeSimilarityThreshold = 0.85;

        #endregion
    }
}
