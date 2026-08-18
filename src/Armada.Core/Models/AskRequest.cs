namespace Armada.Core.Models
{
    /// <summary>
    /// A request to the Ask Armada assistant.
    /// </summary>
    public class AskRequest
    {
        /// <summary>
        /// The natural-language message.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}
