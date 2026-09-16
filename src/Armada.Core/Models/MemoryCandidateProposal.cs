namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A durable-lesson candidate the D18 <c>memory_candidate</c> decision nominated, from a papercut
    /// group or from a Recorder memory record the D23 review flagged as belonging in AI-Memory. It is
    /// stored as a memory proposal for the owner to promote or an operator to dismiss; the model never
    /// writes memory itself. All text fields are redacted before they are stored.
    /// </summary>
    public sealed class MemoryCandidateProposal
    {
        /// <summary>
        /// What nominated the candidate: <see cref="MemoryProposal.SourcePapercutSweep"/> or
        /// <see cref="MemoryProposal.SourceRecorderSeam"/>.
        /// </summary>
        public string Source { get; set; } = MemoryProposal.SourcePapercutSweep;

        /// <summary>
        /// The originating subject key: a papercut group key, or a memory record key. Stable across
        /// passes, so a recurring subject is proposed once. It is stored only as a one-way fingerprint.
        /// </summary>
        public string GroupKey { get; set; } = "";

        /// <summary>
        /// The subject title, redacted.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// The subject detail, redacted.
        /// </summary>
        public string Detail { get; set; } = "";

        /// <summary>
        /// The papercut category, or the memory type for a Recorder candidate.
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
        /// The scope the model chose: <c>shared</c>, <c>repos/&lt;vessel&gt;</c>, or <c>machine-notes</c>.
        /// </summary>
        public string Scope { get; set; } = "";

        /// <summary>
        /// Armada record identifiers the candidate came from (mission and memory ids). They stay in the
        /// admiral database; they are never sent to the model.
        /// </summary>
        public List<string> RelatedRecordIds { get; set; } = new List<string>();

        /// <summary>
        /// When the candidate was created, in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The stored memory proposal identifier, or null when the write failed.
        /// </summary>
        public string? ProposalId { get; set; } = null;
    }
}
