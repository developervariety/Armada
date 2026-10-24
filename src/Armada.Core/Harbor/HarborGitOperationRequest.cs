namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Structured inputs to fixed fork Git operations, with paths resolved by checkout identity.</summary>
    public sealed class HarborGitOperationRequest
    {
        /// <summary>Fixed fork Git service method.</summary>
        public HarborGitOperationEnum Operation { get; set; }

        /// <summary>Use the configured repository instead of the owned checkout.</summary>
        public bool UseRepository { get; set; }

        /// <summary>Use the grant’s configured primary landing checkout.</summary>
        public bool UseWorkingDirectory { get; set; }

        /// <summary>Another checkout under the same grant for structured worktree operations.</summary>
        public string? TargetCheckoutId { get; set; }

        /// <summary>Structured branch name.</summary>
        public string? BranchName { get; set; }

        /// <summary>Structured base branch or full commit.</summary>
        public string? BaseBranch { get; set; }

        /// <summary>Whether worktree creation is detached.</summary>
        public bool Detached { get; set; }

        /// <summary>Named configured Git remote.</summary>
        public string? RemoteName { get; set; }

        /// <summary>Pull request title.</summary>
        public string? Title { get; set; }

        /// <summary>Pull request body.</summary>
        public string? Body { get; set; }

        /// <summary>Recorded lock or ref-operation reason.</summary>
        public string? Reason { get; set; }

        /// <summary>Structured source ref.</summary>
        public string? SourceRef { get; set; }

        /// <summary>Structured destination ref.</summary>
        public string? DestinationRef { get; set; }

        /// <summary>Full commit identity.</summary>
        public string? CommitSha { get; set; }

        /// <summary>Expected old full commit for compare-and-set.</summary>
        public string? ExpectedOldSha { get; set; }

        /// <summary>Target branch of a local merge.</summary>
        public string? TargetBranch { get; set; }

        /// <summary>Commit or snapshot message.</summary>
        public string? CommitMessage { get; set; }

        /// <summary>Existing pull request identity.</summary>
        public string? PullRequestUrl { get; set; }

        /// <summary>Pinned revision for a read.</summary>
        public string? Revision { get; set; }

        /// <summary>Repo-relative path for a tracked-content operation.</summary>
        public string? RelativePath { get; set; }

        /// <summary>Bounded tracked-content search term.</summary>
        public string? Term { get; set; }

        /// <summary>Maximum commits or paths in a bounded result.</summary>
        public int MaxCount { get; set; } = 100;

        /// <summary>Maximum content samples.</summary>
        public int MaxSamples { get; set; } = 20;

        /// <summary>First requested source line.</summary>
        public int Line { get; set; } = 1;

        /// <summary>Maximum source lines.</summary>
        public int MaxLines { get; set; } = 100;

        /// <summary>Ancestor ref to verify.</summary>
        public string? AncestorRef { get; set; }

        /// <summary>Descendant ref to verify.</summary>
        public string? DescendantRef { get; set; }

        /// <summary>Full start commit for changed-path evidence.</summary>
        public string? StartCommit { get; set; }

        /// <summary>Full excluded commit for a comparison.</summary>
        public string? FromCommit { get; set; }

        /// <summary>Full included commit for a comparison.</summary>
        public string? ToCommit { get; set; }

        /// <summary>Bounded Git operation timeout.</summary>
        public int TimeoutMilliseconds { get; set; } = 120000;

        /// <summary>Maximum complete result size.</summary>
        public int OutputLimitBytes { get; set; } = 1024 * 1024;

        /// <summary>Symbolic repository HEAD target.</summary>
        public string? TargetRef { get; set; }

        /// <summary>Prefix limiting a branch or ref inventory.</summary>
        public string? RefPrefix { get; set; }

        /// <summary>Mission identity for a preservation snapshot.</summary>
        public string? MissionId { get; set; }

    }
}
