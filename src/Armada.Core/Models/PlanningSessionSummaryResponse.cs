namespace Armada.Core.Models
{
    /// <summary>
    /// Server-generated dispatch draft extracted from a planning session.
    /// </summary>
    public class PlanningSessionSummaryResponse
    {
        /// <summary>
        /// Planning session identifier.
        /// </summary>
        public string SessionId { get; set; } = String.Empty;

        /// <summary>
        /// Source planning message identifier used to derive the draft.
        /// </summary>
        public string MessageId { get; set; } = String.Empty;

        /// <summary>
        /// Suggested voyage title.
        /// </summary>
        public string Title { get; set; } = String.Empty;

        /// <summary>
        /// Suggested mission description.
        /// </summary>
        public string Description { get; set; } = String.Empty;

        /// <summary>
        /// Summary method used to generate the draft.
        /// </summary>
        public string Method { get; set; } = "fallback";
    }
}
