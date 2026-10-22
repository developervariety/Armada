namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Where a final integration slice stands against the rest of its voyage, from <see cref="IntegrationBarrier.EvaluateAsync"/>.
    /// </summary>
    public class IntegrationBarrierState
    {
        #region Public-Members

        /// <summary>
        /// Whether the mission is a final integration slice (or a rework of one) at all. When false, nothing else applies.
        /// </summary>
        public bool Applies { get; set; } = false;

        /// <summary>
        /// Whether the slice must keep waiting for the rest of the voyage.
        /// </summary>
        public bool Waiting { get; set; } = false;

        /// <summary>
        /// Why it is waiting, or null.
        /// </summary>
        public string? WaitingSummary { get; set; } = null;

        /// <summary>
        /// A mission in another slice that ended Failed or LandingFailed, or null. Integration does not run without it.
        /// </summary>
        public Mission? FailedMission { get; set; } = null;

        /// <summary>
        /// The first mission of each slice an operator cancelled. Those slices are left out of integration.
        /// </summary>
        public List<Mission> DroppedSlices
        {
            get => _DroppedSlices;
            set => _DroppedSlices = value ?? new List<Mission>();
        }

        /// <summary>
        /// Missions in the other slices whose commits integration starts from, oldest first, one per distinct commit.
        /// </summary>
        public List<Mission> SettledCommits
        {
            get => _SettledCommits;
            set => _SettledCommits = value ?? new List<Mission>();
        }

        #endregion

        #region Private-Members

        private List<Mission> _DroppedSlices = new List<Mission>();
        private List<Mission> _SettledCommits = new List<Mission>();

        #endregion
    }
}
