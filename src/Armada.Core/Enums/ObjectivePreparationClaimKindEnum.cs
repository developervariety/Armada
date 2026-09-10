namespace Armada.Core.Enums
{
    /// <summary>
    /// Classification for one verified objective-preparation claim.
    /// </summary>
    public enum ObjectivePreparationClaimKindEnum
    {
        /// <summary>A readable source path.</summary>
        SourcePath,
        /// <summary>An actual dispatch entry point.</summary>
        DispatchEntryPoint,
        /// <summary>An existing implementation type to reuse.</summary>
        ReuseType,
        /// <summary>A catalogue input.</summary>
        CatalogueInput,
        /// <summary>A provisioning requirement.</summary>
        ProvisioningRequirement,
        /// <summary>A response predicate or result rule.</summary>
        ResponseRule,
        /// <summary>A cleanup requirement.</summary>
        CleanupRequirement,
        /// <summary>An affected consumer obligation.</summary>
        ConsumerObligation,
        /// <summary>A source-ledger obligation.</summary>
        LedgerObligation,
        /// <summary>A remaining uncertainty.</summary>
        Uncertainty,
        /// <summary>A recorded owner decision.</summary>
        OwnerDecision
    }
}
