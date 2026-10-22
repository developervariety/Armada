namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Outcome of resolving a record id that may be abbreviated: the id to use, and whether an abbreviation
    /// named more than one record the caller can see.
    /// </summary>
    public sealed class AbbreviatedIdResolution
    {
        #region Public-Members

        /// <summary>
        /// Id to use: the full id an abbreviation resolved to, else the id as given.
        /// </summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>
        /// True when the abbreviation named more than one record the caller can see; nothing is read or stored then.
        /// </summary>
        public bool Ambiguous { get; set; } = false;

        /// <summary>
        /// True when the id was an abbreviation and resolved to exactly one visible record.
        /// </summary>
        public bool Resolved { get; set; } = false;

        #endregion
    }
}
