namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A durable-lesson candidate the D18 <c>memory_candidate</c> decision nominated from a papercut
    /// group. It is written as a proposal file for the owner to promote or discard; the model never
    /// writes memory itself. All text fields are redacted before they reach the file, because the
    /// file lands in the AI-Memory repository.
    /// </summary>
    public sealed class MemoryCandidateProposal
    {
        /// <summary>
        /// The originating papercut group key. Stable across weekly passes, so it also gives the
        /// proposal file a stable identity.
        /// </summary>
        public string GroupKey { get; set; } = "";

        /// <summary>
        /// The group's most recent sample title, redacted.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// The group's most recent sample detail, redacted.
        /// </summary>
        public string Detail { get; set; } = "";

        /// <summary>
        /// The papercut category.
        /// </summary>
        public string Category { get; set; } = "";

        /// <summary>
        /// Number of reports in the group.
        /// </summary>
        public int Count { get; set; } = 0;

        /// <summary>
        /// Number of distinct captains that reported it.
        /// </summary>
        public int DistinctCaptainCount { get; set; } = 0;

        /// <summary>
        /// The vessel context, redacted.
        /// </summary>
        public string Vessels { get; set; } = "";

        /// <summary>
        /// The model's durable-lesson score in [0, 1].
        /// </summary>
        public double DurableLesson { get; set; } = 0.0;

        /// <summary>
        /// The scope the model chose: <c>shared</c>, <c>repos/&lt;vessel&gt;</c>, or <c>machine</c>.
        /// </summary>
        public string Scope { get; set; } = "";

        /// <summary>
        /// When the proposal was created, in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The path the proposal was written to, or null when no writable proposals folder was
        /// configured (the nomination is then recorded as an event only).
        /// </summary>
        public string? ProposalPath { get; set; } = null;
    }
}
