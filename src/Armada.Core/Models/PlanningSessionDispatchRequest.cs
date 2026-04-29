namespace Armada.Core.Models
{
    /// <summary>
    /// Request payload for creating a voyage from a planning session.
    /// </summary>
    public class PlanningSessionDispatchRequest
    {
        /// <summary>
        /// Optional source message identifier.
        /// When omitted, Armada uses the latest non-empty assistant message.
        /// </summary>
        public string? MessageId { get; set; } = null;

        /// <summary>
        /// Optional voyage title override.
        /// </summary>
        public string? Title { get; set; } = null;

        /// <summary>
        /// Optional mission description override.
        /// </summary>
        public string? Description { get; set; } = null;
    }
}
