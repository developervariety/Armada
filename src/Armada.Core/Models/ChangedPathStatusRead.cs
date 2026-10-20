namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The result of reading every path that differs between two immutable commits, with its change status.
    /// <see cref="Available"/> is false when the read failed, and then <see cref="Entries"/> is empty and
    /// <see cref="FailureCode"/> names why: a failed read is never a verified empty change. An available read
    /// with <see cref="Complete"/> false returned only the first entries, and <see cref="IncompleteReason"/>
    /// names the bound that stopped it.
    /// </summary>
    public sealed class ChangedPathStatusRead
    {
        #region Public-Members

        /// <summary>A named commit is not a full object id.</summary>
        public const string InvalidCommitCode = "invalid_commit";

        /// <summary>The start commit is not present in the repository.</summary>
        public const string StartCommitMissingCode = "start_commit_missing";

        /// <summary>The final commit is not present in the repository.</summary>
        public const string FinalCommitMissingCode = "final_commit_missing";

        /// <summary>A git process passed its deadline and was stopped.</summary>
        public const string TimeoutCode = "timeout";

        /// <summary>The comparison printed more than the output limit, so no partial list is returned.</summary>
        public const string OutputLimitCode = "output_limit_exceeded";

        /// <summary>Git exited with an error other than a missing commit.</summary>
        public const string GitFailedCode = "git_failed";

        /// <summary>Git printed output that does not parse as a status list.</summary>
        public const string UnreadableOutputCode = "unreadable_output";

        /// <summary>The git seam in use does not read a repository.</summary>
        public const string NotSupportedCode = "not_supported";

        /// <summary>More paths changed than the caller asked for; only the first are returned.</summary>
        public const string PathLimitCode = "path_limit";

        /// <summary>True when the comparison was read.</summary>
        public bool Available { get; private set; } = false;

        /// <summary>True when <see cref="Entries"/> holds every changed path.</summary>
        public bool Complete { get; private set; } = false;

        /// <summary>Changed paths in the order git prints them. Empty when <see cref="Available"/> is false.</summary>
        public IReadOnlyList<ChangedPathEntry> Entries { get; private set; } = Array.Empty<ChangedPathEntry>();

        /// <summary>Number of changed paths git reported, when the read was available; null otherwise.</summary>
        public int? TotalCount { get; private set; } = null;

        /// <summary>Why the read failed, as one of the codes on this type; null when available.</summary>
        public string? FailureCode { get; private set; } = null;

        /// <summary>Why the read is incomplete, as one of the codes on this type; null when complete or unavailable.</summary>
        public string? IncompleteReason { get; private set; } = null;

        /// <summary>A short explanation of the failure or the bound; null when complete.</summary>
        public string? Message { get; private set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// A read that returned every changed path, or the first <paramref name="maxEntries"/> of them.
        /// </summary>
        /// <param name="entries">Every entry git printed, in order.</param>
        /// <param name="maxEntries">Most entries to keep; at least one.</param>
        /// <returns>An available read, incomplete when entries were dropped.</returns>
        public static ChangedPathStatusRead FromEntries(IReadOnlyList<ChangedPathEntry> entries, int maxEntries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            ChangedPathStatusRead read = new ChangedPathStatusRead
            {
                Available = true,
                TotalCount = entries.Count
            };
            if (entries.Count <= maxEntries)
            {
                read.Complete = true;
                read.Entries = entries;
                return read;
            }

            List<ChangedPathEntry> kept = new List<ChangedPathEntry>(maxEntries);
            for (int index = 0; index < maxEntries; index++) kept.Add(entries[index]);
            read.Entries = kept;
            read.IncompleteReason = PathLimitCode;
            read.Message = entries.Count + " paths changed; only the first " + maxEntries + " are returned.";
            return read;
        }

        /// <summary>
        /// A read that failed.
        /// </summary>
        /// <param name="code">One of the failure codes on this type.</param>
        /// <param name="message">Short explanation.</param>
        /// <returns>An unavailable read.</returns>
        public static ChangedPathStatusRead Unavailable(string code, string message)
        {
            return new ChangedPathStatusRead
            {
                FailureCode = String.IsNullOrWhiteSpace(code) ? GitFailedCode : code,
                Message = String.IsNullOrWhiteSpace(message) ? "The comparison could not be read." : message
            };
        }

        #endregion
    }
}
