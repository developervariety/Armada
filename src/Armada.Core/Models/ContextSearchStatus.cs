namespace Armada.Core.Models
{
    /// <summary>Whether one part of a unified query was searched.</summary>
    public sealed class ContextSearchStatus
    {
        /// <summary>Source group: rules, memory or code.</summary>
        public string Source { get; set; } = "";

        /// <summary>True when this source was searched, including a valid empty result.</summary>
        public bool Available { get; set; }

        /// <summary>Named reason when the source could not be searched.</summary>
        public string? Reason { get; set; }

        /// <summary>Retrieval mode, when available: bm25 or lexical.</summary>
        public string? SearchMode { get; set; }

        /// <summary>Existing code index freshness, including when it has no matching result.</summary>
        public string? Freshness { get; set; }
    }
}
