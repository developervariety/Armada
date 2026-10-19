namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// One line of work inside a voyage: a leaf of the voyage's dependency graph and every stage
    /// above it.
    /// </summary>
    public sealed class VoyageLane
    {
        #region Public-Members

        /// <summary>
        /// The mission at the end of the lane: no other stage of the voyage depends on it.
        /// </summary>
        public string LeafMissionId { get; }

        /// <summary>
        /// The stages of the lane, oldest first. Stages above a fan-out point (a planner whose plan
        /// split into several lines of work) belong to every lane below it.
        /// </summary>
        public IReadOnlyList<Mission> Missions { get; }

        /// <summary>
        /// The stages that belong to this lane only, oldest first. The single lane of a voyage that
        /// has not fanned out owns every stage.
        /// </summary>
        public IReadOnlyList<Mission> OwnMissions { get; }

        #endregion

        #region Private-Members

        private readonly HashSet<string> _Ids;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="leafMissionId">The lane's leaf mission.</param>
        /// <param name="missions">Every stage of the lane.</param>
        /// <param name="ownMissions">The stages that belong to this lane only.</param>
        public VoyageLane(string leafMissionId, IEnumerable<Mission> missions, IEnumerable<Mission> ownMissions)
        {
            LeafMissionId = leafMissionId ?? throw new ArgumentNullException(nameof(leafMissionId));
            Missions = (missions ?? throw new ArgumentNullException(nameof(missions))).ToList();
            OwnMissions = (ownMissions ?? throw new ArgumentNullException(nameof(ownMissions))).ToList();
            _Ids = new HashSet<string>(Missions.Select(mission => mission.Id), StringComparer.Ordinal);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a mission is a stage of this lane.
        /// </summary>
        /// <param name="missionId">A mission id. Null returns false.</param>
        /// <returns>True when the mission belongs to the lane.</returns>
        public bool Contains(string? missionId)
        {
            return !String.IsNullOrWhiteSpace(missionId) && _Ids.Contains(missionId);
        }

        /// <summary>
        /// Whether a mission belongs to this lane and no other.
        /// </summary>
        /// <param name="missionId">A mission id. Null returns false.</param>
        /// <returns>True when the mission is one of <see cref="OwnMissions"/>.</returns>
        public bool Owns(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            return OwnMissions.Any(mission => String.Equals(mission.Id, missionId, StringComparison.Ordinal));
        }

        #endregion
    }
}
