namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The result of sorting a brief's ranked memory leaves into read-first and reference material.
    /// A sort only reorders what the captain reads first; every leaf is still delivered in full.
    /// </summary>
    public sealed class ContextLeafSort
    {
        #region Public-Members

        /// <summary>Topics of the ranked leaves to deliver as reference material. Empty keeps every leaf read-first.</summary>
        public HashSet<string> ReferenceTopics { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Short outcome of the sort, recorded on the brief telemetry.</summary>
        public string Outcome { get; set; } = String.Empty;

        #endregion
    }
}
