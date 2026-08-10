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
