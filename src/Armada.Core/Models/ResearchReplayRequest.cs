namespace Armada.Core.Models
{
    /// <summary>An explicit operator request to repeat every duty of a failed Research chain.</summary>
    public sealed class ResearchReplayRequest
    {
        /// <summary>Return the current plan without changing any record.</summary>
        public bool DryRun { get; set; }

        /// <summary>Exact immutable source commit, never a branch fallback.</summary>
        public string SourceCommit { get; set; } = System.String.Empty;

        /// <summary>Caller-selected idempotency key required when applying the plan.</summary>
        public string? RequestId { get; set; }

        /// <summary>Digest returned by the current preview; applying a stale plan is refused.</summary>
        public string? ExpectedFingerprint { get; set; }

        /// <summary>Server-captured opaque digest of current gate configuration, never raw settings.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? CapturedConfigurationDigest { get; set; }

        /// <summary>Server-captured prior instructions; HTTP and MCP input cannot supply this private field.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Collections.Generic.List<ResearchReplayInstruction> CapturedInstructions { get; set; }
            = new System.Collections.Generic.List<ResearchReplayInstruction>();
    }
}
