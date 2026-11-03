namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The outcome of an operator record of delivery proof on a failed merge entry.
    /// </summary>
    public sealed class MergeDeliveryRecordResult
    {
        #region Public-Members

        /// <summary>True when the proof was stored on the entry.</summary>
        public bool Recorded { get; }

        /// <summary>The refusal reason, one of the <see cref="MergeDeliveryRecorder"/> codes; null when recorded.</summary>
        public string? Code { get; }

        /// <summary>What happened, in words.</summary>
        public string Message { get; }

        /// <summary>The entry as stored after the call, or null when it was not found.</summary>
        public MergeEntry? Entry { get; }

        /// <summary>True when the refusal is an entry that does not exist in the caller's scope.</summary>
        public bool NotFound => String.Equals(Code, MergeDeliveryRecorder.NotFoundCode, StringComparison.Ordinal);

        #endregion

        #region Constructors-and-Factories

        private MergeDeliveryRecordResult(bool recorded, string? code, string message, MergeEntry? entry)
        {
            Recorded = recorded;
            Code = code;
            Message = message ?? String.Empty;
            Entry = entry;
        }

        /// <summary>A stored record.</summary>
        /// <param name="entry">The entry with its proof.</param>
        /// <param name="message">What was recorded.</param>
        /// <returns>Result.</returns>
        public static MergeDeliveryRecordResult Stored(MergeEntry entry, string message)
        {
            return new MergeDeliveryRecordResult(true, null, message, entry);
        }

        /// <summary>A refusal that left the entry unchanged.</summary>
        /// <param name="entry">The entry, or null when it was not found.</param>
        /// <param name="code">Refusal reason.</param>
        /// <param name="message">Why, in words.</param>
        /// <returns>Result.</returns>
        public static MergeDeliveryRecordResult Refused(MergeEntry? entry, string code, string message)
        {
            return new MergeDeliveryRecordResult(false, code, message, entry);
        }

        #endregion
    }
}
