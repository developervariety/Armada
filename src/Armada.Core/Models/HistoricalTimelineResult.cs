namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// A page of historical timeline entries, with the timeline sources that could not be read.
    /// </summary>
    public class HistoricalTimelineResult : EnumerationResult<HistoricalTimelineEntry>
    {
        #region Public-Members

        /// <summary>
        /// Source types left out of this timeline because the configured database provider does not
        /// store their records, for example <c>Planning</c> on a provider without planning sessions.
        /// Empty when every source was read.
        /// </summary>
        public List<string> UnavailableSources { get; set; } = new List<string>();

        /// <summary>
        /// Source types that hold more records than the timeline reads from one source, for example
        /// <c>Request</c> on a server with a long request history. Only the newest records of each named
        /// source are in this timeline and in <see cref="EnumerationResult{T}.TotalRecords"/>; narrow the
        /// query with <c>fromUtc</c>/<c>toUtc</c> or an entity filter to reach older ones. Empty when every
        /// source was read in full.
        /// </summary>
        public List<string> TruncatedSources { get; set; } = new List<string>();

        #endregion
    }
}
