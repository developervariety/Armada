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
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the retry succeeded, false if it failed (conflicts, etc.).</returns>
        Task<bool> RetryLandingAsync(string missionId, CancellationToken token = default);
    }
}
