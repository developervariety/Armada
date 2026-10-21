namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One room a participant was seen in, as part of a <see cref="CoordinationPresenceEntry"/>.
    /// </summary>
    public class CoordinationPresenceRoom
    {
        #region Public-Members

        /// <summary>
        /// Room key.
        /// </summary>
        public string RoomKey { get; set; } = String.Empty;

        /// <summary>
        /// Room display name.
        /// </summary>
        public string RoomName { get; set; } = String.Empty;

        /// <summary>
        /// Last activity in this room, in UTC.
        /// </summary>
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationPresenceRoom()
        {
        }

        #endregion
    }
}
