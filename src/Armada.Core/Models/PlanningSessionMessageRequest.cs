namespace Armada.Core.Models
{
    /// <summary>
    /// Request payload for appending a user message to a planning session.
    /// </summary>
    public class PlanningSessionMessageRequest
    {
        /// <summary>
        /// User message content.
        /// </summary>
        public string Content { get; set; } = String.Empty;

        /// <summary>
        /// Whether to surface the model's reasoning ("thinking") for this turn, mirroring Ask Armada.
        /// </summary>
        public bool ShowThinking { get; set; } = false;

        /// <summary>
        /// Whether to stream the reply incrementally. When false the reply is broadcast once at the end.
        /// Defaults to true.
        /// </summary>
        public bool Stream { get; set; } = true;
    }
}
