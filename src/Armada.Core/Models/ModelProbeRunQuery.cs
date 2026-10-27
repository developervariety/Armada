namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Filters and paging for listing model probe runs.
    /// </summary>
    public class ModelProbeRunQuery
    {
        #region Public-Members

        /// <summary>
        /// Only runs of this tenant, or null for every tenant.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Only runs of this captain, or null.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Only runs that used this model endpoint, or null.
        /// </summary>
        public string? ModelEndpointId { get; set; } = null;

        /// <summary>
        /// Only runs with this status, or null.
        /// </summary>
        public ModelProbeRunStatusEnum? Status { get; set; } = null;

        /// <summary>
        /// Only runs created at or after this time, or null.
        /// </summary>
        public DateTime? FromUtc { get; set; } = null;

        /// <summary>
        /// 1-based page number. Values below 1 become 1.
        /// </summary>
        public int PageNumber
        {
            get => _PageNumber;
            set => _PageNumber = value < 1 ? 1 : value;
        }

        /// <summary>
        /// Page size, 1 to 1000. Out-of-range values are clamped.
        /// </summary>
        public int PageSize
        {
            get => _PageSize;
            set => _PageSize = value < 1 ? 1 : (value > 1000 ? 1000 : value);
        }

        #endregion

        #region Private-Members

        private int _PageNumber = 1;
        private int _PageSize = 100;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ModelProbeRunQuery()
        {
        }

        #endregion
    }
}
