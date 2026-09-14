namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Durable evidence of one objective dispatch attempt. The attempt id is the holder of every
    /// admission lease the attempt owns, so a lease held by this id proves the attempt is alive and
    /// a lease held by anyone else proves it is not.
    /// </summary>
    public class ObjectiveDispatchAttemptRecord
    {
        /// <summary>
        /// Attempt identifier, also the holder name on the attempt's admission leases.
        /// </summary>
        public string AttemptId { get; set; } = String.Empty;

        /// <summary>
        /// Tenant that owns the admitted objectives.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Every objective admitted by the attempt, in lease order.
        /// </summary>
        public List<string> ObjectiveIds { get; set; } = new List<string>();

        /// <summary>
        /// Admission lease names held by the attempt, in acquisition order.
        /// </summary>
        public List<string> LeaseNames { get; set; } = new List<string>();

        /// <summary>
        /// Title of the voyage the attempt creates.
        /// </summary>
        public string? Title { get; set; } = null;

        /// <summary>
        /// Vessel the voyage targets, when known.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// When the attempt was admitted.
        /// </summary>
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Voyage the attempt created, once recorded.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// How the attempt ended, set on the closing record.
        /// </summary>
        public string? Outcome { get; set; } = null;
    }
}
