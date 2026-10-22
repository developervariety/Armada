namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Consecutive failures to start a mission's captain, so assignment backs off instead of retrying every dispatch
    /// cycle, and the reason can be shown on the mission.
    /// </summary>
    public class MissionLaunchFailure
    {
        #region Public-Members

        /// <summary>
        /// Consecutive failed launches.
        /// </summary>
        public int Count { get; set; } = 0;

        /// <summary>
        /// Why the last launch failed.
        /// </summary>
        public string Reason { get; set; } = "";

        /// <summary>
        /// When the last launch failed (UTC).
        /// </summary>
        public DateTime LastUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Earliest time the mission is tried again (UTC).
        /// </summary>
        public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Captains that failed to start this mission. Assignment prefers any other eligible idle captain, so one broken
        /// captain is not picked again and again for the same mission.
        /// </summary>
        public HashSet<string> FailedCaptainIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public MissionLaunchFailure()
        {
        }

        #endregion
    }
}
