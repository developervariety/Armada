namespace Armada.Core.Services
{
    /// <summary>
    /// A typed refusal from the universal fleet-capacity admission gate.
    /// </summary>
    public sealed class FleetCapacityAdmissionException : InvalidOperationException
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        public FleetCapacityAdmissionException(
            string code,
            string message,
            int activeCount,
            int limit,
            string candidateVesselId,
            IReadOnlyList<string> laneMembers)
            : base(message)
        {
            Code = code;
            ActiveCount = activeCount;
            Limit = limit;
            CandidateVesselId = candidateVesselId;
            LaneMembers = laneMembers;
        }

        /// <summary>
        /// Stable machine-readable refusal code.
        /// </summary>
        public string Code { get; }

        /// <summary>
        /// Number of occupied units observed while the durable lease was held.
        /// </summary>
        public int ActiveCount { get; }

        /// <summary>
        /// Configured limit that refused the new work unit.
        /// </summary>
        public int Limit { get; }

        /// <summary>
        /// Vessel whose work was refused.
        /// </summary>
        public string CandidateVesselId { get; }

        /// <summary>
        /// Sorted transitive sibling lane evaluated for the candidate.
        /// </summary>
        public IReadOnlyList<string> LaneMembers { get; }
    }
}
