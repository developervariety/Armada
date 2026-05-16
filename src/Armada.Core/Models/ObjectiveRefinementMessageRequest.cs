namespace Armada.Core.Models
{
    /// <summary>
    /// Request payload for appending a refinement message.
    /// </summary>
    public class ObjectiveRefinementMessageRequest
    {
        /// <summary>
        /// Gets or sets the content.
        /// </summary>
        public string Content { get; set; } = String.Empty;
    }
}
