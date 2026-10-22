namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The live descendants of a captain's agent process, or the named reason they could not be read.
    /// An unreadable tree is never an empty one: a caller that cannot see the children must not treat
    /// the captain as having none.
    /// </summary>
    public sealed class CaptainProcessTreeRead
    {
        #region Public-Members

        /// <summary>Reason when the host exposes no process table to read.</summary>
        public const string ProcessTableUnavailableReason = "process_table_unavailable";

        /// <summary>Reason when the agent process is not in the process table.</summary>
        public const string RootNotFoundReason = "root_process_not_found";

        /// <summary>Reason when reading the process table failed.</summary>
        public const string ReadFailedReason = "read_failed";

        /// <summary>True when the tree was read.</summary>
        public bool Available { get; }

        /// <summary>Why the tree could not be read; null when <see cref="Available"/> is true.</summary>
        public string? UnavailableReason { get; }

        /// <summary>Human-readable detail for an unavailable read.</summary>
        public string? Detail { get; }

        /// <summary>Live descendants of the agent process, excluding the agent process itself and zombies.</summary>
        public IReadOnlyList<CaptainChildProcess> Descendants { get; }

        /// <summary>Start time in UTC of the agent process itself; null when the tree was not read.</summary>
        public DateTime? RootStartedUtc { get; }

        #endregion

        #region Constructors-and-Factories

        private CaptainProcessTreeRead(bool available, string? reason, string? detail, IReadOnlyList<CaptainChildProcess> descendants, DateTime? rootStartedUtc)
        {
            Available = available;
            UnavailableReason = reason;
            Detail = detail;
            Descendants = descendants;
            RootStartedUtc = rootStartedUtc;
        }

        /// <summary>A tree that was read.</summary>
        /// <param name="descendants">Live descendants.</param>
        /// <param name="rootStartedUtc">Start time of the agent process itself.</param>
        /// <returns>An available read.</returns>
        public static CaptainProcessTreeRead Read(IReadOnlyList<CaptainChildProcess> descendants, DateTime rootStartedUtc)
        {
            return new CaptainProcessTreeRead(true, null, null, descendants ?? new List<CaptainChildProcess>(), rootStartedUtc);
        }

        /// <summary>A tree that could not be read.</summary>
        /// <param name="reason">Named reason, such as <see cref="ProcessTableUnavailableReason"/>.</param>
        /// <param name="detail">Human-readable detail.</param>
        /// <returns>An unavailable read.</returns>
        public static CaptainProcessTreeRead Unavailable(string reason, string? detail)
        {
            return new CaptainProcessTreeRead(false, reason, detail, new List<CaptainChildProcess>(), null);
        }

        #endregion
    }
}
