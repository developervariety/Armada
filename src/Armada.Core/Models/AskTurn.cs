namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>A durable user request and its saved captain response.</summary>
    public sealed class AskTurn
    {
        /// <summary>Owning thread identifier.</summary>
        public string ThreadId { get; set; } = String.Empty;
        /// <summary>Client idempotency key and WebSocket turn identifier.</summary>
        public string RequestId { get; set; } = String.Empty;
        /// <summary>One-based sequence within the thread.</summary>
        public long Sequence { get; set; }
        /// <summary>Captain selected when the request was accepted.</summary>
        public string CaptainId { get; set; } = String.Empty;
        /// <summary>User message stored before runtime execution.</summary>
        public string Content { get; set; } = String.Empty;
        /// <summary>Whether the runtime should surface reasoning when supported.</summary>
        public bool ShowThinking { get; set; }
        /// <summary>Stored request lifecycle state.</summary>
        public AskTurnStateEnum State { get; set; } = AskTurnStateEnum.Pending;
        /// <summary>Typed runtime response, including model metrics and failure details.</summary>
        public CaptainChatResponse? Response { get; set; } = null;
        /// <summary>Tool calls a running turn has made so far. Empty once <see cref="Response"/> holds them.</summary>
        public List<ChatToolCall> LiveToolCalls { get; set; } = new List<ChatToolCall>();
        /// <summary>Request creation time in UTC.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Terminal response time in UTC, or null.</summary>
        public DateTime? CompletedUtc { get; set; } = null;
    }
}
