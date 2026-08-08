namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Settings;

    /// <summary>
    /// Builds the ephemeral OpenCode configuration overlay required for a model served by
    /// an external provider such as cun-ai.
    /// </summary>
    /// <remarks>
    /// The configuration references the provider's host environment variable (for example
    /// <c>CUN_AI_KEY</c>) by placeholder rather than accepting or serializing a credential.
    /// OpenCode merges this inline overlay with its global and project configuration, so it
    /// adds only the provider and leaves existing OpenCode providers and captains unchanged.
    /// </remarks>
    public static class OpenCodeProviderConfigBuilder
    {
        #region Private-Members

        private const string _SchemaUrl = "https://opencode.ai/config.json";
        private const string _ProviderPackage = "@ai-sdk/openai-compatible";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns whether a model identifier is served by a registered external provider.
        /// </summary>
        /// <param name="model">Candidate OpenCode model identifier.</param>
        /// <param name="providers">Provider registry; null uses the built-in default set.</param>
        /// <returns>True when the identifier carries a registered provider prefix.</returns>
        public static bool IsProviderModel(string? model, ModelProvidersSettings? providers)
        {
            if (String.IsNullOrWhiteSpace(model)) return false;

            ModelProvidersSettings registry = providers ?? new ModelProvidersSettings();
            string trimmed = model.Trim();
            int slash = trimmed.IndexOf('/');
            if (slash <= 0) return false;

            return registry.Find(trimmed.Substring(0, slash)) != null;
        }

        /// <summary>
        /// Builds an inline OpenCode configuration overlay for one provider model using the
        /// provider's host environment-variable credential reference.
        /// </summary>
        /// <param name="model">Canonical provider model identifier, for example <c>cun-ai/claude-fable-5</c>.</param>
        /// <returns>Valid, deterministic JSON suitable for <c>OPENCODE_CONFIG_CONTENT</c>.</returns>
        public static string Build(string model)
        {
            return Build(model, null, null, null);
        }

        /// <summary>
        /// Builds an inline OpenCode configuration overlay for one provider model.
        /// A per-captain credential wins over the environment-variable reference, so captains on
        /// separate subscriptions run side by side without a shared host variable.
        /// </summary>
        /// <param name="model">Canonical provider model identifier.</param>
        /// <param name="apiKey">Optional per-captain credential; when null the overlay references
        /// the provider's host environment variable instead of embedding a credential.</param>
        /// <param name="baseUrl">Optional per-captain base URL; when null the registered
        /// provider's OpenAI-compatible endpoint is used.</param>
        /// <returns>Valid, deterministic JSON suitable for <c>OPENCODE_CONFIG_CONTENT</c>.</returns>
        public static string Build(string model, string? apiKey, string? baseUrl)
        {
            return Build(model, apiKey, baseUrl, null);
        }

        /// <summary>
        /// Builds an inline OpenCode configuration overlay for one provider model resolved
        /// against the provider registry.
        /// </summary>
        /// <param name="model">Canonical provider model identifier, for example <c>cun-ai/claude-fable-5</c>.</param>
        /// <param name="apiKey">Optional per-captain credential; when null the overlay references
        /// the provider's host environment variable instead of embedding a credential.</param>
        /// <param name="baseUrl">Optional per-captain base URL; when null the registered
        /// provider's OpenAI-compatible endpoint is used.</param>
        /// <param name="providers">Provider registry; null uses the built-in default set.</param>
        /// <returns>Valid, deterministic JSON suitable for <c>OPENCODE_CONFIG_CONTENT</c>.</returns>
        public static string Build(string model, string? apiKey, string? baseUrl, ModelProvidersSettings? providers)
        {
            if (!IsProviderModel(model, providers))
                throw new ArgumentException("A provider model identifier must carry a registered provider prefix.", nameof(model));

            ModelProvidersSettings registry = providers ?? new ModelProvidersSettings();
            string normalizedModel = model.Trim();
            int slash = normalizedModel.IndexOf('/');
            string providerId = normalizedModel.Substring(0, slash);
            ModelProviderSettings provider = registry.Find(providerId)!;

            string effectiveBaseUrl = !String.IsNullOrWhiteSpace(baseUrl)
                ? baseUrl
                : provider.OpenAiBaseUrl;

            string apiKeyReference;
            if (!String.IsNullOrWhiteSpace(apiKey))
            {
                apiKeyReference = apiKey;
            }
            else if (!String.IsNullOrWhiteSpace(provider.ApiKeyEnv))
            {
                apiKeyReference = "{env:" + provider.ApiKeyEnv + "}";
            }
            else
            {
                throw new ArgumentException(
                    "Provider '" + providerId + "' defines no ApiKeyEnv and no per-captain key was supplied.",
                    nameof(apiKey));
            }

            OpenCodeConfigDocument document = new OpenCodeConfigDocument(
                normalizedModel,
                apiKeyReference,
                effectiveBaseUrl,
                providerId,
                !String.IsNullOrWhiteSpace(provider.Name) ? provider.Name : providerId);
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
            /// <param name="model">Canonical provider model identifier.</param>
            /// <param name="apiKey">Credential or environment-variable reference to embed.</param>
            /// <param name="baseUrl">OpenAI-compatible base URL for the provider.</param>
            /// <param name="providerId">Provider namespace prefix.</param>
            /// <param name="providerName">Display name for the provider.</param>
            public OpenCodeConfigDocument(string model, string apiKey, string baseUrl, string providerId, string providerName)
            {
                Provider = new Dictionary<string, OpenCodeProviderDefinition>(StringComparer.Ordinal)
                {
                    [providerId] = new OpenCodeProviderDefinition(model, apiKey, baseUrl, providerName)
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
            /// <param name="model">Canonical provider model identifier.</param>
            /// <param name="apiKey">Credential or environment-variable reference.</param>
            /// <param name="baseUrl">OpenAI-compatible base URL.</param>
            /// <param name="providerName">Display name for the provider.</param>
            public OpenCodeProviderDefinition(string model, string apiKey, string baseUrl, string providerName)
            {
                string providerModelId = model.Substring(model.IndexOf('/') + 1);
                Models = new Dictionary<string, OpenCodeModelDefinition>(StringComparer.Ordinal)
                {
                    [providerModelId] = new OpenCodeModelDefinition(providerModelId)
                };

                Options.ApiKey = apiKey;
                Options.BaseUrl = baseUrl;
                Name = providerName;
            }

            /// <summary>AI SDK adapter package.</summary>
            [JsonPropertyName("npm")]
            public string Npm { get; set; } = _ProviderPackage;

            /// <summary>Display name.</summary>
            [JsonPropertyName("name")]
            public string Name { get; set; }

            /// <summary>OpenAI-compatible provider options.</summary>
            [JsonPropertyName("options")]
            public OpenCodeProviderOptions Options { get; set; } = new OpenCodeProviderOptions();

            /// <summary>Models exposed by this custom provider.</summary>
            [JsonPropertyName("models")]
            public Dictionary<string, OpenCodeModelDefinition> Models { get; set; }
        }

        /// <summary>OpenCode model alias mapped to the provider-facing API identifier.</summary>
        private sealed class OpenCodeModelDefinition
        {
            /// <summary>Creates a model definition for the provider API identifier.</summary>
            /// <param name="model">Provider-facing model identifier, without the Armada namespace prefix.</param>
            public OpenCodeModelDefinition(string model)
            {
                Id = model;
            }

            /// <summary>Model identifier sent to the provider's OpenAI-compatible API.</summary>
            [JsonPropertyName("id")]
            public string Id { get; set; }
        }

        /// <summary>Connection options for the provider.</summary>
        private sealed class OpenCodeProviderOptions
        {
            /// <summary>OpenAI-compatible provider endpoint.</summary>
            [JsonPropertyName("baseURL")]
            public string BaseUrl { get; set; } = String.Empty;

            /// <summary>OpenCode environment-variable credential expression or literal key.</summary>
            [JsonPropertyName("apiKey")]
            public string ApiKey { get; set; } = String.Empty;
        }

        #endregion
    }
}
