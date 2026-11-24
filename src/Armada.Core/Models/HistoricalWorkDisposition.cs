namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;
    /// <summary>Exact-owner assertion bound to the graph, retained source and cancellation evidence.</summary>
    public sealed class HistoricalWorkDisposition
    {
        /// <summary>Explicit acceptance disposition. Missing and Accepted both refuse recovery.</summary>
        public HistoricalWorkDispositionKindEnum Kind { get; set; }
        /// <summary>Fingerprint returned by the current evidence preview.</summary>
        public string GraphFingerprint { get; set; } = String.Empty;
        /// <summary>Original cancelled mission that will receive the one-use permit.</summary>
        public string MissionId { get; set; } = String.Empty;
        /// <summary>Retained source ref, resolved again by the server.</summary>
        public string PreservedSourceRef { get; set; } = String.Empty;
        /// <summary>Exact retained commit. It must remain unlanded on the current target.</summary>
        public string PreservedSourceCommit { get; set; } = String.Empty;
        /// <summary>Latest exact-owner native cancellation event for the mission.</summary>
        public string CancellationEventId { get; set; } = String.Empty;
        /// <summary>Digest of retained native evidence that the owner used for this decision.</summary>
        public string NativeEvidenceSHA256 { get; set; } = String.Empty;
        /// <summary>Digest of retained count and charge evidence; no charge is reset.</summary>
        public string CountsEvidenceSHA256 { get; set; } = String.Empty;
        /// <summary>Bounded truthful owner reason. It is retained as an assertion, not called machine proof.</summary>
        public string Reason { get; set; } = String.Empty;
    }
}
