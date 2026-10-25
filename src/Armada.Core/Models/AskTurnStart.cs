namespace Armada.Core.Models
{
    /// <summary>Result of accepting, replaying, or refusing an Ask turn request.</summary>
    public sealed class AskTurnStart
    {
        /// <summary>HTTP status selected by the service.</summary>
        public int StatusCode { get; set; }
        /// <summary>Stored or active turn, when one is available.</summary>
        public AskTurn? Turn { get; set; } = null;
        /// <summary>Short refusal reason for conflict or invalid request.</summary>
        public string? Message { get; set; } = null;
    }
}
