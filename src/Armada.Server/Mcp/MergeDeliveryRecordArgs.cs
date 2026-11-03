namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for recording delivery proof on a failed merge entry.
    /// </summary>
    public class MergeDeliveryRecordArgs
    {
        /// <summary>
        /// Merge entry ID (mrg_ prefix).
        /// </summary>
        public string EntryId { get; set; } = "";

        /// <summary>
        /// Commit that carries the entry's work on its target branch.
        /// </summary>
        public string Commit { get; set; } = "";

        /// <summary>
        /// Optional incident the record answers.
        /// </summary>
        public string? IncidentId { get; set; } = null;

        /// <summary>
        /// Proof to read: CommitAncestry (the default) or PatchId.
        /// </summary>
        public Armada.Core.Enums.LandingEvidenceKindEnum? ProofKind { get; set; } = null;
    }
}
