namespace Armada.Core.Enums
{
    /// <summary>
    /// Lifecycle state for a release record.
    /// </summary>
    public enum ReleaseStatusEnum
    {
        /// <summary>
        /// Draft release not yet finalized.
        /// </summary>
        Draft,

        /// <summary>
        /// Release candidate ready for final review.
        /// </summary>
        Candidate,

        /// <summary>
        /// Successfully shipped release.
        /// </summary>
        Shipped,

        /// <summary>
        /// Failed release attempt.
        /// </summary>
        Failed,

        /// <summary>
        /// Release was rolled back after shipment.
        /// </summary>
        RolledBack
    }
}
