namespace Armada.Core.Models
{
    using System;

    /// <summary>One tool call a captain made during a chat turn, kept so a reloaded conversation can show it.</summary>
    public sealed class ChatToolCall
    {
        /// <summary>Call identifier. A started call and its completion share one identifier.</summary>
        public string Id { get; set; } = String.Empty;
        /// <summary>Tool name.</summary>
        public string Name { get; set; } = String.Empty;
        /// <summary>"running" until the call completes, then "success" or "failed".</summary>
        public string Status { get; set; } = "running";
        /// <summary>Call arguments as JSON or a short detail, or null.</summary>
        public string? Arguments { get; set; } = null;
        /// <summary>Call result, or null.</summary>
        public string? Result { get; set; } = null;
        /// <summary>Elapsed milliseconds once the call completed, or null.</summary>
        public double? ElapsedMs { get; set; } = null;
    }
}
