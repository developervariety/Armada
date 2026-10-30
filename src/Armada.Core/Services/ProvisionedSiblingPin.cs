namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// A declared sibling that an objective provisions at a pinned commit, as resolved for one piece of work.
    /// </summary>
    public sealed class ProvisionedSiblingPin
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="sibling">Declared sibling.</param>
        /// <param name="objectiveId">Objective that provisions the pin.</param>
        /// <param name="pinnedCommit">Pin as the objective states it.</param>
        /// <param name="repoPath">Sibling vessel repository, or null when none is readable.</param>
        /// <param name="commit">Full resolved commit, or null when the pin does not resolve.</param>
        public ProvisionedSiblingPin(SiblingRepo sibling, string objectiveId, string pinnedCommit, string? repoPath, string? commit)
        {
            Sibling = sibling ?? throw new ArgumentNullException(nameof(sibling));
            ObjectiveId = objectiveId ?? String.Empty;
            PinnedCommit = pinnedCommit ?? String.Empty;
            RepoPath = repoPath;
            Commit = commit;
        }

        /// <summary>Declared sibling.</summary>
        public SiblingRepo Sibling { get; }

        /// <summary>Objective that provisions the pin.</summary>
        public string ObjectiveId { get; }

        /// <summary>Pin as the objective states it.</summary>
        public string PinnedCommit { get; }

        /// <summary>Sibling vessel repository, or null when none is readable.</summary>
        public string? RepoPath { get; }

        /// <summary>Full resolved commit, or null when the pin does not resolve.</summary>
        public string? Commit { get; }
    }
}
