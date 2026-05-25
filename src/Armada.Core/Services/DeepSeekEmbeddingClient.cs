namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
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
    /// DeepSeek embedding client using OpenAI-compatible embeddings endpoint.
    /// </summary>
    public sealed class DeepSeekEmbeddingClient : IEmbeddingClient
    {
        #region Private-Members

        private const string _Header = "[DeepSeekEmbeddingClient] ";
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
        /// Create a DeepSeek embedding client.
        /// </summary>
        public DeepSeekEmbeddingClient(CodeIndexSettings settings, LoggingModule logging, HttpClient http)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Http = http ?? throw new ArgumentNullException(nameof(http));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<float[]> EmbedAsync(string text, CancellationToken token = default)
        {
            IReadOnlyList<float[]> vectors = await EmbedBatchAsync(new List<string> { text ?? string.Empty }, token).ConfigureAwait(false);
            return vectors.Count > 0 ? vectors[0] : Array.Empty<float>();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken token = default)
        {
            if (texts == null || texts.Count == 0) return Array.Empty<float[]>();

            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    string endpoint = BuildEmbeddingEndpoint();
                    EmbeddingRequest payload = new EmbeddingRequest
                    {
                        Model = _Settings.EmbeddingModel,
                        Input = texts.Select(t => t ?? string.Empty).ToList()
                    };

                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    if (!string.IsNullOrWhiteSpace(_Settings.EmbeddingApiKey))
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _Settings.EmbeddingApiKey);
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

                        _Logging.Warn(_Header + "embedding request failed after " + attempt + " attempt(s) with status code " + (int)response.StatusCode);
                        return Array.Empty<float[]>();
                    }

                    string responseBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    EmbeddingResponse? parsed = JsonSerializer.Deserialize<EmbeddingResponse>(responseBody, _JsonOptions);
                    if (parsed?.Data != null && parsed.Data.Count > 0)
                    {
                        float[][] vectors = new float[texts.Count][];
                        bool sawIndexed = false;
                        int sequential = 0;
                        foreach (EmbeddingData data in parsed.Data)
                        {
                            if (data.Embedding == null) continue;
                            int index = data.Index.HasValue ? data.Index.Value : sequential;
                            sequential++;
                            if (index < 0 || index >= vectors.Length) continue;
                            if (data.Index.HasValue) sawIndexed = true;
                            vectors[index] = data.Embedding.ToArray();
                        }

                        if (!sawIndexed && parsed.Data.Count == texts.Count)
                        {
                            for (int i = 0; i < parsed.Data.Count; i++)
                            {
                                vectors[i] = parsed.Data[i].Embedding?.ToArray() ?? Array.Empty<float>();
                            }
                        }

                        return vectors.Select(v => v ?? Array.Empty<float>()).ToList();
                    }

                    _Logging.Warn(_Header + "embedding response missing data[0].embedding");
                    return Array.Empty<float[]>();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "embedding request failed: " + ex.Message);
                    return Array.Empty<float[]>();
                }
            }

            return Array.Empty<float[]>();
        }

        #endregion

        #region Private-Methods

        private string BuildEmbeddingEndpoint()
        {
            string baseUrl = (_Settings.EmbeddingApiBaseUrl ?? string.Empty).Trim();
            return baseUrl.TrimEnd('/') + "/v1/embeddings";
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

        private sealed class EmbeddingRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [JsonPropertyName("input")]
            public List<string> Input { get; set; } = new List<string>();
        }

        private sealed class EmbeddingResponse
        {
            [JsonPropertyName("data")]
            public List<EmbeddingData>? Data { get; set; }
        }

        private sealed class EmbeddingData
        {
            [JsonPropertyName("index")]
            public int? Index { get; set; }

            [JsonPropertyName("embedding")]
            public List<float>? Embedding { get; set; }
        }

        #endregion
    }
}
