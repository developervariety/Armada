namespace Armada.Proxy.Models
{
    using System.Text.Json;

    /// <summary>
    /// Recorded inbound event observed from a connected Armada instance.
    /// </summary>
    public class RemoteInstanceEventRecord
    {
        #region Public-Members

        /// <summary>
        /// Event name or method.
        /// </summary>
        public string Method { get; set; } = String.Empty;

        /// <summary>
        /// Correlation identifier, if supplied.
        /// </summary>
        public string? CorrelationId { get; set; } = null;

        /// <summary>
        /// Human-readable message, if supplied.
        /// </summary>
        public string? Message { get; set; } = null;

        /// <summary>
        /// Event timestamp.
        /// </summary>
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Raw payload snapshot.
        /// </summary>
        public JsonElement? Payload { get; set; } = null;

        #endregion
    }
}
