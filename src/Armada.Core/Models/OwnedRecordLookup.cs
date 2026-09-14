namespace Armada.Core.Models
{
    /// <summary>
    /// Result of finding an owned configuration record by name for another owner's work. It says
    /// how many same-name records were refused, so a caller can report a refusal instead of
    /// treating it as a missing record.
    /// </summary>
    /// <typeparam name="T">Record type.</typeparam>
    public sealed class OwnedRecordLookup<T> where T : class, IOwnedRecord
    {
        #region Public-Members

        /// <summary>
        /// The usable record, or null.
        /// </summary>
        public T? Record { get; }

        /// <summary>
        /// Number of records with the requested name that the owner may not use.
        /// </summary>
        public int RefusedCount { get; }

        /// <summary>
        /// True when records with the name exist but none may be used.
        /// </summary>
        public bool WasRefused => Record == null && RefusedCount > 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="record">The usable record, or null.</param>
        /// <param name="refusedCount">Number of same-name records the owner may not use.</param>
        public OwnedRecordLookup(T? record, int refusedCount)
        {
            Record = record;
            RefusedCount = refusedCount < 0 ? 0 : refusedCount;
        }

        #endregion
    }
}
