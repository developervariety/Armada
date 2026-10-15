namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Where a merge queue entry is processed: the Admiral's own clone of the vessel, or, for a mission whose dock was on
    /// a Harbor, that Harbor's repository (the mission branch exists only there), with git and the test command run
    /// through the Harbor.
    /// </summary>
    public class MergeQueueHost
    {
        #region Public-Members

        /// <summary>
        /// Repository the entry's branch is in.
        /// </summary>
        public string RepoPath { get; }

        /// <summary>
        /// Harbor that owns the repository, or null for the Admiral's host.
        /// </summary>
        public string? HarborId { get; }

        /// <summary>
        /// Whether the repository is on a Harbor.
        /// </summary>
        public bool IsHarbor => HarborId != null;

        /// <summary>
        /// Command executor for the Harbor, or null for the Admiral's host.
        /// </summary>
        public IHostCommandExecutor? Commands { get; }

        /// <summary>
        /// Folder the temporary integration worktrees are created in.
        /// </summary>
        public string IntegrationRoot { get; }

        /// <summary>
        /// Whether the host runs Windows (test commands go through cmd.exe there).
        /// </summary>
        public bool IsWindows { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="repoPath">Repository the entry's branch is in.</param>
        /// <param name="integrationRoot">Folder for temporary integration worktrees.</param>
        /// <param name="harborId">Harbor ID, or null for the Admiral's host.</param>
        /// <param name="commands">Harbor command executor, or null for the Admiral's host.</param>
        /// <param name="isWindows">Whether the host runs Windows.</param>
        /// <exception cref="ArgumentNullException">Thrown when a path is null or empty, or a Harbor has no executor.</exception>
        public MergeQueueHost(string repoPath, string integrationRoot, string? harborId, IHostCommandExecutor? commands, bool isWindows)
        {
            if (String.IsNullOrEmpty(repoPath)) throw new ArgumentNullException(nameof(repoPath));
            if (String.IsNullOrEmpty(integrationRoot)) throw new ArgumentNullException(nameof(integrationRoot));
            if (harborId != null && commands == null) throw new ArgumentNullException(nameof(commands));
            RepoPath = repoPath;
            IntegrationRoot = integrationRoot;
            HarborId = harborId;
            Commands = commands;
            IsWindows = isWindows;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// A path inside <see cref="IntegrationRoot"/>, joined with the host's separator.
        /// </summary>
        /// <param name="name">Folder name.</param>
        /// <returns>The path.</returns>
        public string IntegrationPath(string name)
        {
            if (!IsHarbor) return System.IO.Path.Combine(IntegrationRoot, name);
            char separator = IntegrationRoot.Contains('\\') && !IntegrationRoot.Contains('/') ? '\\' : '/';
            return IntegrationRoot.TrimEnd('/', '\\') + separator + name;
        }

        #endregion
    }
}
