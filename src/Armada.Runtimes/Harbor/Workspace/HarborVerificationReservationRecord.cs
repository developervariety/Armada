namespace Armada.Runtimes.Harbor
{
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Durable reservation that fences a checkout when verification output or process state is uncertain.</summary>
    internal sealed class HarborVerificationReservationRecord
    {
        /// <summary>Opaque output identity reserved for this process attempt.</summary>
        public string OutputId { get; set; } = System.String.Empty;
        /// <summary>Stable operation key that owns the reservation.</summary>
        public string OperationKey { get; set; } = System.String.Empty;
        /// <summary>Checkout fenced while the verification writer is unconfirmed.</summary>
        public string CheckoutId { get; set; } = System.String.Empty;
        /// <summary>Bytes reserved for both verification streams.</summary>
        public long ReservedBytes { get; set; }
        /// <summary>Immutable binding used to validate later output reads.</summary>
        public HarborCheckoutBinding Binding { get; set; } = new HarborCheckoutBinding();
    }
}
