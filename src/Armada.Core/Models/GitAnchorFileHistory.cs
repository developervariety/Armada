namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The recent commit history of one path named by a mission. A captain that receives this does
    /// not have to run git log to find the prior art on the file it is about to change.
    /// </summary>
    public class GitAnchorFileHistory
    {
        #region Public-Members

        /// <summary>
        /// Repository-relative path the history belongs to. Empty when unset.
        /// </summary>
        public string Path
        {
            get
            {
                return _Path;
            }
            set
            {
                _Path = value ?? "";
            }
        }

        /// <summary>
        /// Whether the path exists on the resolved revision. A false value is a fact worth stating:
        /// it tells the captain the file is new work rather than an edit, which is otherwise several
        /// turns of searching to establish.
        /// </summary>
        public bool ExistsOnRevision { get; set; } = false;

        /// <summary>
        /// The path exactly as the mission text named it, when that differs from <see cref="Path"/>.
        /// A mission commonly names a file by a suffix of its tracked path, so the resolver reports
        /// both: the name the captain will search for, and the path the repository actually tracks.
        /// Empty when the mission named the tracked path itself.
        /// </summary>
        public string RequestedPath
        {
            get
            {
                return _RequestedPath;
            }
            set
            {
                _RequestedPath = value ?? "";
            }
        }

        /// <summary>
        /// Whether the path names a read-only tree outside this repository, such as a sibling
        /// external tool's decompiled output. Such a path is absent from the checkout for a reason
        /// that has nothing to do with the mission, so it must never be reported as new work.
        /// </summary>
        public bool IsExternalSourceTree { get; set; } = false;

        /// <summary>
        /// The sibling checkout (a <c>../</c> path beside the dock) whose HEAD tracks this path, when this
        /// checkout does not. Empty when no sibling checkout was found to track it.
        /// </summary>
        public string SiblingCheckout
        {
            get
            {
                return _SiblingCheckout;
            }
            set
            {
                _SiblingCheckout = value ?? "";
            }
        }

        /// <summary>
        /// Sibling checkouts the vessel or the mission declares that could not be searched for this path,
        /// because they are not provisioned beside the dock or could not be read. A path this checkout does
        /// not track is absent only when this list is empty: a sibling that was not searched may hold it.
        /// Never null.
        /// </summary>
        public List<string> UnverifiedSiblingCheckouts
        {
            get
            {
                return _UnverifiedSiblingCheckouts;
            }
            set
            {
                _UnverifiedSiblingCheckouts = value ?? new List<string>();
            }
        }

        /// <summary>
        /// The number of tracked paths the named path matched when it matched more than one, so it
        /// resolves to none of them; zero otherwise. An ambiguous name is not an absent one, and the
        /// brief must not call it new work.
        /// </summary>
        public int MatchingPathCount { get; set; } = 0;

        /// <summary>
        /// Whether the text cites line numbers in this path. An absent path cited with lines is a stale
        /// citation of existing code, not a file to create.
        /// </summary>
        public bool CitedWithLines { get; set; } = false;

        /// <summary>
        /// Recent commits that touched the path, newest first. Never null.
        /// </summary>
        public List<GitAnchorCommit> Commits
        {
            get
            {
                return _Commits;
            }
            set
            {
                _Commits = value ?? new List<GitAnchorCommit>();
            }
        }

        #endregion

        #region Private-Members

        private string _Path = "";
        private string _RequestedPath = "";
        private string _SiblingCheckout = "";
        private List<string> _UnverifiedSiblingCheckouts = new List<string>();
        private List<GitAnchorCommit> _Commits = new List<GitAnchorCommit>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public GitAnchorFileHistory()
        {
        }

        #endregion
    }
}
