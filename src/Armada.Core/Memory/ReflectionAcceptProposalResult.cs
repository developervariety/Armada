namespace Armada.Core.Memory
{
    using System.Collections.Generic;

    /// <summary>Outcome of applying a reviewed reflection memory proposal to the learned playbook.</summary>
    public sealed class ReflectionAcceptProposalResult
    {
        /// <summary>Stable error code when acceptance fails; null on success.</summary>
        public string? Error { get; set; }

        /// <summary>Optional structured details payload accompanying <see cref="Error"/> (offending entries, judge verdicts, etc.).</summary>
        public object? ErrorDetails { get; set; }

        /// <summary>Learned playbook row id after create or update.</summary>
        public string? PlaybookId { get; set; }

        /// <summary>Post-update version marker (ISO-8601 UTC timestamp from playbook LastUpdateUtc).</summary>
        public string? PlaybookVersion { get; set; }

        /// <summary>Markdown written to the learned playbook.</summary>
        public string? AppliedContent { get; set; }

        /// <summary>Wire-string mode of the dispatched mission (consolidate, reorganize, or consolidate-and-reorganize).</summary>
        public string? Mode { get; set; }

        /// <summary>Per-Judge sibling verdicts when the dispatched mission ran with dualJudge=true. Null otherwise.</summary>
        public List<JudgeVerdictRecord>? JudgeVerdicts { get; set; }

        /// <summary>v2-F1 pack-curate accept: ids of vessel_pack_hints rows created, modified, or disabled.</summary>
        public List<string>? AppliedHintIds { get; set; }

        /// <summary>v2-F1 pack-curate accept: non-blocking pack_hint_no_matches warnings from path validation.</summary>
        public List<object>? PathWarnings { get; set; }

        /// <summary>v2-F1 pack-curate accept: non-blocking pack_hint_conflict warnings from conflict detection.</summary>
        public List<object>? ConflictWarnings { get; set; }
    }

    /// <summary>One Judge sibling's verdict observation when dual-Judge was enabled on a reflection dispatch.</summary>
    public sealed class JudgeVerdictRecord
    {
        /// <summary>Owning mission identifier (msn_ prefix).</summary>
        public string MissionId { get; set; } = "";

        /// <summary>Captain identifier when known; null when the Judge sibling has not been assigned.</summary>
        public string? CaptainId { get; set; }

        /// <summary>Parsed verdict marker: PASS, FAIL, NEEDS_REVISION, or PENDING.</summary>
        public string Verdict { get; set; } = "PENDING";
    }
}
