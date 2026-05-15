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
    /// Inference client that routes completion requests through a local OpenCode daemon.
    /// </summary>
    public sealed class OpenCodeServerInferenceClient : IInferenceClient
    {
        #region Private-Members

        private const string _Header = "[OpenCodeServerInferenceClient] ";
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
        /// Create an OpenCode server inference client.
        /// </summary>
        public OpenCodeServerInferenceClient(ArmadaSettings settings, LoggingModule logging, HttpClient http)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _Settings = settings.CodeIndex ?? throw new ArgumentNullException(nameof(settings.CodeIndex));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Http = http ?? throw new ArgumentNullException(nameof(http));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken token = default)
        {
            try
            {
                string sessionId = await CreateSessionAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sessionId)) return string.Empty;
                return await SendMessageAsync(sessionId, systemPrompt ?? string.Empty, userMessage ?? string.Empty, token).ConfigureAwait(false);
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

        #endregion

        #region Private-Methods

        private async Task<string> CreateSessionAsync(CancellationToken token)
        {
            string endpoint = BuildAbsoluteEndpoint("/session");
            CreateSessionRequest payload = new CreateSessionRequest
            {
                Agent = ResolveAgent(),
                Model = new SessionModel
                {
                    Id = ResolveModelId(),
                    ProviderId = ResolveProviderId()
                },
                Title = "armada-inference"
            };

            using HttpRequestMessage request = BuildPostJsonRequest(endpoint, payload);
            using HttpResponseMessage response = await SendWithTimeoutAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _Logging.Warn(_Header + "session request failed with status code " + (int)response.StatusCode);
                return string.Empty;
            }

            string responseBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            CreateSessionResponse? parsed = JsonSerializer.Deserialize<CreateSessionResponse>(responseBody, _JsonOptions);
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.Id))
            {
                _Logging.Warn(_Header + "session response missing id");
                return string.Empty;
            }

            return parsed.Id;
        }

        private async Task<string> SendMessageAsync(string sessionId, string systemPrompt, string userMessage, CancellationToken token)
        {
            string endpoint = BuildAbsoluteEndpoint("/session/" + Uri.EscapeDataString(sessionId) + "/message");
            SendMessageRequest payload = new SendMessageRequest
            {
                Agent = ResolveAgent(),
                Model = new MessageModel
                {
                    ProviderId = ResolveProviderId(),
                    ModelId = ResolveModelId()
                },
                System = systemPrompt,
                Parts = new List<MessagePartRequest>
                {
                    new MessagePartRequest
                    {
                        Type = "text",
                        Text = userMessage
                    }
                }
            };

            using HttpRequestMessage request = BuildPostJsonRequest(endpoint, payload);
            using HttpResponseMessage response = await SendWithTimeoutAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _Logging.Warn(_Header + "message request failed with status code " + (int)response.StatusCode);
                return string.Empty;
            }

            string responseBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            SendMessageResponse? parsed = JsonSerializer.Deserialize<SendMessageResponse>(responseBody, _JsonOptions);
            if (parsed?.Parts == null || parsed.Parts.Count < 1)
            {
                _Logging.Warn(_Header + "message response missing parts");
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < parsed.Parts.Count; i++)
            {
                MessagePartResponse? part = parsed.Parts[i];
                if (part == null) continue;
                if (!string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(part.Text)) continue;
                builder.Append(part.Text);
            }

            return builder.ToString();
        }

        private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage request, CancellationToken token)
        {
            using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_Settings.OpenCodeServer.RequestTimeoutSeconds));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            return await _Http.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
        }

        private HttpRequestMessage BuildPostJsonRequest<TBody>(string endpoint, TBody payload)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            ApplyBasicAuthorization(request);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, _JsonOptions),
                Encoding.UTF8,
                "application/json");
            return request;
        }

        private void ApplyBasicAuthorization(HttpRequestMessage request)
        {
            string password = _Settings.OpenCodeServer.Password ?? string.Empty;
            if (string.IsNullOrWhiteSpace(password)) return;

            string username = _Settings.OpenCodeServer.Username;
            if (string.IsNullOrWhiteSpace(username)) username = "opencode";

            string raw = username + ":" + password;
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64);
        }

        private string BuildAbsoluteEndpoint(string relativePath)
        {
            string baseUrl = _Settings.OpenCodeServer.BaseUrl ?? "http://127.0.0.1:4096";
            string normalizedBase = baseUrl.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(normalizedBase))
                normalizedBase = "http://127.0.0.1:4096";
            return normalizedBase + relativePath;
        }

        private string ResolveProviderId()
        {
            string providerId = _Settings.OpenCodeServer.ProviderId;
            if (string.IsNullOrWhiteSpace(providerId)) providerId = "opencode";
            return providerId;
        }

        private string ResolveModelId()
        {
            string modelId = _Settings.OpenCodeServer.ModelId;
            if (string.IsNullOrWhiteSpace(modelId)) modelId = "deepseek-v4-flash-free";
            return modelId;
        }

        private string ResolveAgent()
        {
            string agent = _Settings.OpenCodeServer.Agent;
            if (string.IsNullOrWhiteSpace(agent)) agent = "summary";
            return agent;
        }

        #endregion

        #region Private-Types

        private sealed class CreateSessionRequest
        {
            [JsonPropertyName("agent")]
            public string Agent { get; set; } = string.Empty;

            [JsonPropertyName("model")]
            public SessionModel Model { get; set; } = new SessionModel();

            [JsonPropertyName("title")]
            public string Title { get; set; } = string.Empty;
        }

        private sealed class SessionModel
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("providerID")]
            public string ProviderId { get; set; } = string.Empty;
        }

        private sealed class CreateSessionResponse
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;
        }

        private sealed class SendMessageRequest
        {
            [JsonPropertyName("agent")]
            public string Agent { get; set; } = string.Empty;

            [JsonPropertyName("model")]
            public MessageModel Model { get; set; } = new MessageModel();

            [JsonPropertyName("system")]
            public string System { get; set; } = string.Empty;

            [JsonPropertyName("parts")]
            public List<MessagePartRequest> Parts { get; set; } = new List<MessagePartRequest>();
        }

        private sealed class MessageModel
        {
            [JsonPropertyName("providerID")]
            public string ProviderId { get; set; } = string.Empty;

            [JsonPropertyName("modelID")]
            public string ModelId { get; set; } = string.Empty;
        }

        private sealed class MessagePartRequest
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = string.Empty;

            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;
        }

        private sealed class SendMessageResponse
        {
            [JsonPropertyName("parts")]
            public List<MessagePartResponse>? Parts { get; set; }
        }

        private sealed class MessagePartResponse
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }
        }

        #endregion
    }
}
