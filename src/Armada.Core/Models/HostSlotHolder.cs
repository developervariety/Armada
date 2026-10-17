namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One request that holds a host-wide build and test slot.
    /// </summary>
    public class HostSlotHolder
    {
        #region Public-Members

        /// <summary>
        /// Key of the holder, such as a check run ID, or null when the holder is unnamed.
        /// </summary>
        public string? Key { get; set; } = null;

        /// <summary>
        /// What the holder runs.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// When the holder was granted its slot.
        /// </summary>
        public DateTime? SinceUtc { get; set; } = null;

        #endregion
    }
}
