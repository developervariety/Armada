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
        /// deobfuscator's decompiled output. Such a path is absent from the checkout for a reason
        /// that has nothing to do with the mission, so it must never be reported as new work.
        /// </summary>
        public bool IsExternalSourceTree { get; set; } = false;

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
