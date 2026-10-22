namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// How the addressee of a coordination note was resolved. A note addressed to a name that is not
    /// a participant key, but that exactly one participant key uses as its display or author name,
    /// is delivered to that key, so the stored note, the read filter and the wake all name the key
    /// the session actually reads and acknowledges as.
    /// </summary>
    public class CoordinationAddressResolution
    {
        #region Public-Members

        /// <summary>
        /// The participant key the sender gave, trimmed; null for a broadcast note.
        /// </summary>
        public string? RequestedKey { get; set; } = null;

        /// <summary>
        /// The participant key the note is stored and woken under; null for a broadcast note.
        /// </summary>
        public string? ResolvedKey { get; set; } = null;

        /// <summary>
        /// Sender-facing explanation when the addressee was resolved from a name or could not be
        /// resolved because several keys use the name; null when the given key was used as is.
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
