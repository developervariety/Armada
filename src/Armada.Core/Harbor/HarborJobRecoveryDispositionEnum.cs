namespace Armada.Core.Harbor
{
    /// <summary>Outcome of a lifecycle recovery check.</summary>
    public enum HarborJobRecoveryDispositionEnum
    {
        /// <summary>The exact attempt is safe to retain and control.</summary>
        Adopted,

        /// <summary>Identity or ownership is uncertain; keep the durable capacity hold.</summary>
        Uncertain
    }
}
