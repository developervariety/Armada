namespace Armada.Core.Services
{
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Models;

    /// <summary>
    /// Helper methods for serializing and deserializing runtime-specific captain options.
    /// </summary>
    public static class CaptainRuntimeOptions
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _SerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Serialize a runtime options payload to JSON.
        /// </summary>
        public static string? Serialize<T>(T? value) where T : class
        {
            if (value == null) return null;
            return JsonSerializer.Serialize(value, _SerializerOptions);
        }

        /// <summary>
        /// Deserialize a runtime options payload from JSON.
        /// </summary>
        public static T? Deserialize<T>(string? json) where T : class
        {
            if (String.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(json, _SerializerOptions);
        }

        /// <summary>
        /// Retrieve typed Mux options for a captain.
        /// Returns null when the captain is null or has no runtime options payload.
        /// </summary>
        public static MuxCaptainOptions? GetMuxOptions(Captain? captain)
        {
            if (captain == null) return null;
            return Deserialize<MuxCaptainOptions>(captain.RuntimeOptionsJson);
        }

        #endregion
    }
}
