namespace Armada.Core.Harbor
{
    /// <summary>Per-job server decision for a runner resume proof.</summary>
    public sealed class HarborResumeJobResult
    {
        /// <summary>Server-issued job identifier.</summary>
        public string JobId { get; set; } = String.Empty;

        /// <summary>Server decision for this job.</summary>
        public HarborResumeDecisionEnum Decision { get; set; } = HarborResumeDecisionEnum.Reject;

        /// <summary>Persisted sequence from which output must be replayed.</summary>
        public long ReplayFromSequence { get; set; }

        /// <summary>Stable refusal reason, when rejected.</summary>
        public string? Reason { get; set; }
    }
}
