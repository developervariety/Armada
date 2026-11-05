namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>A scored Judge finding and its claimed required reference.</summary>
    public sealed class JudgeFinding
    {
        /// <summary>Unique finding label within this review.</summary>
        public string? Id { get; set; }
        /// <summary>Finding and supporting evidence.</summary>
        public string? Text { get; set; }
        /// <summary>Occurrence score from one to ten.</summary>
        public int? Frequency { get; set; }
        /// <summary>Impact score from one to ten.</summary>
        public int? Severity { get; set; }
        /// <summary>Unmet acceptance criterion, or null when none is claimed.</summary>
        [JsonRequired]
        public string? Criterion { get; set; }
        /// <summary>Violated memory rule, or null when none is claimed.</summary>
        [JsonRequired]
        public string? Rule { get; set; }
    }
}
