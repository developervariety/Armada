namespace Armada.Server.WebSocket
{
    using System;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A replayable WebSocket event with a process stream and monotonic cursor.
    /// </summary>
    internal sealed class WebSocketEventEnvelope
    {
        /// <summary>
        /// Event type.
        /// </summary>
        public string Type { get; set; } = "";

        /// <summary>
        /// Optional human-readable message.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; set; }

        /// <summary>
        /// Optional event data.
        /// </summary>
        public object? Data { get; set; }

        /// <summary>
        /// Event creation time in UTC.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Identifier for the Admiral process event stream.
        /// </summary>
        public string StreamId { get; set; } = "";

        /// <summary>
        /// Strictly increasing position in the process event stream.
        /// </summary>
        public long Cursor { get; set; }
    }
}
