namespace Armada.Test.Shared.Infrastructure
{
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;

    /// <summary>
    /// Shared JSON serialization options and deserialization helpers for end-to-end suites.
    /// Uses case-insensitive deserialization to handle the server's camelCase responses.
    /// Does NOT use camelCase naming policy for serialization -- Watson WebServer expects PascalCase request bodies.
    /// Ported from the retired automated harness so end-to-end descriptors can construct request bodies
    /// and deserialize responses exactly as the legacy suites did.
    /// </summary>
    public static class JsonHelper
    {
        #region Public-Members

        /// <summary>
        /// Shared serializer options: case-insensitive input, string enums, null omission.
        /// No naming policy -- serialization preserves property names as-is (PascalCase for anonymous types).
        /// </summary>
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Deserialize the response body into a typed object.
        /// </summary>
        /// <typeparam name="T">Target type.</typeparam>
        /// <param name="response">HTTP response.</param>
        /// <returns>Deserialized instance.</returns>
        public static async Task<T> DeserializeAsync<T>(HttpResponseMessage response)
        {
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(body, Options)!;
        }

        /// <summary>
        /// Deserialize a JSON string into a typed object.
        /// </summary>
        /// <typeparam name="T">Target type.</typeparam>
        /// <param name="json">JSON string.</param>
        /// <returns>Deserialized instance.</returns>
        public static T Deserialize<T>(string json)
        {
            return JsonSerializer.Deserialize<T>(json, Options)!;
        }

        /// <summary>
        /// Serialize an object to a JSON string.
        /// </summary>
        /// <param name="value">Value to serialize.</param>
        /// <returns>JSON string.</returns>
        public static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, Options);
        }

        /// <summary>
        /// Create a StringContent with a JSON payload for HTTP requests.
        /// </summary>
        /// <param name="value">Value to serialize into the request body.</param>
        /// <returns>StringContent with application/json media type.</returns>
        public static StringContent ToJsonContent(object value)
        {
            return new StringContent(
                JsonSerializer.Serialize(value, Options),
                Encoding.UTF8,
                "application/json");
        }

        #endregion
    }
}
