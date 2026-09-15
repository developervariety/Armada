namespace Armada.Core.Settings
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Reads and writes <see cref="AgentWakeDeliveryMode"/> by its declared member names. An unknown,
    /// empty or non-string value is rejected with an error that lists the accepted names.
    /// </summary>
    public sealed class AgentWakeDeliveryModeConverter : JsonConverter<AgentWakeDeliveryMode>
    {
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
            if (Enum.TryParse(trimmed, ignoreCase: true, out AgentWakeDeliveryMode parsed))
                return parsed;

            throw new JsonException(
                "AgentWake deliveryMode '" + trimmed + "' is not recognized. Use SpawnProcess, StoredWake, or Both.");
        }

        /// <summary>
        /// Write a delivery mode to JSON using its declared name.
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
