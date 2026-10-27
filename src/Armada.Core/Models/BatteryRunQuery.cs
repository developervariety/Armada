namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Filters and paging for battery runs.
    /// </summary>
    public class BatteryRunQuery
    {
        #region Public-Members

        /// <summary>
        /// Tenant filter, or null for every tenant.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Captain filter, or null.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Status filter, or null.
        /// </summary>
        public BatteryRunStatusEnum? Status { get; set; } = null;

        /// <summary>
        /// Page number (1-based).
        /// </summary>
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// Page size (1 to 1000).
        /// </summary>
        public int PageSize { get; set; } = 25;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryRunQuery()
        {
        }

        #endregion
    }
}
