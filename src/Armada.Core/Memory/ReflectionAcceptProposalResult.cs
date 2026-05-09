namespace Armada.Core.Memory
{
    /// <summary>Outcome of applying a reviewed reflection memory proposal to the learned playbook.</summary>
    public sealed class ReflectionAcceptProposalResult
    {
        /// <summary>Stable error code when acceptance fails; null on success.</summary>
        public string? Error { get; set; }

        /// <summary>Learned playbook row id after create or update.</summary>
        public string? PlaybookId { get; set; }

        /// <summary>Post-update version marker (ISO-8601 UTC timestamp from playbook LastUpdateUtc).</summary>
        public string? PlaybookVersion { get; set; }

        /// <summary>Markdown written to the learned playbook.</summary>
        public string? AppliedContent { get; set; }
    }
}
