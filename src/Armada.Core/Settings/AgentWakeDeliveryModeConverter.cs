namespace Armada.Core.Settings
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Reads and writes <see cref="AgentWakeDeliveryMode"/>, accepting the legacy
    /// <c>McpNotification</c> spelling of <see cref="AgentWakeDeliveryMode.StoredWake"/>.
    /// <para>
    /// The old name claimed an MCP notification. Armada's MCP transport is stateless and
    /// cannot carry a server push, so no notification was ever sent; the mode stores a Wake
    /// row that a session collects at its next tool call. The name is corrected, and this
    /// converter keeps every settings file written before the rename loading unchanged.
    /// </para>
    /// </summary>
    public sealed class AgentWakeDeliveryModeConverter : JsonConverter<AgentWakeDeliveryMode>
    {
        #region Public-Members

        /// <summary>
        /// The pre-rename spelling of <see cref="AgentWakeDeliveryMode.StoredWake"/>. Still
        /// accepted on read so an existing settings file keeps working.
        /// </summary>
        public const string LegacyStoredWakeName = "McpNotification";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read a delivery mode from JSON.
        /// </summary>
        /// <param name="reader">JSON reader.</param>
        /// <param name="typeToConvert">Target type.</param>
        /// <param name="options">Serializer options.</param>
        /// <returns>The parsed delivery mode.</returns>
        public override AgentWakeDeliveryMode Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("AgentWake deliveryMode must be a string.");

            string? value = reader.GetString();
            if (String.IsNullOrWhiteSpace(value))
                throw new JsonException("AgentWake deliveryMode must not be empty.");

            string trimmed = value!.Trim();
            if (String.Equals(trimmed, LegacyStoredWakeName, StringComparison.OrdinalIgnoreCase))
                return AgentWakeDeliveryMode.StoredWake;

            if (Enum.TryParse(trimmed, ignoreCase: true, out AgentWakeDeliveryMode parsed))
                return parsed;

            throw new JsonException(
                "AgentWake deliveryMode '" + trimmed + "' is not recognized. Use SpawnProcess, StoredWake, or Both.");
        }

        /// <summary>
        /// Write a delivery mode to JSON using its current name.
        /// </summary>
        /// <param name="writer">JSON writer.</param>
        /// <param name="value">Value to write.</param>
        /// <param name="options">Serializer options.</param>
        public override void Write(
            Utf8JsonWriter writer,
            AgentWakeDeliveryMode value,
            JsonSerializerOptions options)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.WriteStringValue(value.ToString());
        }

        #endregion
    }
}
