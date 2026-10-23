namespace Armada.Runtimes
{
    using System;

    /// <summary>
    /// One agent process recorded by <see cref="HarborProcessLedger"/>.
    /// </summary>
    public class HarborProcessLedgerEntry
    {
        #region Public-Members

        /// <summary>
        /// The Harbor job the process belongs to.
        /// </summary>
        public string JobId { get; set; } = String.Empty;

        /// <summary>
        /// The operating-system process id.
        /// </summary>
        public int ProcessId { get; set; } = 0;

        /// <summary>
        /// When the process started (UTC), which tells it apart from a later process that reused the id.
        /// </summary>
        public DateTime StartedUtc { get; set; } = DateTime.MinValue;

        #endregion
    }
}
