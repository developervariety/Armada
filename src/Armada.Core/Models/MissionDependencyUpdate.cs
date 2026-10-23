namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One mission whose dependency moves to another mission, written as part of a mission chain.
    /// </summary>
    public class MissionDependencyUpdate
    {
        #region Public-Members

        /// <summary>
        /// The mission whose dependency changes.
        /// </summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>
        /// The mission it depends on from now on.
        /// </summary>
        public string DependsOnMissionId { get; set; } = String.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public MissionDependencyUpdate()
        {
        }

        /// <summary>
        /// Instantiate with the mission and its new dependency.
        /// </summary>
        /// <param name="missionId">The mission whose dependency changes.</param>
        /// <param name="dependsOnMissionId">The mission it depends on from now on.</param>
        public MissionDependencyUpdate(string missionId, string dependsOnMissionId)
        {
            MissionId = missionId ?? String.Empty;
            DependsOnMissionId = dependsOnMissionId ?? String.Empty;
        }

        #endregion
    }
}
