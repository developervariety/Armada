namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Service for managing mission landing operations including retries and dedicated worktree merges.
    /// </summary>
    public interface ILandingService
    {
        /// <summary>
        /// Retry landing for a mission in LandingFailed status.
        /// Rebases the mission branch onto the current target branch head and re-runs landing.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="tenantId">Optional tenant ID for tenant-scoped reads. Null for system/admin context.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the retry succeeded, false if it failed (conflicts, etc.) or another retry for the
        /// mission is already in flight.</returns>
        Task<bool> RetryLandingAsync(string missionId, string? tenantId = null, CancellationToken token = default);

        /// <summary>
        /// Whether a landing retry for the mission is running now. Only one retry per mission runs at a time;
        /// <see cref="RetryLandingAsync"/> refuses a second one and returns false.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <returns>True while a retry for the mission is in flight.</returns>
        bool IsRetryInFlight(string missionId);

        /// <summary>
        /// Merge a mission branch into a target branch using a dedicated temporary integration worktree.
        /// </summary>
        /// <param name="vessel">Vessel containing repository paths.</param>
        /// <param name="mission">Mission being landed.</param>
        /// <param name="targetBranch">Target branch to land into.</param>
        /// <param name="sourceBranch">Optional mission branch override when the mission record does not carry it.</param>
        /// <param name="commitMessage">Optional merge commit message.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if merge and push succeeded; otherwise false.</returns>
        Task<bool> MergeInDedicatedWorktreeAsync(
            Vessel vessel,
            Mission mission,
            string targetBranch,
            string? sourceBranch = null,
            string? commitMessage = null,
            CancellationToken token = default);
    }
}
