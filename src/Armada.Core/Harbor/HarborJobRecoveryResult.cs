namespace Armada.Core.Harbor
{
    /// <summary>Result of restoring or adopting a durable Harbor job.</summary>
    public sealed class HarborJobRecoveryResult
    {
        /// <summary>Whether the durable attempt was safely restored or remains uncertain.</summary>
        public HarborJobRecoveryDispositionEnum Disposition { get; }

        /// <summary>Lifecycle observer for a restored mission job.</summary>
        public IHarborJobObserver? Observer { get; }

        /// <summary>Create a recovery result.</summary>
        public HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum disposition, IHarborJobObserver? observer = null)
        {
            Disposition = disposition;
            Observer = observer;
        }
    }
}
