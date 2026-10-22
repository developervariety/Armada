namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Result of saving a dock's uncommitted work to a recover branch in the vessel repository.
    /// </summary>
    public sealed class DockWorkSnapshot
    {
        #region Public-Members

        /// <summary>Outcome.</summary>
        public DockWorkSnapshotStatusEnum Status { get; }

        /// <summary>Full ref name that holds the work, such as <c>refs/heads/recover/msn_example-1</c>; null when none does.</summary>
        public string? RefName { get; }

        /// <summary>Commit the ref points at; null when none does.</summary>
        public string? CommitSha { get; }

        /// <summary>Human-readable outcome.</summary>
        public string Message { get; }

        /// <summary>True when a recover branch holds the work.</summary>
        public bool HasRef => RefName != null;

        #endregion

        #region Constructors-and-Factories

        private DockWorkSnapshot(DockWorkSnapshotStatusEnum status, string? refName, string? commitSha, string message)
        {
            Status = status;
            RefName = refName;
            CommitSha = commitSha;
            Message = message ?? String.Empty;
        }

        /// <summary>A new recover branch holds the work.</summary>
        /// <param name="refName">Full ref name.</param>
        /// <param name="commitSha">Snapshot commit.</param>
        /// <returns>The result.</returns>
        public static DockWorkSnapshot Saved(string refName, string commitSha)
        {
            return new DockWorkSnapshot(DockWorkSnapshotStatusEnum.Saved, refName, commitSha, "saved the dock's work to " + refName);
        }

        /// <summary>An existing recover branch already holds the same work.</summary>
        /// <param name="refName">Full ref name.</param>
        /// <param name="commitSha">Commit it points at.</param>
        /// <returns>The result.</returns>
        public static DockWorkSnapshot AlreadySaved(string refName, string commitSha)
        {
            return new DockWorkSnapshot(DockWorkSnapshotStatusEnum.AlreadySaved, refName, commitSha, "the dock's work is already saved in " + refName);
        }

        /// <summary>The dock holds nothing beyond its HEAD commit.</summary>
        /// <returns>The result.</returns>
        public static DockWorkSnapshot NoChanges()
        {
            return new DockWorkSnapshot(DockWorkSnapshotStatusEnum.NoChanges, null, null, "the dock has no uncommitted work");
        }

        /// <summary>The work could not be saved.</summary>
        /// <param name="message">Why.</param>
        /// <returns>The result.</returns>
        public static DockWorkSnapshot Failed(string message)
        {
            return new DockWorkSnapshot(DockWorkSnapshotStatusEnum.Failed, null, null, message);
        }

        #endregion
    }
}
