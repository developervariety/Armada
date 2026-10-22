namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// How the addressee of a coordination note was resolved. A note addressed to a name that is not
    /// a participant key, but that a participant key posted or heartbeated under recently, is
    /// delivered to that key: one such key receives the note under its key, and several such keys
    /// each read the note and each get a wake, so the read filter and the wake reach the key the
    /// session actually reads and acknowledges as.
    /// </summary>
    public class CoordinationAddressResolution
    {
        #region Public-Members

        /// <summary>
        /// The participant key the sender gave, trimmed; null for a broadcast note.
        /// </summary>
        public string? RequestedKey { get; set; } = null;

        /// <summary>
        /// The value the note is stored under: the one key that uses the given name, or the value as
        /// given when it is a key, a name several keys use, or a value nobody uses; null for a
        /// broadcast note.
        /// </summary>
        public string? ResolvedKey { get; set; } = null;

        /// <summary>
        /// Participant keys the note is delivered to, each of which gets a wake: the given key, the
        /// one key that uses the given name, or every key that uses it. Empty for a broadcast note.
        /// </summary>
        public List<string> DeliveredKeys { get; set; } = new List<string>();

        /// <summary>
        /// Sender-facing explanation when the addressee was resolved from a name, naming every key
        /// the note is delivered to; null when the given key was used as is.
        /// </summary>
        public string? Notice { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationAddressResolution()
        {
        }

        #endregion
    }
}
