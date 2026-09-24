namespace Armada.Server
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// Outcome of an operator cancel of a merge entry.
    /// </summary>
    public sealed class MergeEntryCancellationResult
    {
        #region Public-Members

        /// <summary>
        /// True when the entry was cancelled.
        /// </summary>
        public bool Succeeded { get; private set; }

        /// <summary>
        /// Refusal code when refused; null on success.
        /// </summary>
        public string? Code { get; private set; }

        /// <summary>
        /// Refusal reason when refused; null on success.
        /// </summary>
        public string? Message { get; private set; }

        /// <summary>
        /// The cancelled entry, the unchanged finished entry, or null when no entry was found.
        /// </summary>
        public MergeEntry? Entry { get; private set; }

        /// <summary>
        /// True when the refusal is because the entry does not exist in the caller's scope.
        /// </summary>
        public bool NotFound => !Succeeded && String.Equals(Code, MergeEntryCancellation.NotFoundCode, StringComparison.Ordinal);

        #endregion

        #region Constructors-and-Factories

        private MergeEntryCancellationResult()
        {
        }

        /// <summary>
        /// A completed cancel.
        /// </summary>
        /// <param name="entry">Cancelled entry.</param>
        /// <returns>The result.</returns>
        public static MergeEntryCancellationResult Cancelled(MergeEntry entry)
        {
            return new MergeEntryCancellationResult { Succeeded = true, Entry = entry ?? throw new ArgumentNullException(nameof(entry)) };
        }

        /// <summary>
        /// A refused cancel. Nothing changed.
        /// </summary>
        /// <param name="entry">The entry, or null when none was found.</param>
        /// <param name="code">Refusal code.</param>
        /// <param name="message">Refusal reason.</param>
        /// <returns>The result.</returns>
        public static MergeEntryCancellationResult Refused(MergeEntry? entry, string code, string message)
        {
            return new MergeEntryCancellationResult { Succeeded = false, Entry = entry, Code = code, Message = message };
        }

        #endregion
    }
}
