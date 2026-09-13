namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One recorded recovery event for a mission.
    /// </summary>
    public class MissionRecoveryEvent
    {
        #region Public-Members

        /// <summary>
        /// Event identifier.
        /// </summary>
        public string EventId { get; set; } = "";

        /// <summary>
        /// Event type.
        /// </summary>
        public string EventType { get; set; } = "";

        /// <summary>
        /// Redacted and bounded event message.
        /// </summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// UTC time the event was recorded.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
