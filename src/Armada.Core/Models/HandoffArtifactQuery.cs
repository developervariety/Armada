namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Filters for listing handoff artifacts (newest first). Every filter is optional.
    /// </summary>
    public class HandoffArtifactQuery
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
        /// Page number, starting at 1.
        /// </summary>
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// Page size (1 to 1000).
        /// </summary>
        public int PageSize { get; set; } = 100;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public HandoffArtifactQuery()
        {
        }

        #endregion
    }
}
