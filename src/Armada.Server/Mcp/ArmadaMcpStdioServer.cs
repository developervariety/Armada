namespace Armada.Server.Mcp
{
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// MCP stdio server that uses Content-Length framed JSON-RPC messages.
    /// </summary>
    public sealed class ArmadaMcpStdioServer
    {
        private const int _MAX_HEADER_BYTES = 8192;
        private const int _MAX_BODY_BYTES = 16 * 1024 * 1024;
        private const string _PROTOCOL_VERSION = "2025-03-26";

        private static readonly Encoding _Utf8 = new UTF8Encoding(false);
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private readonly List<ToolRegistration> _Tools = new List<ToolRegistration>();

        /// <summary>
        /// Gets or sets the MCP protocol version returned by initialize.
        /// </summary>
        public string ProtocolVersion { get; set; } = _PROTOCOL_VERSION;

        /// <summary>
        /// Gets or sets the server name returned by initialize.
        /// </summary>
        public string ServerName { get; set; } = "Armada";

        /// <summary>
        /// Gets or sets the server version returned by initialize.
        /// </summary>
        public string ServerVersion { get; set; } = "1.0.0";

        /// <summary>
        /// Registers a tool for tools/list and tools/call.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Tool input schema.</param>
        /// <param name="handler">Tool invocation handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, Task<object>> handler)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name is required.", nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _Tools.RemoveAll(tool => String.Equals(tool.Name, name, StringComparison.Ordinal));
            _Tools.Add(new ToolRegistration(name, description, inputSchema, handler));
        }

        /// <summary>
        /// Runs the stdio server using process stdin and stdout.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task RunAsync(CancellationToken token = default)
        {
            using Stream input = Console.OpenStandardInput();
            using Stream output = Console.OpenStandardOutput();
            await RunAsync(input, output, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs the stdio server using the provided streams.
        /// </summary>
        /// <param name="input">Input stream containing line-delimited or Content-Length framed JSON-RPC messages.</param>
        /// <param name="output">Output stream receiving Content-Length framed JSON-RPC responses.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task RunAsync(Stream input, Stream output, CancellationToken token = default)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            while (!token.IsCancellationRequested)
            {
                string? message;
                try { message = await ReadMessageAsync(input, token).ConfigureAwait(false); }
                catch (InvalidDataException ex)
                {
                    string error = CreateErrorResponse(null, -32700, ex.Message);
                    await WriteMessageAsync(output, error, token).ConfigureAwait(false);
                    return;
                }
                if (message == null) return;

                string? response = await HandleMessageAsync(message).ConfigureAwait(false);
                if (response != null)
                    await WriteMessageAsync(output, response, token).ConfigureAwait(false);
            }
        }

        private async Task<string?> ReadMessageAsync(Stream input, CancellationToken token)
        {
            byte[]? lineBytes = await ReadLineBytesAsync(input, token).ConfigureAwait(false);
            if (lineBytes == null) return null;
            string line = DecodeLine(lineBytes);
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                return line;

            int contentLength = ParseContentLengthLine(line);
            if (contentLength < 0 || contentLength > _MAX_BODY_BYTES)
                throw new InvalidDataException("MCP frame Content-Length is invalid.");

            while (true)
            {
                byte[]? headerLineBytes = await ReadLineBytesAsync(input, token).ConfigureAwait(false);
                if (headerLineBytes == null)
                    throw new InvalidDataException("Unexpected end of stream while reading MCP frame headers.");
                if (DecodeLine(headerLineBytes).Length == 0) break;
            }

            byte[] bodyBytes = await ReadExactBytesAsync(input, contentLength, token).ConfigureAwait(false);
            return _Utf8.GetString(bodyBytes);
        }

        private static async Task<byte[]?> ReadLineBytesAsync(Stream input, CancellationToken token)
        {
            MemoryStream line = new MemoryStream();
            byte[] buffer = new byte[1];
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false);
                if (read == 0)
                {
                    if (line.Length == 0) return null;
                    break;
                }

                byte value = buffer[0];
                line.WriteByte(value);
                if (value == 10) break;
                if (line.Length > _MAX_HEADER_BYTES)
                    throw new InvalidDataException("MCP message line is too large.");
            }

            return line.ToArray();
        }

        private static async Task<byte[]> ReadExactBytesAsync(Stream input, int length, CancellationToken token)
        {
            byte[] bytes = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = await input.ReadAsync(bytes.AsMemory(offset, length - offset), token).ConfigureAwait(false);
                if (read == 0)
                    throw new InvalidDataException("Unexpected end of stream while reading MCP frame body.");
                offset += read;
            }

            return bytes;
        }

        private static string DecodeLine(byte[] bytes)
        {
            int length = bytes.Length;
            if (length > 0 && bytes[length - 1] == 10) length--;
            if (length > 0 && bytes[length - 1] == 13) length--;
            return _Utf8.GetString(bytes, 0, length);
        }

        private static int ParseContentLengthLine(string line)
        {
            int colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) throw new InvalidDataException("MCP frame is missing Content-Length.");
            string value = line.Substring(colonIndex + 1).Trim();
            if (Int32.TryParse(value, out int length)) return length;
            throw new InvalidDataException("MCP frame Content-Length is invalid.");
        }

        private static async Task WriteMessageAsync(Stream output, string message, CancellationToken token)
        {
            byte[] bodyBytes = _Utf8.GetBytes(message);
            byte[] headerBytes = Encoding.ASCII.GetBytes("Content-Length: " + bodyBytes.Length + "\r\n\r\n");
            await output.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), token).ConfigureAwait(false);
            await output.WriteAsync(bodyBytes.AsMemory(0, bodyBytes.Length), token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
        }

        private async Task<string?> HandleMessageAsync(string message)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(message);
                JsonElement request = document.RootElement;
                if (request.ValueKind != JsonValueKind.Object)
                    return CreateErrorResponse(null, -32600, "Invalid JSON-RPC request.");

                JsonNode? id = ExtractId(request);
                bool isNotification = !request.TryGetProperty("id", out _);
                if (!request.TryGetProperty("method", out JsonElement methodElement) || methodElement.ValueKind != JsonValueKind.String)
                    return isNotification ? null : CreateErrorResponse(id, -32600, "JSON-RPC method is required.");

                string method = methodElement.GetString()!;
                JsonElement? parameters = null;
                if (request.TryGetProperty("params", out JsonElement paramsElement))
                    parameters = paramsElement;

                try
                {
                    JsonNode? result = await HandleMethodAsync(method, parameters).ConfigureAwait(false);
                    if (isNotification) return null;

                    return CreateSuccessResponse(id, result);
                }
                catch (JsonRpcInvalidParamsException ex)
                {
                    return isNotification ? null : CreateErrorResponse(id, -32602, ex.Message);
                }
                catch (JsonRpcMethodException ex)
                {
                    return isNotification ? null : CreateErrorResponse(id, -32601, ex.Message);
                }
                catch (JsonRpcToolException ex)
                {
                    return isNotification ? null : CreateErrorResponse(id, -32603, ex.Message);
                }
            }
            catch (JsonException)
            {
                return CreateErrorResponse(null, -32700, "Parse error.");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse(null, -32603, ex.Message);
            }
        }

        private async Task<JsonNode?> HandleMethodAsync(string method, JsonElement? parameters)
        {
            switch (method)
            {
                case "initialize":
                    return CreateInitializeResult();
                case "notifications/initialized":
                    return new JsonObject();
                case "ping":
                    return new JsonObject();
                case "tools/list":
                    return CreateToolsListResult();
                case "tools/call":
                    return await CallToolAsync(parameters).ConfigureAwait(false);
                default:
                    throw new JsonRpcMethodException("Method not found: " + method);
            }
        }

        private JsonObject CreateInitializeResult()
        {
            return new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject()
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = ServerName,
                    ["version"] = ServerVersion
                }
            };
        }

        private JsonObject CreateToolsListResult()
        {
            JsonArray tools = new JsonArray();
            foreach (ToolRegistration registration in _Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = registration.Name,
                    ["description"] = registration.Description,
                    ["inputSchema"] = ToJsonNode(registration.InputSchema) ?? new JsonObject()
                });
            }

            return new JsonObject
            {
                ["tools"] = tools
            };
        }

        private async Task<JsonNode?> CallToolAsync(JsonElement? parameters)
        {
            if (!parameters.HasValue || parameters.Value.ValueKind != JsonValueKind.Object)
                throw new JsonRpcInvalidParamsException("tools/call requires object params.");

            JsonElement paramsElement = parameters.Value;
            if (!paramsElement.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
                throw new JsonRpcInvalidParamsException("tools/call requires a tool name.");

            string toolName = nameElement.GetString()!;
            ToolRegistration? registration = _Tools.FirstOrDefault(tool => String.Equals(tool.Name, toolName, StringComparison.Ordinal));
            if (registration == null)
                throw new JsonRpcMethodException("Tool not found: " + toolName);

            JsonElement? arguments = null;
            if (paramsElement.TryGetProperty("arguments", out JsonElement argumentsElement))
                arguments = argumentsElement.Clone();

            object toolResult = await registration.Handler(arguments).ConfigureAwait(false);
            JsonNode? toolNode = ToJsonNode(toolResult);
            if (TryGetToolError(toolNode, out string? errorMessage))
                throw new JsonRpcToolException(errorMessage ?? "Tool returned an error.");

            if (toolNode is JsonObject toolObject && toolObject.ContainsKey("content"))
                return toolObject;

            string text = toolResult is string stringResult
                ? stringResult
                : JsonSerializer.Serialize(toolResult, _JsonOptions);

            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = text
                    }
                }
            };
        }

        private static bool TryGetToolError(JsonNode? node, out string? errorMessage)
        {
            errorMessage = null;
            if (node is not JsonObject obj) return false;

            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                if (!String.Equals(property.Key, "Error", StringComparison.OrdinalIgnoreCase)) continue;

                errorMessage = property.Value == null ? "Tool returned an error." : property.Value.GetValue<string>();
                return true;
            }

            return false;
        }

        private static JsonNode? ExtractId(JsonElement request)
        {
            if (!request.TryGetProperty("id", out JsonElement idElement)) return null;
            return JsonNode.Parse(idElement.GetRawText());
        }

        private static JsonNode? ToJsonNode(object? value)
        {
            if (value == null) return null;
            if (value is JsonNode node) return node.DeepClone();
            if (value is JsonElement element) return JsonNode.Parse(element.GetRawText());
            return JsonSerializer.SerializeToNode(value, _JsonOptions);
        }

        private static string CreateSuccessResponse(JsonNode? id, JsonNode? result)
        {
            JsonObject response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result
            };
            return response.ToJsonString(_JsonOptions);
        }

        private static string CreateErrorResponse(JsonNode? id, int code, string message)
        {
            JsonObject response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
            return response.ToJsonString(_JsonOptions);
        }

        private sealed record ToolRegistration(
            string Name,
            string Description,
            object InputSchema,
            Func<JsonElement?, Task<object>> Handler);

        private sealed class JsonRpcMethodException : Exception
        {
            public JsonRpcMethodException(string message) : base(message)
            {
            }
        }

        private sealed class JsonRpcInvalidParamsException : Exception
        {
            public JsonRpcInvalidParamsException(string message) : base(message)
            {
            }
        }

        private sealed class JsonRpcToolException : Exception
        {
            public JsonRpcToolException(string message) : base(message)
            {
            }
        }
    }
}
