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
    /// Voyage AI embedding client for the code index. Calls the Voyage AI embeddings
    /// endpoint and fails gracefully: on any transport, status, or parse error it logs a
    /// warning and returns an empty result rather than throwing into the caller.
    /// </summary>
    public sealed class VoyageEmbeddingClient : IEmbeddingClient
    {
        #region Private-Members

        private const string _Header = "[VoyageEmbeddingClient] ";
        private const string _DocumentInputType = "document";
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly CodeIndexSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly HttpClient _Http;
        private readonly Func<TimeSpan, CancellationToken, Task> _DelayAsync;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a Voyage AI embedding client.
        /// </summary>
        public VoyageEmbeddingClient(CodeIndexSettings settings, LoggingModule logging, HttpClient http)
            : this(settings, logging, http, (delay, token) => Task.Delay(delay, token))
        {
        }

        /// <summary>
        /// Create a Voyage AI embedding client. <paramref name="delayAsync"/> performs each retry backoff wait, so a test can observe the
        /// backoff without waiting for it.
        /// </summary>
        public VoyageEmbeddingClient(CodeIndexSettings settings, LoggingModule logging, HttpClient http, Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Http = http ?? throw new ArgumentNullException(nameof(http));
            _DelayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
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
                        InputType = _DocumentInputType,
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

                    // Stream the response body straight into the deserializer to avoid
                    // a Large Object Heap string allocation for the full JSON payload
                    // (each voyage-code-3 batch response is ~190 KB and would otherwise
                    // round-trip through a >85 KB string on every batch).
                    using Stream responseStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    EmbeddingResponse? parsed = await JsonSerializer.DeserializeAsync<EmbeddingResponse>(responseStream, _JsonOptions, token).ConfigureAwait(false);
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
            // The base URL already carries the API version segment (for example
            // https://api.voyageai.com/v1), so the client appends only the resource path.
            string baseUrl = (_Settings.EmbeddingApiBaseUrl ?? string.Empty).Trim();
            return baseUrl.TrimEnd('/') + "/embeddings";
        }

        private static bool IsRetryableStatusCode(HttpResponseMessage response)
        {
            int statusCode = (int)response.StatusCode;
            return statusCode == 429 || statusCode >= 500;
        }

        private Task DelayForRetryAsync(int attempt, CancellationToken token)
        {
            int exponentialMs = Math.Min(8000, 500 * (1 << Math.Max(0, attempt - 1)));
            int jitterMs = Random.Shared.Next(0, 101);
            return _DelayAsync(TimeSpan.FromMilliseconds(exponentialMs + jitterMs), token);
        }

        #endregion

        #region Private-Types

        private sealed class EmbeddingRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [JsonPropertyName("input_type")]
            public string InputType { get; set; } = _DocumentInputType;

            [JsonPropertyName("input")]
            public List<string> Input { get; set; } = new List<string>();
        }

        private sealed class EmbeddingResponse
        {
            [JsonPropertyName("data")]
            public List<EmbeddingData>? Data { get; set; }

            [JsonPropertyName("model")]
            public string? Model { get; set; }

            [JsonPropertyName("usage")]
            public EmbeddingUsage? Usage { get; set; }
        }

        private sealed class EmbeddingData
        {
            [JsonPropertyName("index")]
            public int? Index { get; set; }

            [JsonPropertyName("embedding")]
            public List<float>? Embedding { get; set; }
        }

        private sealed class EmbeddingUsage
        {
            [JsonPropertyName("total_tokens")]
            public int? TotalTokens { get; set; }
        }

        #endregion
    }
}
