namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A participant key that more than one session appears to use: within the presence window it
    /// posted or heartbeated under two or more names and switched back to a name it had already used.
    /// One rename switches names once and is not reported; two sessions on one key alternate.
    /// An addressed note or wake to a shared key reaches every session on it, and the presence row
    /// shows only the name that wrote last.
    /// </summary>
    public class CoordinationSharedParticipantKey
    {
        #region Public-Members

        /// <summary>
        /// The participant key in use under several names.
        /// </summary>
        public string ParticipantKey { get; set; } = String.Empty;

        /// <summary>
        /// Every name seen on the key within the window, most recently seen first.
        /// </summary>
        public List<string> DisplayNames
        {
            get => _DisplayNames;
            set => _DisplayNames = value ?? new List<string>();
        }

        /// <summary>
        /// Number of times the name changed between consecutive posts or heartbeats within the window.
        /// </summary>
        public int NameSwitches { get; set; } = 0;

        /// <summary>
        /// Most recent post or heartbeat on the key, in UTC.
        /// </summary>
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Operator-facing explanation and the fix.
        /// </summary>
        public string Reason { get; set; } = String.Empty;

        #endregion

        #region Private-Members

        private List<string> _DisplayNames = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationSharedParticipantKey()
        {
        }

        #endregion
    }
}
