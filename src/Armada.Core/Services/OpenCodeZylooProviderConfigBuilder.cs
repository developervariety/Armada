namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Builds the ephemeral OpenCode configuration overlay required for a Zyloo model.
    /// </summary>
    /// <remarks>
    /// The configuration deliberately references <c>ZYLOO_KEY</c> by environment-variable
    /// placeholder rather than accepting or serializing a credential. OpenCode merges this
    /// inline overlay with its global and project configuration, so it adds only the Zyloo
    /// provider and leaves existing OpenCode providers and captains unchanged.
    /// </remarks>
    public static class OpenCodeZylooProviderConfigBuilder
    {
        #region Private-Members

        private const string _ModelPrefix = "zyloo/";
        private const string _SchemaUrl = "https://opencode.ai/config.json";
        private const string _ProviderPackage = "@ai-sdk/openai-compatible";
        private const string _ProviderName = "Zyloo";
        private const string _BaseUrl = "https://api.zyloo.io/v1";
        private const string _ApiKeyReference = "{env:ZYLOO_KEY}";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns whether a model identifier is served by Zyloo.
        /// </summary>
        /// <param name="model">Candidate OpenCode model identifier.</param>
        /// <returns>True when the normalized identifier begins with <c>zyloo/</c>.</returns>
        public static bool IsZylooModel(string? model)
        {
            return !String.IsNullOrWhiteSpace(model) &&
                model.Trim().StartsWith(_ModelPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds an inline OpenCode configuration overlay for one Zyloo model.
        /// </summary>
        /// <param name="model">Canonical Zyloo model identifier.</param>
        /// <returns>Valid, deterministic JSON suitable for <c>OPENCODE_CONFIG_CONTENT</c>.</returns>
        public static string Build(string model)
        {
            if (!IsZylooModel(model))
                throw new ArgumentException("A Zyloo model identifier must begin with 'zyloo/'.", nameof(model));

            string normalizedModel = model.Trim();
            OpenCodeConfigDocument document = new OpenCodeConfigDocument(normalizedModel);
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            return JsonSerializer.Serialize(document, options).Replace("\r\n", "\n");
        }

        #endregion

        #region Private-Types

        /// <summary>Root document for the OpenCode inline configuration overlay.</summary>
        private sealed class OpenCodeConfigDocument
        {
            /// <summary>Creates the document for a single model.</summary>
            /// <param name="model">Canonical Zyloo model identifier.</param>
            public OpenCodeConfigDocument(string model)
            {
                Provider = new Dictionary<string, OpenCodeProviderDefinition>(StringComparer.Ordinal)
                {
                    ["zyloo"] = new OpenCodeProviderDefinition(model)
                };
            }

            /// <summary>OpenCode JSON-schema URL.</summary>
            [JsonPropertyName("$schema")]
            public string Schema { get; set; } = _SchemaUrl;

            /// <summary>Provider overlay.</summary>
            [JsonPropertyName("provider")]
            public Dictionary<string, OpenCodeProviderDefinition> Provider { get; set; }
        }

        /// <summary>OpenCode custom-provider definition.</summary>
        private sealed class OpenCodeProviderDefinition
        {
            /// <summary>Creates the custom provider for one model.</summary>
            /// <param name="model">Canonical Zyloo model identifier.</param>
            public OpenCodeProviderDefinition(string model)
            {
                Models = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [model] = new object()
                };
            }

            /// <summary>AI SDK adapter package.</summary>
            [JsonPropertyName("npm")]
            public string Npm { get; set; } = _ProviderPackage;

            /// <summary>Display name.</summary>
            [JsonPropertyName("name")]
            public string Name { get; set; } = _ProviderName;

            /// <summary>OpenAI-compatible provider options.</summary>
            [JsonPropertyName("options")]
            public OpenCodeProviderOptions Options { get; set; } = new OpenCodeProviderOptions();

            /// <summary>Models exposed by this custom provider.</summary>
            [JsonPropertyName("models")]
            public Dictionary<string, object> Models { get; set; }
        }

        /// <summary>Connection options for the provider.</summary>
        private sealed class OpenCodeProviderOptions
        {
            /// <summary>OpenAI-compatible Zyloo endpoint.</summary>
            [JsonPropertyName("baseURL")]
            public string BaseUrl { get; set; } = _BaseUrl;

            /// <summary>OpenCode environment-variable credential expression.</summary>
            [JsonPropertyName("apiKey")]
            public string ApiKey { get; set; } = _ApiKeyReference;
        }

        #endregion
    }
}
