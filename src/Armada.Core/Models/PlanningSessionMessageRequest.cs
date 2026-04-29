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
    }
}
