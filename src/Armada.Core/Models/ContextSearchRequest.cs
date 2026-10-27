namespace Armada.Core.Models
{
    /// <summary>One scoped query over rules, working facts and indexed code.</summary>
    public sealed class ContextSearchRequest
    {
        /// <summary>Natural-language task or identifier to find.</summary>
        public string Query { get; set; } = "";

        /// <summary>Vessel to search. A mission credential always uses its own vessel.</summary>
        public string? VesselId { get; set; }

        /// <summary>Maximum optional results, clamped to 1 through 40.</summary>
        public int Limit { get; set; } = 20;

        /// <summary>UTF-8 text budget for optional results. Required rules are exempt.</summary>
        public int MaxBytes { get; set; } = 24000;

        /// <summary>Include existing code search. This never creates or refreshes an index.</summary>
        public bool IncludeCode { get; set; } = true;
    }
}
