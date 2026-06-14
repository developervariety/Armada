namespace Armada.Core.Models
{
    /// <summary>
    /// Outcome of a landing-drain safety-net merge enqueue attempt.
    /// </summary>
    public enum SafetyNetEnqueueOutcomeEnum
    {
        /// <summary>Branch was enqueued to the merge queue.</summary>
        Enqueued,

        /// <summary>Branch was enqueued and flagged for deep review (predicate or audit gate).</summary>
        EnqueuedFlaggedForReview,

        /// <summary>Mission already has an active merge entry.</summary>
        AlreadyEnqueued,

        /// <summary>Mission has no branch name to enqueue.</summary>
        SkippedNoBranch
    }

    /// <summary>
    /// Result of <see cref="Services.Interfaces.IMergeQueueService.TrySafetyNetEnqueueAsync"/>.
    /// </summary>
    public sealed class SafetyNetEnqueueResult
    {
        #region Public-Members

        /// <summary>What happened during the safety-net enqueue attempt.</summary>
        public SafetyNetEnqueueOutcomeEnum Outcome { get; init; }

        /// <summary>Created or existing merge entry, when applicable.</summary>
        public MergeEntry? Entry { get; init; }

        /// <summary>Optional human-readable detail (predicate reason, etc.).</summary>
        public string? Detail { get; init; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public SafetyNetEnqueueResult(SafetyNetEnqueueOutcomeEnum outcome, MergeEntry? entry = null, string? detail = null)
        {
            Outcome = outcome;
            Entry = entry;
            Detail = detail;
        }

        #endregion
    }
}
