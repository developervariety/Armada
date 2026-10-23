namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>Typed provider error payload for JSON decoding.</summary>
    internal sealed class ProviderErrorDetail
    {
        /// <summary>Create an empty provider payload.</summary>
        public ProviderErrorDetail() { }

        /// <summary>Provider error type.</summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>Provider error code.</summary>
        [JsonPropertyName("code")]
        public string? Code { get; set; }

    }
}
