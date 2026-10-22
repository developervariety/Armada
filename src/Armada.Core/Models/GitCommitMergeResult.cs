namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Result of merging one commit into a worktree's current branch.
    /// </summary>
    public class GitCommitMergeResult
    {
        #region Public-Members

        /// <summary>
        /// What happened.
        /// </summary>
        public GitCommitMergeOutcomeEnum Outcome { get; set; } = GitCommitMergeOutcomeEnum.Failed;

        /// <summary>
        /// Files that conflicted, when the outcome is <see cref="GitCommitMergeOutcomeEnum.Conflicted"/>.
        /// </summary>
        public List<string> ConflictedFiles
        {
            get => _ConflictedFiles;
            set => _ConflictedFiles = value ?? new List<string>();
        }

        /// <summary>
        /// Git's explanation when the merge did not happen, or null.
        /// </summary>
        public string? Message { get; set; } = null;

        #endregion

        #region Private-Members

        private List<string> _ConflictedFiles = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public GitCommitMergeResult()
        {
        }

        /// <summary>
        /// Instantiate with an outcome and message.
        /// </summary>
        /// <param name="outcome">What happened.</param>
        /// <param name="message">Explanation, or null.</param>
        public GitCommitMergeResult(GitCommitMergeOutcomeEnum outcome, string? message = null)
        {
            Outcome = outcome;
            Message = String.IsNullOrWhiteSpace(message) ? null : message.Trim();
        }

        #endregion
    }
}
