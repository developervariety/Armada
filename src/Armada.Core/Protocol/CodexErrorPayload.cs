namespace Armada.Core.Protocol
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The <c>error</c> object of a Codex exec --json <c>turn.failed</c> event (<see cref="CodexStreamEvent.Error"/>).
    /// </summary>
    public class CodexErrorPayload
    {
        #region Public-Members

        /// <summary>
        /// The error message, or null.
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; } = null;

        #endregion
    }
}
