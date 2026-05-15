namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Normalized GitHub pull-request detail returned by Armada.
    /// </summary>
    public class GitHubPullRequestDetail
    {
        /// <summary>
        /// Repository full name (owner/repo).
        /// </summary>
        public string Repository { get; set; } = String.Empty;

        /// <summary>
        /// Pull-request number.
        /// </summary>
        public int Number { get; set; } = 0;

        /// <summary>
        /// Optional linked mission identifier.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Pull-request URL.
        /// </summary>
        public string Url { get; set; } = String.Empty;

        /// <summary>
        /// Human-facing title.
        /// </summary>
        public string Title { get; set; } = String.Empty;

        /// <summary>
        /// Optional body text.
        /// </summary>
        public string? Body { get; set; } = null;

        /// <summary>
        /// Current GitHub state string.
        /// </summary>
        public string State { get; set; } = String.Empty;

        /// <summary>
        /// Whether the pull request is a draft.
        /// </summary>
        public bool IsDraft { get; set; } = false;

        /// <summary>
        /// Whether the pull request has been merged.
        /// </summary>
        public bool IsMerged { get; set; } = false;

        /// <summary>
        /// GitHub mergeability state when available.
        /// </summary>
        public string? MergeableState { get; set; } = null;

        /// <summary>
        /// Derived summary of current review state.
        /// </summary>
        public string ReviewStatus { get; set; } = "NoReview";

        /// <summary>
        /// Base branch name.
        /// </summary>
        public string BaseRefName { get; set; } = String.Empty;

        /// <summary>
        /// Head branch name.
        /// </summary>
        public string HeadRefName { get; set; } = String.Empty;

        /// <summary>
        /// Latest head commit SHA.
        /// </summary>
        public string? HeadSha { get; set; } = null;

        /// <summary>
        /// Author login when available.
        /// </summary>
        public string? AuthorLogin { get; set; } = null;

        /// <summary>
        /// Merge actor login when available.
        /// </summary>
        public string? MergedByLogin { get; set; } = null;

        /// <summary>
        /// Requested reviewer logins.
        /// </summary>
        public List<string> RequestedReviewers { get; set; } = new List<string>();

        /// <summary>
        /// Label names.
        /// </summary>
        public List<string> Labels { get; set; } = new List<string>();

        /// <summary>
        /// Total changed files count.
        /// </summary>
        public int ChangedFiles { get; set; } = 0;

        /// <summary>
        /// Total additions count.
        /// </summary>
        public int Additions { get; set; } = 0;

        /// <summary>
        /// Total deletions count.
        /// </summary>
        public int Deletions { get; set; } = 0;

        /// <summary>
        /// Total commit count.
        /// </summary>
        public int CommitCount { get; set; } = 0;

        /// <summary>
        /// Review decisions attached to the pull request.
        /// </summary>
        public List<GitHubPullRequestReview> Reviews { get; set; } = new List<GitHubPullRequestReview>();

        /// <summary>
        /// Issue comments attached to the pull request.
        /// </summary>
        public List<GitHubPullRequestComment> Comments { get; set; } = new List<GitHubPullRequestComment>();

        /// <summary>
        /// Provider check statuses for the latest head commit.
        /// </summary>
        public List<GitHubPullRequestCheck> Checks { get; set; } = new List<GitHubPullRequestCheck>();

        /// <summary>
        /// Creation timestamp in UTC when available.
        /// </summary>
        public DateTime? CreatedUtc { get; set; } = null;

        /// <summary>
        /// Last update timestamp in UTC when available.
        /// </summary>
        public DateTime? UpdatedUtc { get; set; } = null;

        /// <summary>
        /// Merge timestamp in UTC when available.
        /// </summary>
        public DateTime? MergedUtc { get; set; } = null;
    }

    /// <summary>
    /// One GitHub pull-request review record.
    /// </summary>
    public class GitHubPullRequestReview
    {
        /// <summary>
        /// Reviewer login.
        /// </summary>
        public string? ReviewerLogin { get; set; } = null;

        /// <summary>
        /// Review state string.
        /// </summary>
        public string State { get; set; } = String.Empty;

        /// <summary>
        /// Optional review body.
        /// </summary>
        public string? Body { get; set; } = null;

        /// <summary>
        /// Submission timestamp in UTC when available.
        /// </summary>
        public DateTime? SubmittedUtc { get; set; } = null;
    }

    /// <summary>
    /// One GitHub pull-request issue comment.
    /// </summary>
    public class GitHubPullRequestComment
    {
        /// <summary>
        /// Comment author login.
        /// </summary>
        public string? AuthorLogin { get; set; } = null;

        /// <summary>
        /// Comment body.
        /// </summary>
        public string? Body { get; set; } = null;

        /// <summary>
        /// Comment URL.
        /// </summary>
        public string? Url { get; set; } = null;

        /// <summary>
        /// Creation timestamp in UTC when available.
        /// </summary>
        public DateTime? CreatedUtc { get; set; } = null;
    }

    /// <summary>
    /// One GitHub check-run summary attached to the pull request head commit.
    /// </summary>
    public class GitHubPullRequestCheck
    {
        /// <summary>
        /// Check name.
        /// </summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>
        /// Current status string.
        /// </summary>
        public string Status { get; set; } = String.Empty;

        /// <summary>
        /// Final conclusion string when available.
        /// </summary>
        public string? Conclusion { get; set; } = null;

        /// <summary>
        /// Optional provider details URL.
        /// </summary>
        public string? DetailsUrl { get; set; } = null;
    }
}
