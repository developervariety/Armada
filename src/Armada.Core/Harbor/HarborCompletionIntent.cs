namespace Armada.Core.Harbor
{
    using System;
    using Armada.Core.Enums;

    /// <summary>Durable computed instructions for finishing one confirmed Harbor mission completion.</summary>
    public sealed class HarborCompletionIntent
    {
        /// <summary>Harbor job that owns this exact completion.</summary>
        public string JobId { get; set; } = String.Empty;

        /// <summary>Computed retry outcome; recovery does not reclassify provider output.</summary>
        public HarborCompletionOutcomeEnum Outcome { get; set; } = HarborCompletionOutcomeEnum.Transient;

        /// <summary>Failure text shown on the pending mission.</summary>
        public string FailureReason { get; set; } = String.Empty;

        /// <summary>Machine reason used to record the retry fact.</summary>
        public string ReasonCode { get; set; } = String.Empty;

        /// <summary>Final captain state after cleanup.</summary>
        public CaptainStateEnum CaptainState { get; set; } = CaptainStateEnum.Idle;

        /// <summary>Captain quarantine expiry, when the computed outcome requires a timed hold.</summary>
        public DateTime? QuarantineUntilUtc { get; set; } = null;

        /// <summary>Captain quarantine reason, when the computed outcome requires a hold.</summary>
        public string? QuarantineReason { get; set; } = null;

        /// <summary>Planned mission recovery counter for a rerouted attempt.</summary>
        public int? RecoveryAttempts { get; set; } = null;

        /// <summary>Whether all planned mission and captain effects were applied.</summary>
        public bool Completed { get; set; } = false;

        /// <summary>Stable identifier for the append-only mission attempt fact.</summary>
        public string FactId { get; set; } = String.Empty;

        /// <summary>Stable identifier for the append-only lifecycle event.</summary>
        public string EventId { get; set; } = String.Empty;

        /// <summary>Stable identifier for the append-only captain signal.</summary>
        public string SignalId { get; set; } = String.Empty;

        /// <summary>Bounded JSON payload with immutable append-only event, fact and signal fields.</summary>
        public string Payload { get; set; } = String.Empty;
    }
}
