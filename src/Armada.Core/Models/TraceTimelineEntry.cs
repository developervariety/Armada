namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One lifecycle event in a trace record's timeline.
    /// </summary>
    public sealed class TraceTimelineEntry
    {
        #region Public-Members

        /// <summary>Event type.</summary>
        public string Event { get; set; } = String.Empty;

        /// <summary>When the event was recorded.</summary>
        public DateTime Utc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
