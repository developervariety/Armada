namespace Armada.Runtimes.Harbor
{
    using System;

    /// <summary>Named failure for unsafe, missing, corrupt, or exhausted local Harbor journal state.</summary>
    public sealed class HarborJobJournalException : Exception
    {
        /// <summary>Create a journal failure with a stable reason code and diagnostic message.</summary>
        /// <param name="reasonCode">Stable failure code.</param>
        /// <param name="message">Safe diagnostic message.</param>
        /// <param name="innerException">Underlying failure, if present.</param>
        public HarborJobJournalException(string reasonCode, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            ReasonCode = String.IsNullOrWhiteSpace(reasonCode) ? "harbor_journal_failed" : reasonCode;
        }

        /// <summary>Stable failure code suitable for protocol or operator reporting.</summary>
        public string ReasonCode { get; }
    }
}
