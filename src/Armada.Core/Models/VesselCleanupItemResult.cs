namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Outcome of cleaning one selected vessel resource.
    /// </summary>
    public class VesselCleanupItemResult
    {
        /// <summary>
        /// Resource key.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Kind of resource, when the key still resolved.
        /// </summary>
        public VesselCleanupResourceTypeEnum? ResourceType { get; set; } = null;

        /// <summary>
        /// Display identity, when the key still resolved.
        /// </summary>
        public string? Identity { get; set; } = null;

        /// <summary>
        /// Outcome.
        /// </summary>
        public VesselCleanupItemStatusEnum Status { get; set; } = VesselCleanupItemStatusEnum.Skipped;

        /// <summary>
        /// Explanation of the outcome.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}
