namespace Armada.Core.Models
{
    /// <summary>Input fields for creating or updating a mission action definition.</summary>
    public sealed class FleetActionUpsertRequest
    {
        /// <summary>Display name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Prompt template for each selected vessel.</summary>
        public string PromptTemplate { get; set; } = string.Empty;
        /// <summary>Optional pipeline identifier.</summary>
        public string? PipelineId { get; set; }
        /// <summary>Maximum concurrent targets, from one through eight.</summary>
        public int Concurrency { get; set; } = 1;
        /// <summary>Whether the definition accepts new runs.</summary>
        public bool Active { get; set; } = true;
    }
}
