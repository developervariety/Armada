namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One bounded, evidence-backed claim prepared before objective dispatch.
    /// </summary>
    public class ObjectivePreparationClaim
    {
        /// <summary>
        /// Stable identifier used to reverify one claim without replacing its siblings.
        /// </summary>
        public string Id { get; set; } = "opc_" + Guid.NewGuid().ToString("N");

        /// <summary>
        /// Claim classification used by the shared brief renderer.
        /// </summary>
        public ObjectivePreparationClaimKindEnum Kind { get; set; } = ObjectivePreparationClaimKindEnum.SourcePath;

        /// <summary>
        /// Concise verified statement.
        /// </summary>
        public string Text { get; set; } = String.Empty;

        /// <summary>
        /// Evidence paths or links that support the statement.
        /// </summary>
        public List<string> EvidenceLinks { get; set; } = new List<string>();

        /// <summary>
        /// Repository anchors whose change requires this claim to be checked again.
        /// </summary>
        public ObjectivePreparationDependencyEnum DependsOn { get; set; } = ObjectivePreparationDependencyEnum.None;

        /// <summary>
        /// Current verification state.
        /// </summary>
        public ObjectivePreparationClaimStateEnum State { get; set; } = ObjectivePreparationClaimStateEnum.Verified;

        /// <summary>
        /// Last successful verification timestamp in UTC.
        /// </summary>
        public DateTime? VerifiedUtc { get; set; } = null;

        /// <summary>
        /// Timestamp in UTC when an anchor change invalidated the claim.
        /// </summary>
        public DateTime? InvalidatedUtc { get; set; } = null;

        /// <summary>
        /// Durable explanation of why the claim needs another check.
        /// </summary>
        public string? InvalidationReason { get; set; } = null;
    }
}
