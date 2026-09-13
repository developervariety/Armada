namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Describes a local branch and its position relative to the vessel default branch.
    /// </summary>
    public class BranchInfo
    {
        /// <summary>Short branch name.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Whether this is the repository HEAD branch.</summary>
        public bool IsCurrent { get; set; }

        /// <summary>Whether this is the configured default branch.</summary>
        public bool IsDefault { get; set; }

        /// <summary>Short tip commit hash, when available.</summary>
        public string? CommitHash { get; set; }

        /// <summary>Tip commit subject, when available.</summary>
        public string? CommitSubject { get; set; }

        /// <summary>Tip commit date in UTC, when available.</summary>
        public DateTime? CommitDate { get; set; }

        /// <summary>Commits ahead of the default branch.</summary>
        public int Ahead { get; set; }

        /// <summary>Commits behind the default branch.</summary>
        public int Behind { get; set; }

        /// <summary>Reason divergence is unavailable, when the default ref cannot be compared.</summary>
        public string? DivergenceError { get; set; }
    }
}
