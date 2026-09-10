namespace Armada.Core.Models
{
    /// <summary>
    /// One explicit repository revision used during objective preparation.
    /// </summary>
    public class ObjectivePreparationAnchor
    {
        /// <summary>
        /// Vessel whose repository contains the revision.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Human-readable ref that was resolved.
        /// </summary>
        public string? Ref { get; set; } = null;

        /// <summary>
        /// Immutable commit resolved from the ref.
        /// </summary>
        public string? ResolvedCommit { get; set; } = null;
    }
}
