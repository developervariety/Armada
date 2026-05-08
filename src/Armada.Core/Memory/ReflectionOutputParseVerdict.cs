namespace Armada.Core.Memory
{
    /// <summary>Outcome of parsing a reflection consolidation AgentOutput against the fenced-block contract.</summary>
    public enum ReflectionOutputParseVerdict
    {
        /// <summary>Exactly one reflections-candidate and one reflections-diff block; fences closed; EvidenceConfidence OK when surfaced.</summary>
        Success,

        /// <summary>Missing/duplicate fences, bad structure, invalid EvidenceConfidence, or other contract breach.</summary>
        OutputContractViolation,
    }
}
