namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// A backup or restore did not complete. The message is a stable reason.
    /// </summary>
    public sealed class DatabaseBackupException : Exception
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="failureReason">Stable reason.</param>
        /// <param name="refused">True when the request was refused before any change; false for an operational failure.</param>
        /// <param name="innerException">Optional cause.</param>
        public DatabaseBackupException(string failureReason, bool refused, Exception? innerException = null)
            : base(String.IsNullOrWhiteSpace(failureReason) ? "database_backup_failed" : failureReason, innerException)
        {
            FailureReason = Message;
            Refused = refused;
        }

        /// <summary>
        /// Stable reason.
        /// </summary>
        public string FailureReason { get; }

        /// <summary>
        /// True when the request was refused before any change was made.
        /// </summary>
        public bool Refused { get; }
    }
}
