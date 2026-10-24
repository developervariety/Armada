namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Query parameters for deployment environment APIs and persistence.
    /// </summary>
    public class DeploymentEnvironmentQuery
    {
        #region Private-Members

        private int _PageSize = 25;

        #endregion

        /// <summary>
        /// Tenant scope or filter.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User scope or filter.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Vessel filter.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Environment kind filter.
        /// </summary>
        public EnvironmentKindEnum? Kind { get; set; } = null;

        /// <summary>
        /// Default-environment filter.
        /// </summary>
        public bool? IsDefault { get; set; } = null;

        /// <summary>
        /// Active-state filter.
        /// </summary>
        public bool? Active { get; set; } = null;

        /// <summary>
        /// Free-text name/description/source search.
        /// </summary>
        public string? Search { get; set; } = null;

        /// <summary>
        /// Page number.
        /// </summary>
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// Results per page (1 to 1000, default 25). Values outside the range are clamped, so a request can never ask for
        /// an unbounded page.
        /// </summary>
        public int PageSize
        {
            get => _PageSize;
            set => _PageSize = value < 1 ? 1 : (value > 1000 ? 1000 : value);
        }

        /// <summary>
        /// Zero-based offset.
        /// </summary>
        public int Offset => PageNumber <= 1 ? 0 : (PageNumber - 1) * PageSize;
    }
}
