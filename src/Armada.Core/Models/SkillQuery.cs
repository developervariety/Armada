namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Query parameters for skill APIs and storage.
    /// </summary>
    public class SkillQuery
    {
        /// <summary>
        /// Tenant scope or filter.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User scope or filter.
        /// </summary>
        public string? UserId { get; set; } = null;

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
        /// Page size.
        /// </summary>
        public int PageSize { get; set; } = 25;

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
