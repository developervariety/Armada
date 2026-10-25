namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>Database result when a durable turn request is reserved.</summary>
    public sealed class AskTurnReservation
    {
        /// <summary>Reservation outcome.</summary>
        public AskTurnReservationResultEnum Result { get; set; }
        /// <summary>The created, existing, or active turn when present.</summary>
        public AskTurn? Turn { get; set; } = null;
    }
}
