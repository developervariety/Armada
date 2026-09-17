namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Groups of similar code chunks in one vessel's code index, with the coverage the comparison had.
    /// </summary>
    public class CodeDuplicateReport
    {
        #region Public-Members

        /// <summary>
        /// Vessel compared.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// False when no comparison ran; <see cref="UnavailableReason"/> and <see cref="Message"/> say why.
        /// An unavailable report never means "no duplicates".
        /// </summary>
        public bool Available { get; set; } = false;

        /// <summary>
        /// Machine-readable reason when <see cref="Available"/> is false.
        /// </summary>
        public string? UnavailableReason { get; set; } = null;

        /// <summary>
        /// Human-readable explanation when unavailable.
        /// </summary>
        public string? Message { get; set; } = null;

        /// <summary>
        /// Index freshness at the time of the comparison.
        /// </summary>
        public string Freshness { get; set; } = "Missing";

        /// <summary>
        /// Commit the index was built from.
        /// </summary>
        public string? IndexedCommitSha { get; set; } = null;

        /// <summary>
        /// True when the similarity comparison over embeddings ran; false when only identical content was compared.
        /// </summary>
        public bool SimilarityCompared { get; set; } = false;

        /// <summary>
        /// Similarity threshold applied.
        /// </summary>
        public double Threshold { get; set; } = 0;

        /// <summary>
        /// Minimum non-blank lines applied.
        /// </summary>
        public int MinLines { get; set; } = 0;

        /// <summary>
        /// What was compared and what was left out.
        /// </summary>
        public CodeDuplicateCoverage Coverage { get; set; } = new CodeDuplicateCoverage();

        /// <summary>
        /// Pairs at or above the threshold.
        /// </summary>
        public int PairCount { get; set; } = 0;

        /// <summary>
        /// Groups found before <see cref="CodeDuplicateRequest.MaxGroups"/> was applied.
        /// </summary>
        public int GroupCount { get; set; } = 0;

        /// <summary>
        /// True when pair collection stopped at its cap; the groups then cover only part of the index.
        /// </summary>
        public bool Truncated { get; set; } = false;

        /// <summary>
        /// Conditions that narrow what the result means, such as a stale index or no embeddings.
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>
        /// Groups ordered by member count, then highest pair similarity, largest first.
        /// </summary>
        public List<CodeDuplicateGroup> Groups { get; set; } = new List<CodeDuplicateGroup>();

        #endregion
    }
}
