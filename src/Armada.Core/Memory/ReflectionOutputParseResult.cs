namespace Armada.Core.Memory
{
    using System.Collections.Generic;

    /// <summary>Structured result from parsing reflection consolidation markdown output.</summary>
    public sealed class ReflectionOutputParseResult
    {
        /// <summary>Gets or sets whether the fenced-block contract succeeded.</summary>
        public ReflectionOutputParseVerdict Verdict { get; set; }

        /// <summary>Gets or sets inner markdown of reflections-candidate on success.</summary>
        public string CandidateMarkdown { get; set; } = "";

        /// <summary>Gets or sets raw reflections-diff fence body text (JSON intent) on success.</summary>
        public string ReflectionsDiffText { get; set; } = "";

        /// <summary>Gets or sets contract errors when Verdict is OutputContractViolation.</summary>
        public List<ReflectionOutputParseError> Errors { get; set; } = new List<ReflectionOutputParseError>();
    }
}
