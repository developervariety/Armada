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

        #endregion
    }
}
