namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;

    /// <summary>
    /// Progress information for a voyage.
    /// </summary>
    public class VoyageProgress
    {
        #region Public-Members

        /// <summary>
        /// Voyage details.
        /// </summary>
        public Voyage Voyage { get; set; } = null!;

        /// <summary>
        /// Total missions in this voyage.
        /// </summary>
        public int TotalMissions { get; set; } = 0;

        /// <summary>
        /// Completed missions in this voyage.
        /// </summary>
        public int CompletedMissions { get; set; } = 0;

        /// <summary>
        /// Failed missions in this voyage.
        /// </summary>
        public int FailedMissions { get; set; } = 0;

        /// <summary>
        /// In-progress missions in this voyage.
        /// </summary>
        public int InProgressMissions { get; set; } = 0;

        /// <summary>
        /// Distinct vessel identifiers referenced by this voyage's missions.
        /// </summary>
        public List<string> VesselIds { get; set; } = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public VoyageProgress()
        {
        }

        #endregion
    }
}
