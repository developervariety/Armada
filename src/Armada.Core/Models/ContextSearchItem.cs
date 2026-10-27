namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>A source-attributed result shared by context search backends.</summary>
    public sealed class ContextSearchItem
    {
        /// <summary>Stable identity within the source class.</summary>
        public string Id { get; set; } = "";

        /// <summary>Source class; a memory result never has rule authority.</summary>
        public ContextSearchSourceEnum Source { get; set; }

        /// <summary>Short recall hook or source title.</summary>
        public string Title { get; set; } = "";

        /// <summary>Complete returned chunk or record text.</summary>
        public string Text { get; set; } = "";

        /// <summary>Logical source path or native memory identifier.</summary>
        public string Reference { get; set; } = "";

        /// <summary>Source snapshot hash, native version or indexed commit.</summary>
        public string Version { get; set; } = "";

        /// <summary>Origin description or evidence pointer, when the source carries one.</summary>
        public string? Evidence { get; set; }

        /// <summary>Whether this item must be delivered independently of search limits.</summary>
        public bool Required { get; set; }

        /// <summary>Index freshness for code results; null for other sources.</summary>
        public string? Freshness { get; set; }

        /// <summary>Reciprocal source rank. Scores order recall, never authority.</summary>
        public double Score { get; set; }

        // Metadata contributes to lexical matching without repeating it in the returned text.
        internal string SearchMetadata { get; set; } = "";
    }
}
