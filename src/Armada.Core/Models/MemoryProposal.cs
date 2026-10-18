namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// A stored nomination of a durable lesson for the owner's external AI-Memory. The typed-decision
    /// model only nominates; it never writes AI-Memory and never dismisses. The store is the database
    /// because the AI-Memory folder is read-only to the admiral. Text fields are redacted before they
    /// are stored.
    /// </summary>
    public sealed class MemoryProposal
    {
        #region Public-Members

        /// <summary>Source value for a proposal nominated by the weekly papercut sweep.</summary>
        public const string SourcePapercutSweep = "papercut_sweep";

        /// <summary>Source value for a proposal nominated by the Recorder memory review.</summary>
        public const string SourceRecorderSeam = "recorder_seam";

        /// <summary>Maximum stored length of the source code.</summary>
        public const int MaximumSourceLength = 64;

        /// <summary>Maximum stored length of the title.</summary>
        public const int MaximumTitleLength = 512;

        /// <summary>Maximum stored length of the body.</summary>
        public const int MaximumBodyLength = 8000;

        /// <summary>Maximum stored length of the target hint.</summary>
        public const int MaximumTargetHintLength = 256;

        /// <summary>Maximum stored length of the operator name and the dismissal reason.</summary>
        public const int MaximumReasonLength = 2000;

        /// <summary>Unique identifier.</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable(Constants.MemoryProposalIdPrefix, 24);

        /// <summary>Tenant identifier.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>User identifier.</summary>
        public string? UserId { get; set; } = null;

        /// <summary>What nominated the proposal: <see cref="SourcePapercutSweep"/> or <see cref="SourceRecorderSeam"/>.</summary>
        public string Source { get; set; } = SourcePapercutSweep;

        /// <summary>
        /// A one-way SHA-256 fingerprint of the nominated subject (a papercut group key or a memory
        /// record), so a recurring subject is proposed once rather than on every pass.
        /// </summary>
        public string SourceKey { get; set; } = String.Empty;

        /// <summary>One-line subject, redacted.</summary>
        public string Title { get; set; } = String.Empty;

        /// <summary>The lesson text, redacted.</summary>
        public string Body { get; set; } = String.Empty;

        /// <summary>
        /// Plain-text hint of the suggested AI-Memory scope: <c>shared</c>, <c>repos/&lt;repo&gt;</c>, or
        /// <c>machine-notes</c>. The owner chooses the real folder.
        /// </summary>
        public string TargetHint { get; set; } = String.Empty;

        /// <summary>The model's confidence in [0, 1].</summary>
        public double Confidence
        {
            get => _Confidence;
            set => _Confidence = Double.IsNaN(value) ? 0.0 : Math.Max(0.0, Math.Min(1.0, value));
        }

        /// <summary>Armada record identifiers the proposal was nominated from.</summary>
        public List<string> RelatedRecordIds
        {
            get => _RelatedRecordIds;
            set => _RelatedRecordIds = value ?? new List<string>();
        }

        /// <summary>Review state.</summary>
        public MemoryProposalStateEnum State { get; set; } = MemoryProposalStateEnum.Open;

        /// <summary>Operator who dismissed the proposal.</summary>
        public string? DismissedBy { get; set; } = null;

        /// <summary>Why the operator dismissed the proposal.</summary>
        public string? DismissedReason { get; set; } = null;

        /// <summary>When the proposal was dismissed, in UTC.</summary>
        public DateTime? DismissedUtc { get; set; } = null;

        /// <summary>The operator who recorded the promotion.</summary>
        public string? PromotedBy { get; set; } = null;

        /// <summary>The full 40-character AI-Memory commit SHA that holds the promoted rule.</summary>
        public string? PromotedCommitSha { get; set; } = null;

        /// <summary>When the promotion was recorded, in UTC.</summary>
        public DateTime? PromotedUtc { get; set; } = null;

        /// <summary>Creation time in UTC.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Last update time in UTC.</summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a value is a full 40-character hexadecimal git commit SHA. A short SHA is refused,
        /// because it can become ambiguous as the repository grows.
        /// </summary>
        /// <param name="value">Candidate SHA.</param>
        /// <returns>True for exactly 40 hexadecimal characters, ignoring surrounding whitespace.</returns>
        public static bool IsFullCommitSha(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            string trimmed = value.Trim();
            if (trimmed.Length != 40) return false;
            foreach (char c in trimmed)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        #endregion

        #region Private-Members

        private double _Confidence = 0.0;
        private List<string> _RelatedRecordIds = new List<string>();

        #endregion
    }
}
