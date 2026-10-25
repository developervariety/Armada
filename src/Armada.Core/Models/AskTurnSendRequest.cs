namespace Armada.Core.Models
{
    /// <summary>A client-idempotent Ask turn request.</summary>
    public sealed class AskTurnSendRequest
    {
        /// <summary>Stable client request id, also used as the live turn id.</summary>
        public string RequestId { get; set; } = string.Empty;
        /// <summary>User message content.</summary>
        public string Content { get; set; } = string.Empty;
        /// <summary>Whether to request runtime reasoning when supported.</summary>
        public bool ShowThinking { get; set; }
    }
}
