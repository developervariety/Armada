namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Filters for listing stage outcomes (newest first). Every filter is optional.
    /// </summary>
    public class StageOutcomeQuery
    {
        #region Public-Members

        /// <summary>
        /// Tenant, or null for every tenant.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Mission, or null.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Voyage, or null.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Vessel, or null.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Captain, or null.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Persona (exact, case-insensitive), or null.
        /// </summary>
        public string? Persona { get; set; } = null;

        /// <summary>
        /// Model (exact, case-insensitive), or null.
        /// </summary>
        public string? Model { get; set; } = null;

        /// <summary>
        /// Suspected cause, or null.
        /// </summary>
        public FailureCauseEnum? SuspectedCause { get; set; } = null;

        /// <summary>
        /// Cause source, or null.
        /// </summary>
        public FailureCauseSourceEnum? CauseSource { get; set; } = null;

        /// <summary>
        /// Disposition, or null.
        /// </summary>
        public StageDispositionEnum? Disposition { get; set; } = null;

        /// <summary>
        /// Evaluation attempts only (true), production only (false), or both (null).
        /// </summary>
        public bool? IsEvaluation { get; set; } = null;

        /// <summary>
        /// Ended at or after (UTC), or null.
        /// </summary>
        public DateTime? FromUtc { get; set; } = null;

        /// <summary>
        /// Ended before (UTC), or null.
        /// </summary>
        public DateTime? ToUtc { get; set; } = null;

        /// <summary>
        /// Page number, starting at 1.
        /// </summary>
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// Page size, 1 to 1000.
        /// </summary>
        public int PageSize { get; set; } = 100;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public StageOutcomeQuery()
        {
        }

        #endregion
    }
}
