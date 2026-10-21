namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// One participant's presence across every coordination room: the session once, with each
    /// room it was seen in.
    /// </summary>
    public class CoordinationPresenceEntry
    {
        #region Public-Members

        /// <summary>
        /// Stable participant key.
        /// </summary>
        public string ParticipantKey { get; set; } = String.Empty;

        /// <summary>
        /// Display name from the participant's most recent presence row.
        /// </summary>
        public string DisplayName { get; set; } = String.Empty;

        /// <summary>
        /// Most recent activity in any room, in UTC.
        /// </summary>
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Rooms the participant was seen in within the window, most recent first.
        /// </summary>
        public List<CoordinationPresenceRoom> Rooms
        {
            get => _Rooms;
            set => _Rooms = value ?? new List<CoordinationPresenceRoom>();
        }

        #endregion

        #region Private-Members

        private List<CoordinationPresenceRoom> _Rooms = new List<CoordinationPresenceRoom>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationPresenceEntry()
        {
        }

        #endregion
    }
}
