namespace Armada.Runtimes.Tools
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>Deserializes tool arguments into one typed request DTO.</summary>
    internal static class ToolArgumentParser
    {
        private static readonly JsonSerializerOptions _Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.Strict
        };

        /// <summary>Parse one tool request and reject malformed or oversized input.</summary>
        public static T Parse<T>(string json) where T : class
        {
            ToolExecution.EnsureInputSize(json, "tool arguments");
            try
            {
                return JsonSerializer.Deserialize<T>(json, _Options)
                    ?? throw new ArgumentException("Tool arguments must be a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("Tool arguments are invalid: " + ex.Message, ex);
            }
        }
    }
}
