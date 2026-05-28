namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// DeepSeek inference client using OpenAI-compatible chat completion endpoint.
    /// </summary>
    public sealed class DeepSeekInferenceClient : IInferenceClient
    {
        #region Private-Members

        private const string _Header = "[DeepSeekInferenceClient] ";
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly CodeIndexSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly HttpClient _Http;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a DeepSeek inference client.
        /// </summary>
        public DeepSeekInferenceClient(CodeIndexSettings settings, LoggingModule logging, HttpClient http)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Http = http ?? throw new ArgumentNullException(nameof(http));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken token = default)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    string endpoint = BuildCompletionEndpoint();
                    ChatCompletionRequest payload = new ChatCompletionRequest
                    {
                        Model = _Settings.SummarizerModel,
                        Messages = new List<ChatMessage>
                        {
                            new ChatMessage { Role = "system", Content = systemPrompt ?? string.Empty },
                            new ChatMessage { Role = "user", Content = userMessage ?? string.Empty }
                        },
                        MaxTokens = _Settings.MaxSummaryOutputTokens
                    };

                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    string apiKey = ResolveInferenceApiKey();
                    if (!string.IsNullOrWhiteSpace(apiKey))
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    request.Content = new StringContent(
                        JsonSerializer.Serialize(payload, _JsonOptions),
                        Encoding.UTF8,
                        "application/json");

                    using HttpResponseMessage response = await _Http.SendAsync(request, token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (IsRetryableStatusCode(response) && attempt < maxAttempts)
                        {
                            await DelayForRetryAsync(attempt, token).ConfigureAwait(false);
                            continue;
                        }

                        _Logging.Warn(_Header + "completion request failed after " + attempt + " attempt(s) with status code " + (int)response.StatusCode);
                        return string.Empty;
                    }

                    // Stream the response body straight into the deserializer to avoid
                    // a Large Object Heap string allocation. LLM completion responses
                    // can be tens of KB to multi-MB; the prior shape allocated the
                    // entire body as a string on the LOH per call.
                    using Stream responseStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    ChatCompletionResponse? parsed = await JsonSerializer.DeserializeAsync<ChatCompletionResponse>(responseStream, _JsonOptions, token).ConfigureAwait(false);
                    ChatMessage? message = parsed?.Choices != null && parsed.Choices.Count > 0
                        ? parsed.Choices[0].Message
                        : null;
                    if (message != null)
                        return message.Content ?? string.Empty;

                    _Logging.Warn(_Header + "completion response missing choices[0].message.content");
                    return string.Empty;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "completion request failed: " + ex.Message);
                    return string.Empty;
                }
            }

            return string.Empty;
        }

        #endregion

        #region Private-Methods

        private string BuildCompletionEndpoint()
        {
            string baseUrl = ResolveInferenceBaseUrl().Trim();
            return baseUrl.TrimEnd('/') + "/v1/chat/completions";
        }

        private string ResolveInferenceBaseUrl()
        {
            if (!string.IsNullOrWhiteSpace(_Settings.SummarizerApiBaseUrl))
                return _Settings.SummarizerApiBaseUrl;
            return _Settings.EmbeddingApiBaseUrl;
        }

        private string ResolveInferenceApiKey()
        {
            if (!string.IsNullOrWhiteSpace(_Settings.SummarizerApiKey))
                return _Settings.SummarizerApiKey;
            return _Settings.EmbeddingApiKey;
        }

        private static bool IsRetryableStatusCode(HttpResponseMessage response)
        {
            int statusCode = (int)response.StatusCode;
            return statusCode == 429 || statusCode >= 500;
        }

        private static Task DelayForRetryAsync(int attempt, CancellationToken token)
        {
            int exponentialMs = Math.Min(8000, 500 * (1 << Math.Max(0, attempt - 1)));
            int jitterMs = Random.Shared.Next(0, 101);
            return Task.Delay(exponentialMs + jitterMs, token);
        }

        #endregion

        #region Private-Types

        private sealed class ChatCompletionRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [JsonPropertyName("messages")]
            public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }
        }

        private sealed class ChatMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = string.Empty;

            [JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;
        }

        private sealed class ChatCompletionResponse
        {
            [JsonPropertyName("choices")]
            public List<ChatChoice>? Choices { get; set; }
        }

        private sealed class ChatChoice
        {
            [JsonPropertyName("message")]
            public ChatMessage? Message { get; set; }
        }

        #endregion
    }
}
