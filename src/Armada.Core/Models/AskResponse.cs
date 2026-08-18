namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// A response from the Ask Armada assistant.
    /// </summary>
    public class AskResponse
    {
        /// <summary>
        /// The reply text (markdown-friendly plain text).
        /// </summary>
        public string Reply { get; set; } = string.Empty;

        /// <summary>
        /// The kind of response.
        /// </summary>
        public AskResponseKindEnum Kind { get; set; } = AskResponseKindEnum.Unknown;

        /// <summary>
        /// Suggested navigation links relevant to the answer.
        /// </summary>
        public List<AskLink> Links { get; set; } = new List<AskLink>();
    }
}
