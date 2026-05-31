namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Dispatch-ready code context pack.
    /// </summary>
    public class ContextPackResponse
    {
        #region Public-Members

        /// <summary>
        /// Index status used to build the pack.
        /// </summary>
        public CodeIndexStatus Status { get; set; } = new CodeIndexStatus();

        /// <summary>
        /// Mission goal.
        /// </summary>
        public string Goal { get; set; } = "";

        /// <summary>
        /// Markdown context pack.
        /// </summary>
        public string Markdown { get; set; } = "";

        /// <summary>
        /// Rough token estimate based on character length.
        /// </summary>
        public int EstimatedTokens { get; set; } = 0;

        /// <summary>
        /// Absolute path to the materialized context pack on the Admiral host.
        /// </summary>
        public string MaterializedPath { get; set; } = "";

        /// <summary>
        /// Suggested prestagedFiles entry for armada_dispatch.
        /// </summary>
        public List<PrestagedFile> PrestagedFiles { get; set; } = new List<PrestagedFile>();

        /// <summary>
        /// Evidence results included in the markdown.
        /// </summary>
        public List<CodeSearchResult> Results { get; set; } = new List<CodeSearchResult>();

        /// <summary>
        /// Distinct files contributed only by graph expansion.
        /// </summary>
        public List<string> GraphIncludedFiles { get; set; } = new List<string>();

        /// <summary>
        /// Identifiers of <c>vessel_pack_hints</c> rows that fired during pre-selection (v2-F1).
        /// Empty when no pack hints matched the goal text. Returned as an empty array (not null)
        /// for consistent caller handling.
        /// </summary>
        public List<string> MatchedHintIds { get; set; } = new List<string>();

        /// <summary>
        /// Non-blocking warnings emitted during pack assembly (v2-F1). Examples:
        /// "hard_include_truncated: forced-include files exceeded token budget; truncated by priority order".
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>
        /// Operator-facing quality metrics for this generated pack.
        /// </summary>
        public ContextPackMetrics Metrics { get; set; } = new ContextPackMetrics();

        /// <summary>
        /// Summarized version of the markdown context pack.
        /// </summary>
        public string? SummarizedMarkdown { get; set; } = null;

        /// <summary>
        /// True if the markdown was successfully summarized.
        /// </summary>
        public bool IsSummarized { get; set; } = false;

        #endregion
    }

    /// <summary>
    /// Lightweight quality metrics for a generated context pack. These are derived
    /// from existing response data so callers can inspect pack usefulness without
    /// additional database writes.
    /// </summary>
    public class ContextPackMetrics
    {
        #region Public-Members

        /// <summary>Number of search results included in the pack response.</summary>
        public int ResultCount { get; set; }

        /// <summary>Distinct repo-relative files represented by the included results.</summary>
        public int IncludedFileCount { get; set; }

        /// <summary>Distinct repo-relative files represented by the included results.</summary>
        public List<string> IncludedFiles { get; set; } = new List<string>();

        /// <summary>Number of vessel_pack_hints rows that matched the goal.</summary>
        public int MatchedHintCount { get; set; }

        /// <summary>Identifiers of vessel_pack_hints rows that matched the goal.</summary>
        public List<string> MatchedHintIds { get; set; } = new List<string>();

        /// <summary>Whether symbol-graph expansion contributed additional context.</summary>
        public bool GraphExpansionUsed { get; set; }

        /// <summary>Number of non-blocking warnings emitted during pack assembly.</summary>
        public int WarningCount { get; set; }

        /// <summary>Whether summarization was used for the materialized pack.</summary>
        public bool IsSummarized { get; set; }

        /// <summary>Number of prestagedFiles entries returned with the pack.</summary>
        public int PrestagedFileCount { get; set; }

        /// <summary>Estimated token count for the raw markdown pack.</summary>
        public int EstimatedTokens { get; set; }

        /// <summary>Fleet context packs: number of vessels considered.</summary>
        public int VesselCount { get; set; }

        /// <summary>True when the response was served from the baseline cache rather than generated synchronously.</summary>
        public bool CacheHit { get; set; }

        /// <summary>Cache key used to locate the cached pack (indexed commit SHA), or null when not a cache hit.</summary>
        public string? CacheKey { get; set; } = null;

        #endregion
    }
}
