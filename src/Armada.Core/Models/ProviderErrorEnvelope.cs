namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>Typed provider error payload for JSON decoding.</summary>
    internal sealed class ProviderErrorEnvelope
    {
        /// <summary>Create an empty provider payload.</summary>
        public ProviderErrorEnvelope() { }

        /// <summary>Top-level provider error type.</summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>Top-level provider error code.</summary>
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        /// <summary>Nested provider error detail.</summary>
        [JsonPropertyName("error")]
        public ProviderErrorDetail? Error { get; set; }

    }
}
