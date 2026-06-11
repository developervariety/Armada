namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome mode of a [ARMADA:NEEDS-INPUT] marker emitted by a captain.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum NeedsInputModeEnum
    {
        /// <summary>
        /// Notify the orchestrator but continue the mission without blocking.
        /// </summary>
        Soft,

        /// <summary>
        /// Park the mission in WaitingForInput until a reply arrives.
        /// </summary>
        Block
    }

    /// <summary>
    /// Parsed result of a [ARMADA:NEEDS-INPUT soft|block] marker found in captain output.
    /// </summary>
    public class CaptainNeedsInputRequest
    {
        #region Public-Members

        /// <summary>
        /// Whether the marker was present and valid.
        /// </summary>
        public bool Found { get; set; } = false;

        /// <summary>
        /// True if the marker was present but could not be fully parsed.
        /// </summary>
        public bool Malformed { get; set; } = false;

        /// <summary>
        /// Requested blocking mode (Soft or Block).
        /// </summary>
        public NeedsInputModeEnum Mode { get; set; } = NeedsInputModeEnum.Soft;

        /// <summary>
        /// The question text that follows the marker.
        /// </summary>
        public string QuestionText { get; set; } = string.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CaptainNeedsInputRequest()
        {
        }

        #endregion
    }
}
