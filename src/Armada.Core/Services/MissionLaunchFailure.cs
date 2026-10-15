namespace Armada.Core.Services
{
    using System;

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
