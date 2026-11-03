namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// The proof that a LandingFailed mission's work is already on its vessel's remote default branch. The landing
    /// sweep records it with a proposal; confirming the proposal reads the proof again before it completes the mission.
    /// </summary>
    public class LandingReconcileEvidence
    {
        #region Public-Members

        /// <summary>Mission the proof is about.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Vessel whose default branch holds the work.</summary>
        public string? VesselId { get; set; } = null;

        /// <summary>Commit on the default branch that proves the landing: the mission's own commit, or the trailer commit.</summary>
        public string EvidenceCommit { get; set; } = String.Empty;

        /// <summary>The work the proof compares with, for a patch-id proof: the mission's commit or the branch tip.</summary>
        public string? SourceCommit { get; set; } = null;

        /// <summary>Which kind of proof was found.</summary>
        public LandingEvidenceKindEnum EvidenceKind { get; set; } = LandingEvidenceKindEnum.CommitAncestry;

        /// <summary>Remote-tracking ref the proof was read from, for example <c>origin/main</c>.</summary>
        public string TargetRef { get; set; } = String.Empty;

        /// <summary>The mission's failure reason when the proof was found.</summary>
        public string? FailureReason { get; set; } = null;

        /// <summary>When the proof was found.</summary>
        public DateTime ProposedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>The operator who recorded the proof, or null when the landing sweep found it.</summary>
        public string? RecordedBy { get; set; } = null;

        /// <summary>The incident the operator's record answers, or null.</summary>
        public string? IncidentId { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// One sentence an operator reads next to the Needs You item.
        /// </summary>
        /// <returns>The proof, in words.</returns>
        public string Describe()
        {
            string commit = EvidenceCommit.Length > 12 ? EvidenceCommit.Substring(0, 12) : EvidenceCommit;
            string source = SourceCommit == null ? String.Empty : SourceCommit.Length > 12 ? SourceCommit.Substring(0, 12) : SourceCommit;
            string proof = EvidenceKind switch
            {
                LandingEvidenceKindEnum.CommitAncestry => "Its commit " + commit + " is already on " + TargetRef + ".",
                LandingEvidenceKindEnum.PatchId => "Commit " + commit + " on " + TargetRef + " carries the same patch as its commit " + source + ".",
                _ => "Commit " + commit + " on " + TargetRef + " carries its Armada-Mission-Id trailer."
            };
            return String.IsNullOrWhiteSpace(RecordedBy) ? proof : proof + " Recorded by " + RecordedBy.Trim() + ".";
        }

        #endregion
    }
}
