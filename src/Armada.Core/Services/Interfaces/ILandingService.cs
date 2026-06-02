namespace Armada.Core.Services.Interfaces
{
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
        /// <returns>True if the retry succeeded, false if it failed (conflicts, etc.).</returns>
        Task<bool> RetryLandingAsync(string missionId, string? tenantId = null, CancellationToken token = default);

        /// <summary>
        /// Bounded automatic landing retry for target-branch drift. Fetches the latest target,
        /// rebases the mission branch onto it, and re-runs landing only when the rebase is clean
        /// (drift). A genuine content conflict aborts the rebase and does not consume a retry.
        /// Honors the MaxLandingRetries setting (0 disables auto-retry); the per-mission attempt
        /// count is tracked in memory and cleared on a terminal outcome (Complete or exhausted).
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="tenantId">Optional tenant ID for tenant-scoped reads. Null for system/admin context.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the auto-retry landed the mission, false otherwise (conflict, error, or bound reached).</returns>
        Task<bool> AutoRetryLandingAsync(string missionId, string? tenantId = null, CancellationToken token = default);
    }
}
