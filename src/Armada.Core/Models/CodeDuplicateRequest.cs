namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Request to find groups of similar code chunks within one vessel's code index.
    /// </summary>
    public class CodeDuplicateRequest
    {
        #region Public-Members

        /// <summary>
        /// Vessel whose index is compared.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Cosine similarity at or above which two chunks are paired. Null uses
        /// <c>codeIndex.duplicateSimilarityThreshold</c>. Clamped to [0.5 .. 1.0].
        /// </summary>
        public double? Threshold { get; set; } = null;

        /// <summary>
        /// Minimum non-blank lines a chunk needs to be compared. Null uses
        /// <c>codeIndex.duplicateMinLines</c>.
        /// </summary>
        public int? MinLines { get; set; } = null;

        /// <summary>
        /// Optional repo-relative path prefix; only chunks under it are compared.
        /// </summary>
        public string? PathPrefix { get; set; } = null;

        /// <summary>
        /// Optional language filter, e.g. csharp.
        /// </summary>
        public string? Language { get; set; } = null;

        /// <summary>
        /// Path fragments to leave out of the comparison, matched like <c>codeIndex.excludedPathFragments</c>.
        /// </summary>
        public List<string> ExcludePathFragments { get; set; } = new List<string>();

        /// <summary>
        /// Maximum groups returned, largest first. Clamped to [1 .. 500]. Default 50.
        /// </summary>
        public int MaxGroups { get; set; } = 50;

        /// <summary>
        /// Include each member's chunk content.
        /// </summary>
        public bool IncludeContent { get; set; } = false;

        #endregion
    }
}
