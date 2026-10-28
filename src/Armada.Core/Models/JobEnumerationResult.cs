namespace Armada.Core.Models
{
    /// <summary>
    /// One page of background jobs, with the count of journal records that could not be read. A page shorter than the
    /// journal holds is never silent: <see cref="UnreadableJournalRecords"/> names how many records are missing from it.
    /// </summary>
    public class JobEnumerationResult : EnumerationResult<LongRunningJob>
    {
        #region Public-Members

        /// <summary>
        /// Number of job journal records, or journal listings, that could not be read and so are not in the list.
        /// </summary>
        public int UnreadableJournalRecords { get; set; } = 0;

        #endregion
    }
}
