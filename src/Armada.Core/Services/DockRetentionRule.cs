namespace Armada.Core.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Whether a mission still works from its dock after its captain is gone. A mission whose work is
    /// produced but not yet landed, whose pull request is open, or that awaits operator review lands or
    /// is approved from that dock, so recovery cleanup that releases the captain must keep the dock.
    /// </summary>
    public static class DockRetentionRule
    {
        /// <summary>True when the mission still needs its dock.</summary>
        /// <param name="mission">The dock's mission; null needs nothing.</param>
        /// <returns>True when the dock must be kept.</returns>
        public static bool MissionStillUsesDock(Mission? mission)
        {
            if (mission == null) return false;
            return mission.Status == MissionStatusEnum.WorkProduced
                || mission.Status == MissionStatusEnum.PullRequestOpen
                || mission.Status == MissionStatusEnum.Review;
        }
    }
}
