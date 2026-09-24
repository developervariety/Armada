namespace Armada.Test.Automated
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// Sends one payload to a configuration record through each of the three write surfaces of a running
    /// admiral: REST over HTTP, MCP over its HTTP transport (so the argument normalizer runs exactly as it
    /// does for a real client), and a WebSocket command. Payloads are raw JSON so an explicit null or an
    /// empty string reaches the server as written.
    /// </summary>
    public sealed class ConfigurationSurfaces
    {
        #region Private-Members

        private readonly HttpClient _AuthClient;
        private readonly HttpClient _McpClient;
        private readonly int _RestPort;
        private readonly string _ApiKey;
        private int _McpRequestId = 1000;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="authClient">REST client carrying the administrator API key.</param>
        /// <param name="mcpClient">MCP client carrying the administrator API key.</param>
        /// <param name="restPort">REST port, which also serves the WebSocket endpoint.</param>
        /// <param name="apiKey">Administrator API key.</param>
        public ConfigurationSurfaces(HttpClient authClient, HttpClient mcpClient, int restPort, string apiKey)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _McpClient = mcpClient ?? throw new ArgumentNullException(nameof(mcpClient));
            _RestPort = restPort;
            _ApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Send a REST request. A null bearer uses the administrator API key.
        /// </summary>
        public async Task<SurfaceReply> RestAsync(HttpMethod method, string path, string? jsonBody, string? bearer = null)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(method, path))
            {
                if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                HttpClient client = _AuthClient;
                HttpClient? owned = null;
                if (bearer != null)
                {
                    owned = new HttpClient { BaseAddress = _AuthClient.BaseAddress };
                    owned.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    client = owned;
                }

                try
                {
                    using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                    {
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return SurfaceReply.From((int)response.StatusCode, !response.IsSuccessStatusCode, text);
                    }
                }
                finally
                {
                    owned?.Dispose();
                }
            }
        }

        /// <summary>
        /// Call an MCP tool through the HTTP transport. A null bearer uses the administrator API key.
        /// </summary>
        public async Task<SurfaceReply> McpAsync(string tool, string argumentsJson, string? bearer = null)
        {
            int id = Interlocked.Increment(ref _McpRequestId);
            string body = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + argumentsJson + "}}";
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/mcp"))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.Add("Accept", "application/json, text/event-stream");
                HttpClient client = _McpClient;
                HttpClient? owned = null;
                if (bearer != null)
                {
                    owned = new HttpClient { BaseAddress = _McpClient.BaseAddress };
                    owned.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    client = owned;
                }

                try
                {
                    using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                    {
                        string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode) return SurfaceReply.From((int)response.StatusCode, true, raw);
                        JsonElement envelope = JsonSerializer.Deserialize<JsonElement>(ExtractJsonRpc(raw, response.Content.Headers.ContentType?.MediaType));
                        if (envelope.TryGetProperty("error", out JsonElement rpcError)) return SurfaceReply.From(500, true, rpcError.GetRawText());
                        JsonElement result = envelope.GetProperty("result");
                        bool isError = result.TryGetProperty("isError", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
                        string text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
                        return SurfaceReply.From(200, isError, text);
                    }
                }
                finally
                {
                    owned?.Dispose();
                }
            }
        }

        /// <summary>
        /// Send one WebSocket command and return its reply. A null bearer authenticates with the administrator API key.
        /// </summary>
        public async Task<SurfaceReply> WsAsync(string action, string? id, string? dataJson, string? bearer = null)
        {
            using (ClientWebSocket socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(new Uri("ws://localhost:" + _RestPort + "/ws"), CancellationToken.None).ConfigureAwait(false);
                string auth = bearer == null
                    ? "{\"Route\":\"authenticate\",\"apiKey\":" + JsonSerializer.Serialize(_ApiKey) + "}"
                    : "{\"Route\":\"authenticate\",\"token\":" + JsonSerializer.Serialize(bearer) + "}";
                await SendAsync(socket, auth).ConfigureAwait(false);
                JsonElement authReply = await ReceiveAsync(socket).ConfigureAwait(false);
                if (authReply.GetProperty("type").GetString() != "auth.result")
                    throw new InvalidOperationException("WebSocket authentication failed: " + authReply.GetRawText());

                StringBuilder command = new StringBuilder();
                command.Append("{\"Route\":\"command\",\"action\":").Append(JsonSerializer.Serialize(action));
                if (id != null) command.Append(",\"id\":").Append(JsonSerializer.Serialize(id));
                if (dataJson != null) command.Append(",\"data\":").Append(dataJson);
                command.Append('}');
                await SendAsync(socket, command.ToString()).ConfigureAwait(false);

                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    JsonElement frame = await ReceiveAsync(socket).ConfigureAwait(false);
                    string? type = frame.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;
                    if (type == "command.result")
                        return SurfaceReply.From(200, false, frame.TryGetProperty("data", out JsonElement data) ? data.GetRawText() : "null");
                    if (type == "command.error")
                        return SurfaceReply.From(400, true, frame.GetRawText());
                }

                throw new TimeoutException("No reply to WebSocket command " + action);
            }
        }

        /// <summary>
        /// Create a tenant with a tenant administrator and return that administrator's bearer token.
        /// </summary>
        public async Task<string> CreateTenantAdministratorTokenAsync(string label)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            HttpResponseMessage tenantResponse = await _AuthClient.PostAsync("/api/v1/tenants",
                JsonHelper.ToJsonContent(new { Name = label + "-" + suffix })).ConfigureAwait(false);
            tenantResponse.EnsureSuccessStatusCode();
            TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(tenantResponse).ConfigureAwait(false);

            HttpResponseMessage userResponse = await _AuthClient.PostAsync("/api/v1/users",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenant.Id,
                    Email = label + "-" + suffix + "@parity.armada",
                    PasswordSha256 = UserMaster.ComputePasswordHash("parity-" + suffix),
                    IsTenantAdmin = true
                })).ConfigureAwait(false);
            userResponse.EnsureSuccessStatusCode();
            UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);

            HttpResponseMessage credentialResponse = await _AuthClient.PostAsync("/api/v1/credentials",
                JsonHelper.ToJsonContent(new { TenantId = tenant.Id, UserId = user.Id, Name = label + "-cred" })).ConfigureAwait(false);
            credentialResponse.EnsureSuccessStatusCode();
            Credential credential = await JsonHelper.DeserializeAsync<Credential>(credentialResponse).ConfigureAwait(false);
            return credential.BearerToken;
        }

        /// <summary>
        /// Serialize a value to JSON keeping explicit nulls, with camelCase names as MCP and WebSocket clients send.
        /// </summary>
        public static string Json(object value)
        {
            return JsonSerializer.Serialize(value, _PayloadOptions);
        }

        /// <summary>
        /// Find a property by name, ignoring case.
        /// </summary>
        public static bool TryProp(JsonElement element, string name, out JsonElement value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (String.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Read a property as text: a string as written, a boolean as True or False, null or absent as empty.
        /// </summary>
        public static string Text(JsonElement element, string name)
        {
            if (!TryProp(element, name, out JsonElement value)) return "";
            switch (value.ValueKind)
            {
                case JsonValueKind.String: return value.GetString() ?? "";
                case JsonValueKind.True: return "True";
                case JsonValueKind.False: return "False";
                case JsonValueKind.Null: return "";
                default: return value.GetRawText();
            }
        }

        #endregion

        #region Private-Members-Static

        private static readonly JsonSerializerOptions _PayloadOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Private-Methods

        private static async Task SendAsync(ClientWebSocket socket, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }

        private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
        {
            using (MemoryStream stream = new MemoryStream())
            using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                byte[] buffer = new byte[65536];
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("WebSocket closed");
                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                using (JsonDocument document = JsonDocument.Parse(stream.ToArray()))
                {
                    return document.RootElement.Clone();
                }
            }
        }

        private static string ExtractJsonRpc(string body, string? mediaType)
        {
            if (!String.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) return body;
            foreach (string line in body.Split('\n'))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) return line.Substring(5).Trim();
            }

            throw new InvalidDataException("MCP SSE response did not contain a data event.");
        }

        #endregion
    }

    /// <summary>
    /// One surface's reply: a status, whether it refused, and its body.
    /// </summary>
    public sealed class SurfaceReply
    {
        #region Public-Members

        /// <summary>
        /// HTTP status for REST; 200 or 400 for MCP and WebSocket replies.
        /// </summary>
        public int Status { get; set; } = 0;

        /// <summary>
        /// True when the surface refused the request.
        /// </summary>
        public bool IsError { get; set; } = false;

        /// <summary>
        /// Body text.
        /// </summary>
        public string Text { get; set; } = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Build a reply.
        /// </summary>
        public static SurfaceReply From(int status, bool isError, string text)
        {
            // An MCP handler reports a refusal as an Error envelope; the transport may or may not flag it.
            bool envelopeError = false;
            if (!isError && text.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(text))
                    {
                        envelopeError = document.RootElement.TryGetProperty("Error", out JsonElement error)
                            && error.ValueKind == JsonValueKind.String
                            && !String.IsNullOrEmpty(error.GetString());
                    }
                }
                catch (JsonException)
                {
                }
            }

            return new SurfaceReply { Status = status, IsError = isError || envelopeError, Text = text };
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Parse the body.
        /// </summary>
        public JsonElement Json()
        {
            return JsonSerializer.Deserialize<JsonElement>(Text);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Status + (IsError ? " (refused) " : " ") + Text;
        }

        #endregion
    }
}
