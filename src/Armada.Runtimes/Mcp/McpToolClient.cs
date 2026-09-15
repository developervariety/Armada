namespace Armada.Runtimes.Mcp
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
    using SyslogLogging;

    /// <summary>
    /// A minimal client for the MCP Streamable HTTP transport: <c>initialize</c>, paginated <c>tools/list</c> and
    /// <c>tools/call</c>, reading plain JSON or event-stream bodies.
    ///
    /// The only credential it can present is a session token in the <c>X-Token</c> header. It never sends an
    /// Authorization header or an API key, so a caller-scoped chat token cannot be replaced by a broader
    /// credential, and the server applies the token owner's own scope and tool access to every request.
    /// </summary>
    public sealed class McpToolClient : IDisposable
    {
        #region Public-Members

        /// <summary>
        /// Header that carries the session token.
        /// </summary>
        public const string SessionTokenHeader = "X-Token";

        /// <summary>
        /// Endpoint this client targets.
        /// </summary>
        public string Endpoint => _Endpoint;

        #endregion

        #region Private-Members

        private const int _MaxToolPages = 50;
        private const int _MaxResponseBytes = 4 * 1024 * 1024;

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _Http;
        private readonly string _Endpoint;
        private readonly LoggingModule? _Logging;
        private readonly string _Header = "[McpToolClient] ";
        private string? _SessionId = null;
        private int _RpcId = 0;
        private bool _Disposed = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate a client for one MCP endpoint.
        /// </summary>
        /// <param name="endpoint">Absolute endpoint URL.</param>
        /// <param name="sessionToken">Session token sent as X-Token on every request; null sends no credential.</param>
        /// <param name="logging">Optional logging module.</param>
        /// <param name="timeoutSeconds">Per-request timeout in seconds; clamped to 5..600.</param>
        public McpToolClient(string endpoint, string? sessionToken, LoggingModule? logging = null, int timeoutSeconds = 100)
        {
            if (String.IsNullOrWhiteSpace(endpoint)) throw new ArgumentNullException(nameof(endpoint));

            _Endpoint = endpoint.Trim();
            _Logging = logging;
            _Http = new HttpClient();
            _Http.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600));
            if (!String.IsNullOrWhiteSpace(sessionToken))
                _Http.DefaultRequestHeaders.TryAddWithoutValidation(SessionTokenHeader, sessionToken.Trim());
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Open the MCP session: send <c>initialize</c>, keep a server-assigned session id when one is returned,
        /// then acknowledge with <c>notifications/initialized</c>.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task InitializeAsync(CancellationToken token = default)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new Dictionary<string, object>(),
                ["clientInfo"] = new Dictionary<string, object> { ["name"] = "armada-api-runtime", ["version"] = "1.0" }
            };
            await SendAsync<McpInitializeResult>("initialize", parameters, token).ConfigureAwait(false);
            await SendNotificationAsync("notifications/initialized", token).ConfigureAwait(false);
        }

        /// <summary>
        /// List every tool the server offers this caller, following pagination.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The tools; empty when the server offers none.</returns>
        public async Task<List<McpRemoteTool>> ListToolsAsync(CancellationToken token = default)
        {
            List<McpRemoteTool> tools = new List<McpRemoteTool>();
            string? cursor = null;

            for (int page = 0; page < _MaxToolPages; page++)
            {
                Dictionary<string, object> parameters = new Dictionary<string, object>();
                if (!String.IsNullOrEmpty(cursor)) parameters["cursor"] = cursor;

                McpToolsListResult result = await SendAsync<McpToolsListResult>("tools/list", parameters, token).ConfigureAwait(false);
                if (result.Tools != null)
                {
                    foreach (McpToolDescriptor descriptor in result.Tools)
                    {
                        if (descriptor == null || String.IsNullOrWhiteSpace(descriptor.Name)) continue;
                        McpRemoteTool tool = new McpRemoteTool();
                        tool.Name = descriptor.Name.Trim();
                        tool.Description = descriptor.Description ?? String.Empty;
                        if (descriptor.InputSchema != null) tool.InputSchema = descriptor.InputSchema;
                        tools.Add(tool);
                    }
                }

                if (String.IsNullOrEmpty(result.NextCursor)) return tools;
                cursor = result.NextCursor;
            }

            throw new McpClientException("MCP tools/list from " + _Endpoint + " did not finish within " + _MaxToolPages + " pages.");
        }

        /// <summary>
        /// Call a tool and return the text of its result.
        /// </summary>
        /// <param name="name">Tool name as advertised.</param>
        /// <param name="argumentsJson">Arguments as a JSON object; null or blank sends no arguments.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The tool result.</returns>
        public async Task<McpToolCallResult> CallToolAsync(string name, string? argumentsJson, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));

            Dictionary<string, object> arguments;
            try
            {
                arguments = String.IsNullOrWhiteSpace(argumentsJson)
                    ? new Dictionary<string, object>()
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(argumentsJson, _JsonOptions) ?? new Dictionary<string, object>();
            }
            catch (JsonException ex)
            {
                throw new McpClientException("Tool arguments for " + name + " are not a JSON object.", ex);
            }

            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                ["name"] = name,
                ["arguments"] = arguments
            };

            McpCallToolResult result = await SendAsync<McpCallToolResult>("tools/call", parameters, token).ConfigureAwait(false);
            McpToolCallResult callResult = new McpToolCallResult();
            callResult.IsError = result.IsError == true;

            StringBuilder text = new StringBuilder();
            if (result.Content != null)
            {
                foreach (McpContentBlock block in result.Content)
                {
                    if (block == null) continue;
                    if (text.Length > 0) text.Append('\n');
                    if (String.Equals(block.Type, "text", StringComparison.Ordinal))
                        text.Append(block.Text ?? String.Empty);
                    else
                        text.Append("[" + (block.Type ?? "unknown") + " content omitted]");
                }
            }

            callResult.Text = text.ToString();
            return callResult;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_Disposed) return;
            _Disposed = true;
            _Http.Dispose();
        }

        #endregion

        #region Private-Methods

        private async Task<TResult> SendAsync<TResult>(string method, object parameters, CancellationToken token) where TResult : class
        {
            McpRpcRequest payload = new McpRpcRequest();
            payload.Id = Interlocked.Increment(ref _RpcId);
            payload.Method = method;
            payload.Params = parameters;

            using (HttpRequestMessage request = BuildRequest(payload))
            using (HttpResponseMessage response = await _Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                string body = await ReadBoundedAsync(response, token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    throw new McpClientException("MCP " + method + " to " + _Endpoint + " was refused with HTTP " + (int)response.StatusCode + ".", (int)response.StatusCode);

                if (response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values))
                {
                    foreach (string value in values)
                    {
                        if (!String.IsNullOrWhiteSpace(value))
                        {
                            _SessionId = value;
                            break;
                        }
                    }
                }

                string? json = ExtractEnvelope(body);
                if (json == null) throw new McpClientException("MCP " + method + " to " + _Endpoint + " returned no JSON-RPC response.");

                McpRpcResponse<TResult>? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<McpRpcResponse<TResult>>(json, _JsonOptions);
                }
                catch (JsonException ex)
                {
                    throw new McpClientException("MCP " + method + " to " + _Endpoint + " returned malformed JSON.", ex);
                }

                if (envelope == null) throw new McpClientException("MCP " + method + " to " + _Endpoint + " returned an empty response.");
                if (envelope.Error != null)
                    throw new McpClientException("MCP " + method + " failed: " + (envelope.Error.Message ?? ("error " + envelope.Error.Code)));
                if (envelope.Result == null) throw new McpClientException("MCP " + method + " to " + _Endpoint + " returned no result.");
                return envelope.Result;
            }
        }

        private async Task SendNotificationAsync(string method, CancellationToken token)
        {
            McpRpcRequest payload = new McpRpcRequest();
            payload.Method = method;
            payload.Params = new Dictionary<string, object>();

            using (HttpRequestMessage request = BuildRequest(payload))
            using (HttpResponseMessage response = await _Http.SendAsync(request, token).ConfigureAwait(false))
            {
                // A refused acknowledgement does not end the session: the server has already answered initialize.
                if (!response.IsSuccessStatusCode)
                    _Logging?.Debug(_Header + method + " to " + _Endpoint + " returned HTTP " + (int)response.StatusCode);
            }
        }

        private HttpRequestMessage BuildRequest(McpRpcRequest payload)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _Endpoint);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, _JsonOptions), Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (!String.IsNullOrEmpty(_SessionId)) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _SessionId);
            return request;
        }

        private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
        {
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (bytes.Length > _MaxResponseBytes)
                throw new McpClientException("MCP response exceeded " + _MaxResponseBytes + " bytes.");
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Return the JSON-RPC object from a plain JSON body, or the first JSON data frame of an event-stream body.
        /// </summary>
        private static string? ExtractEnvelope(string body)
        {
            if (String.IsNullOrWhiteSpace(body)) return null;

            string trimmed = body.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal)) return trimmed;

            foreach (string rawLine in body.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string data = line.Substring(5).Trim();
                if (data.StartsWith("{", StringComparison.Ordinal)) return data;
            }

            return null;
        }

        #endregion
    }
}
