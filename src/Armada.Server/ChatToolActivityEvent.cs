namespace Armada.Server
{
    using System;

    /// <summary>
    /// A tool card update for a chat surface, built from a runtime tool activity record.
    /// </summary>
    public sealed class ChatToolActivityEvent
    {
        #region Public-Members

        /// <summary>
        /// Card identifier. A started call and its completion share one identifier.
        /// </summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>
        /// Tool name.
        /// </summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>
        /// Redacted primary argument, or null.
        /// </summary>
        public string? Arguments { get; set; } = null;

        /// <summary>
        /// "started" while the call runs, "completed" when it finished.
        /// </summary>
        public string Phase { get; set; } = "started";

        /// <summary>
        /// True for success, false for error or incomplete, null while running.
        /// </summary>
        public bool? Ok { get; set; } = null;

        #endregion
    }
}
