namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The result of reading the paths a branch changes against its base. <see cref="Available"/> is
    /// false when the read failed; an empty <see cref="Paths"/> list with <see cref="Available"/> true
    /// is a verified empty change, never a read failure. A caller deciding work from the change set
    /// must treat an unavailable read as "every path may have changed".
    /// </summary>
    public sealed class ChangedPathsRead
    {
        #region Public-Members

        /// <summary>
        /// Prefix of every reason a caller logs or records when the changed paths are unavailable.
        /// </summary>
        public const string UnavailablePrefix = "changed_paths_unavailable";

        /// <summary>
        /// True when the changed paths were read.
        /// </summary>
        public bool Available { get; private set; } = false;

        /// <summary>
        /// Every repository-relative path the change touches. Empty when <see cref="Available"/> is false.
        /// </summary>
        public IReadOnlyList<string> Paths { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Why the read failed, when <see cref="Available"/> is false; null otherwise.
        /// </summary>
        public string? FailureReason { get; private set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// A successful read.
        /// </summary>
        /// <param name="paths">Changed paths; null or empty is a verified empty change.</param>
        /// <returns>An available read.</returns>
        public static ChangedPathsRead FromPaths(IReadOnlyList<string>? paths)
        {
            return new ChangedPathsRead
            {
                Available = true,
                Paths = paths ?? Array.Empty<string>()
            };
        }

        /// <summary>
        /// A read that failed.
        /// </summary>
        /// <param name="reason">Why the read failed.</param>
        /// <returns>An unavailable read.</returns>
        public static ChangedPathsRead Unavailable(string? reason)
        {
            return new ChangedPathsRead
            {
                Available = false,
                FailureReason = String.IsNullOrWhiteSpace(reason) ? "unknown" : reason
            };
        }

        /// <summary>
        /// The named reason a caller logs when this read is unavailable.
        /// </summary>
        /// <returns>Reason text.</returns>
        public string FormatReason()
        {
            return UnavailablePrefix + ": " + (FailureReason ?? "unknown");
        }

        #endregion
    }
}
