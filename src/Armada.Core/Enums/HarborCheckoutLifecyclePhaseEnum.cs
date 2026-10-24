namespace Armada.Core.Enums
{
    /// <summary>Durable progress phase for Harbor-owned checkout cleanup.</summary>
    public enum HarborCheckoutLifecyclePhaseEnum
    {
        /// <summary>Intent and pinned head are durable before the first remote mutation.</summary>
        Prepared = 0,
        /// <summary>Uncommitted work snapshot has a durable replay receipt.</summary>
        SnapshotComplete = 1,
        /// <summary>Recovery references have durable replay receipts.</summary>
        ReferencesComplete = 2,
        /// <summary>Configured branch cleanup has a durable replay receipt.</summary>
        BranchCleanupComplete = 3,
        /// <summary>Remote checkout removal has a durable accepted receipt.</summary>
        CheckoutRemoved = 4,
        /// <summary>Local dock state records that lifecycle cleanup is complete.</summary>
        Terminal = 5
    }
}
