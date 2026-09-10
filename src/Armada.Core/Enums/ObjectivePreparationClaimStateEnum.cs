namespace Armada.Core.Enums
{
    /// <summary>
    /// Verification state for an objective-preparation claim.
    /// </summary>
    public enum ObjectivePreparationClaimStateEnum
    {
        /// <summary>The claim is verified against its current anchors.</summary>
        Verified,
        /// <summary>The claim must be checked again before it is relied on.</summary>
        NeedsRecheck
    }
}
