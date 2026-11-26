namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>Refuses publication when Git proves the candidate omits the mission's captured work.</summary>
    public static class CapturedCommitLandingRule
    {
        /// <summary>Checks a source or integration ref without treating an unavailable ancestry read as a mismatch.</summary>
        /// <param name="git">Git ancestry reader.</param>
        /// <param name="repository">Repository containing the captured commit and candidate ref.</param>
        /// <param name="mission">Mission whose saved commit must survive landing.</param>
        /// <param name="candidate">Source or integration ref to check.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A named refusal only for a definite non-ancestor; otherwise null.</returns>
        public static async Task<string?> DescribeRefusalAsync(IGitService git, string repository,
            Mission mission, string candidate, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(mission.CommitHash)) return null;
            bool? contains = await git.TryIsAncestorAsync(repository, mission.CommitHash, candidate, token).ConfigureAwait(false);
            return contains == false ? "captured_commit_not_in_candidate: recorded commit " + mission.CommitHash
                + " is not an ancestor of " + candidate + "; preserve the source branch and captured commit before retrying landing." : null;
        }
    }
}
