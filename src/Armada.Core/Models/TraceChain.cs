namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The linked records reached from one id: the requested record first, then every record it links to
    /// or that links to it, breadth first, up to a bound.
    /// </summary>
    public sealed class TraceChain
    {
        #region Public-Members

        /// <summary>Id as the caller gave it.</summary>
        public string RequestedId { get; set; } = String.Empty;

        /// <summary>Full id the request resolved to, or null when it resolved to none or to several.</summary>
        public string? ResolvedId { get; set; } = null;

        /// <summary>Full ids a short id matched when it matched more than one.</summary>
        public List<string> Candidates { get; set; } = new List<string>();

        /// <summary>Where the requested record was found: <c>table</c>, <c>live</c> (built from the source row and stored) or <c>none</c>.</summary>
        public string Source { get; set; } = "none";

        /// <summary>Records in the chain, the requested record first.</summary>
        public List<TraceDocument> Records { get; set; } = new List<TraceDocument>();

        /// <summary>Linked ids with no trace record and no source row.</summary>
        public List<string> MissingIds { get; set; } = new List<string>();

        /// <summary>Archive keys of missing linked records whose trace record is in the archive bucket.</summary>
        public List<string> ArchivedKeys { get; set; } = new List<string>();

        /// <summary>True when the chain reached its bound before it ran out of links.</summary>
        public bool Truncated { get; set; } = false;

        #endregion
    }
}
