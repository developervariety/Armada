namespace Armada.Core.Services
{
    using System;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Builds a bounded HTTP client for a configured <see cref="ModelEndpoint"/> and enforces provider kind
    /// capability rules. Validation requests use the provider wire contracts directly so this assembly does
    /// not need a runtime model client dependency.
    /// </summary>
    public static class ModelEndpointClientFactory
    {
        #region Public-Methods

        /// <summary>
        /// Whether the given provider can serve the given kind of model. Anthropic has no embedding API and
        /// Voyage AI has no inference API, so those combinations are rejected before a client is built.
        /// </summary>
        /// <param name="provider">Provider.</param>
        /// <param name="kind">Endpoint kind.</param>
        /// <returns>True when the provider supports the kind.</returns>
        public static bool SupportsKind(ModelProviderEnum provider, ModelEndpointKindEnum kind)
        {
            if (kind == ModelEndpointKindEnum.Embedding && provider == ModelProviderEnum.Anthropic) return false;
            if (kind == ModelEndpointKindEnum.Inference && provider == ModelProviderEnum.VoyageAI) return false;
            return true;
        }

        /// <summary>
        /// Human-readable reason a provider cannot serve a kind, or null when the combination is valid.
        /// </summary>
        /// <param name="provider">Provider.</param>
        /// <param name="kind">Endpoint kind.</param>
        /// <returns>Reason string or null.</returns>
        public static string? UnsupportedReason(ModelProviderEnum provider, ModelEndpointKindEnum kind)
        {
            if (kind == ModelEndpointKindEnum.Embedding && provider == ModelProviderEnum.Anthropic)
                return "Anthropic does not provide an embeddings API; choose the Inference kind or a different provider.";
            if (kind == ModelEndpointKindEnum.Inference && provider == ModelProviderEnum.VoyageAI)
                return "Voyage AI provides embeddings only; choose the Embedding kind or a different provider.";
            return null;
        }

        /// <summary>
        /// Build an HTTP client for a single endpoint probe. The caller owns the returned client and must dispose it.
        /// </summary>
        /// <param name="endpoint">Model endpoint.</param>
        /// <returns>A configured HTTP client.</returns>
        public static HttpClient CreateHttpClient(ModelEndpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (!Enum.IsDefined(endpoint.Provider)) throw new ArgumentException("Unknown model endpoint provider.");
            if (!Enum.IsDefined(endpoint.Kind)) throw new ArgumentException("Unknown model endpoint kind.");

            string? reason = UnsupportedReason(endpoint.Provider, endpoint.Kind);
            if (reason != null) throw new InvalidOperationException(reason);

            if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !String.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Endpoint base URL must be an absolute HTTP(S) URL without user information.");

            HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false };
            HttpClient client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromMilliseconds(endpoint.TimeoutMs);
            if (!String.IsNullOrWhiteSpace(endpoint.ApiKey))
            {
                switch (endpoint.Provider)
                {
                    case ModelProviderEnum.OpenAI:
                    case ModelProviderEnum.OpenAICompatible:
                    case ModelProviderEnum.VoyageAI:
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                        break;
                    case ModelProviderEnum.Anthropic:
                        client.DefaultRequestHeaders.Add("x-api-key", endpoint.ApiKey);
                        break;
                    case ModelProviderEnum.Gemini:
                        client.DefaultRequestHeaders.Add("x-goog-api-key", endpoint.ApiKey);
                        break;
                    case ModelProviderEnum.Ollama:
                        break;
                    default:
                        throw new ArgumentException("Unknown model endpoint provider.");
                }
            }
            return client;
        }

        /// <summary>
        /// Build the bounded, provider-specific request used by the explicit model validation route. The
        /// returned request owns its JSON content and must be disposed by the caller.
        /// </summary>
        /// <param name="endpoint">Model endpoint.</param>
        /// <returns>A POST request for the configured provider and endpoint kind.</returns>
        public static HttpRequestMessage CreateValidationRequest(ModelEndpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (String.IsNullOrWhiteSpace(endpoint.Model))
                throw new ArgumentException("A model is required for model validation.");

            if (!Enum.IsDefined(endpoint.Provider)) throw new ArgumentException("Unknown model endpoint provider.");
            if (!Enum.IsDefined(endpoint.Kind)) throw new ArgumentException("Unknown model endpoint kind.");
            string? reason = UnsupportedReason(endpoint.Provider, endpoint.Kind);
            if (reason != null) throw new InvalidOperationException(reason);

            string model = endpoint.Model.Trim();
            string prompt = "Reply with the single word: pong";
            Uri uri;
            object body;

            switch (endpoint.Provider)
            {
                case ModelProviderEnum.Ollama:
                    if (endpoint.Kind == ModelEndpointKindEnum.Embedding)
                    {
                        uri = AppendProviderPath(endpoint.BaseUrl, "api", "embeddings");
                        body = new { model, prompt = "Armada model endpoint validation" };
                    }
                    else
                    {
                        uri = AppendProviderPath(endpoint.BaseUrl, "api", "chat");
                        body = new
                        {
                            model,
                            messages = new[] { new { role = "user", content = prompt } },
                            stream = false
                        };
                    }
                    break;
                case ModelProviderEnum.OpenAI:
                case ModelProviderEnum.OpenAICompatible:
                    uri = AppendProviderPath(endpoint.BaseUrl, "v1", endpoint.Kind == ModelEndpointKindEnum.Embedding ? "embeddings" : "chat/completions");
                    body = endpoint.Kind == ModelEndpointKindEnum.Embedding
                        ? new { model, input = new[] { "Armada model endpoint validation" } }
                        : new
                        {
                            model,
                            messages = new[] { new { role = "user", content = prompt } },
                            max_tokens = 16
                        };
                    break;
                case ModelProviderEnum.Anthropic:
                    uri = AppendProviderPath(endpoint.BaseUrl, "v1", "messages");
                    body = new
                    {
                        model,
                        max_tokens = 16,
                        messages = new[] { new { role = "user", content = prompt } }
                    };
                    break;
                case ModelProviderEnum.Gemini:
                    if (endpoint.Kind == ModelEndpointKindEnum.Embedding)
                    {
                        uri = AppendProviderPath(endpoint.BaseUrl, "v1beta", "models/" + Uri.EscapeDataString(model) + ":embedContent");
                        body = new
                        {
                            model = "models/" + model,
                            content = new { parts = new[] { new { text = "Armada model endpoint validation" } } }
                        };
                    }
                    else
                    {
                        uri = AppendProviderPath(endpoint.BaseUrl, "v1beta", "models/" + Uri.EscapeDataString(model) + ":generateContent");
                        body = new
                        {
                            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
                            generationConfig = new { maxOutputTokens = 16 }
                        };
                    }
                    break;
                case ModelProviderEnum.VoyageAI:
                    uri = AppendProviderPath(endpoint.BaseUrl, "v1", "embeddings");
                    body = new
                    {
                        model,
                        input = new[] { "Armada model endpoint validation" },
                        input_type = "document"
                    };
                    break;
                default:
                    throw new ArgumentException("Unknown model endpoint provider.");
            }

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (endpoint.Provider == ModelProviderEnum.Anthropic)
                request.Headers.Add("anthropic-version", "2023-06-01");
            return request;
        }

        private static Uri AppendProviderPath(string baseUrl, string version, string leaf)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
                || !String.IsNullOrEmpty(baseUri.UserInfo))
                throw new ArgumentException("Endpoint base URL must be an absolute HTTP(S) URL without user information.");

            string path = baseUri.AbsolutePath.TrimEnd('/');
            string versionSuffix = "/" + version;
            if (!path.EndsWith(versionSuffix, StringComparison.OrdinalIgnoreCase)) path += versionSuffix;
            path += "/" + leaf.TrimStart('/');
            UriBuilder builder = new UriBuilder(baseUri) { Path = path, Query = String.Empty };
            return builder.Uri;
        }

        #endregion
    }
}
