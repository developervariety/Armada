namespace Armada.Server
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Input to <see cref="DispatchContextPackStager.PrepareAsync"/>: the missions of one dispatch and how
    /// a failure to build their context packs is handled.
    /// </summary>
    public sealed class DispatchContextPackRequest
    {
        #region Public-Members

        /// <summary>
        /// Vessel the missions run on.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Dispatch-level code context mode (auto, off or force). A mission's own mode wins. Null means auto.
        /// </summary>
        public string? CodeContextMode { get; set; } = null;

        /// <summary>
        /// Optional token budget for each pack.
        /// </summary>
        public int? TokenBudget { get; set; } = null;

        /// <summary>
        /// Optional maximum ranked result count for each pack.
        /// </summary>
        public int? MaxResults { get; set; } = null;

        /// <summary>
        /// Missions to prepare. A staged pack is added to each mission's prestaged files; a mission whose pack
        /// cannot be staged gets a line in its description that says why.
        /// </summary>
        public List<MissionDescription> Missions
        {
            get => _Missions;
            set => _Missions = value ?? new List<MissionDescription>();
        }

        /// <summary>
        /// Dispatch path that asks for the packs (operator, scheduler, rescue). Recorded on every
        /// not-staged event.
        /// </summary>
        public string Source { get; set; } = "operator";

        /// <summary>
        /// When true, a failure the dispatch settings say must stop the dispatch (force mode, or a required
        /// pack) is returned as an error. When false the dispatch always continues and every mission whose
        /// pack could not be staged is recorded with the reason.
        /// </summary>
        public bool RefuseOnFailure { get; set; } = true;

        /// <summary>
        /// Tenant recorded on not-staged events.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User recorded on not-staged events.
        /// </summary>
        public string? UserId { get; set; } = null;

        #endregion

        #region Private-Members

        private List<MissionDescription> _Missions = new List<MissionDescription>();

        #endregion
    }
}
