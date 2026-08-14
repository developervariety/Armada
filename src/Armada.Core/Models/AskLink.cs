namespace Armada.Core.Models
{
    /// <summary>
    /// A suggested navigation link returned by the Ask Armada assistant.
    /// </summary>
    public class AskLink
    {
        /// <summary>
        /// Human-readable label.
        /// </summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// Relative dashboard path (e.g. /captains).
        /// </summary>
        public string Href { get; set; } = string.Empty;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public AskLink()
        {
        }

        /// <summary>
        /// Instantiate with a label and href.
        /// </summary>
        /// <param name="label">Label.</param>
        /// <param name="href">Relative path.</param>
        public AskLink(string label, string href)
        {
            Label = label;
            Href = href;
        }
    }
}
