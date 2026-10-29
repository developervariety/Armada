namespace Armada.Core.Models
{
    using System;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Query parameters for skill APIs and storage.
    /// </summary>
    public class SkillQuery
    {
        #region Private-Members

        private int _PageSize = 10;

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
        /// Server-set visibility for a regular user: when set, only tenant-wide rows and rows owned by this user are
        /// returned, applied in the data source before counting. Never read from a request body.
        /// </summary>
        [JsonIgnore]
        public string? ViewerUserId { get; set; } = null;

        /// <summary>
        /// Category filter.
        /// </summary>
        public string? Category { get; set; } = null;

        /// <summary>
        /// Optional name/description search text.
        /// </summary>
        public string? Search { get; set; } = null;

        /// <summary>
        /// Active-state filter.
        /// </summary>
        public bool? Active { get; set; } = null;

        /// <summary>
        /// Page number.
        /// </summary>
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// Results per page (1 to 1000, default 10). Values outside the range are clamped, so a request can never ask for
        /// an unbounded page.
        /// </summary>
        public int PageSize
        {
            get => _PageSize;
            set => _PageSize = value < 1 ? 1 : (value > 1000 ? 1000 : value);
        }

        /// <summary>
        /// Created-after filter.
        /// </summary>
        public DateTime? FromUtc { get; set; } = null;

        /// <summary>
        /// Created-before filter.
        /// </summary>
        public DateTime? ToUtc { get; set; } = null;

        /// <summary>
        /// Zero-based offset.
        /// </summary>
        public int Offset => PageNumber <= 1 ? 0 : (PageNumber - 1) * PageSize;
    }
}
