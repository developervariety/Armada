namespace Armada.Core.Models
{
    /// <summary>
    /// Request payload for generating a dispatch draft from a planning session.
    /// </summary>
    public class PlanningSessionSummaryRequest
    {
        /// <summary>
        /// Optional source message identifier.
        /// When omitted, Armada uses the latest non-empty assistant message.
        /// </summary>
        public string? MessageId { get; set; } = null;

        /// <summary>
        /// Optional preferred title override to preserve during summarization.
        /// </summary>
        public string? Title { get; set; } = null;
    }
}
