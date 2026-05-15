namespace Armada.Core.Models
{
    /// <summary>
    /// Request payload for generating or selecting a refinement summary.
    /// </summary>
    public class ObjectiveRefinementSummaryRequest
    {
        /// <summary>
        /// Gets or sets the message identifier.
        /// </summary>
        public string? MessageId { get; set; } = null;
    }
}
