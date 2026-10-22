namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// A link from one trace record to a related record.
    /// </summary>
    public sealed class TraceLink
    {
        #region Public-Members

        /// <summary>Kind of the related record.</summary>
        public TraceEntityKindEnum Kind { get; set; } = TraceEntityKindEnum.Mission;

        /// <summary>Id of the related record.</summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>How the records relate, such as <c>voyage</c>, <c>parent</c>, <c>rescue_of</c> or <c>challenger_of</c>.</summary>
        public string Relation { get; set; } = String.Empty;

        #endregion
    }
}
