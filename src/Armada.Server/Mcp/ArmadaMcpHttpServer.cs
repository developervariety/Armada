namespace Armada.Server.Mcp
{
    using System.Globalization;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using ModelContextProtocol;
    using ModelContextProtocol.AspNetCore;
    using ModelContextProtocol.Protocol;
    using ModelContextProtocol.Server;

    /// <summary>
    /// Hosts Armada's MCP tools over the official MCP C# SDK Streamable HTTP transport.
    /// </summary>
    public sealed class ArmadaMcpHttpServer : IAsyncDisposable
    {
        // Some MCP clients do not follow nextCursor during initial tool discovery. Keep the
        // normal Armada catalog on one page, while retaining pagination for unusually large
        // extension catalogs.
        private const int _PAGE_SIZE = 500;

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _Hostname;
        private readonly int _Port;
        private readonly object _Sync = new object();
        private readonly Dictionary<string, ToolRegistration> _Tools =
            new Dictionary<string, ToolRegistration>(StringComparer.Ordinal);

        private WebApplication? _Application;

        /// <summary>
        /// Gets or sets the advertised MCP server name.
        /// </summary>
        public string ServerName { get; set; } = "Armada";

        /// <summary>
        /// Gets or sets the advertised MCP server version.
        /// </summary>
        public string ServerVersion { get; set; } = "1.0.0";

        /// <summary>
        /// Create an MCP HTTP server.
        /// </summary>
        /// <param name="hostname">Hostname or address to bind.</param>
        /// <param name="port">TCP port to bind.</param>
        public ArmadaMcpHttpServer(string hostname, int port)
        {
            if (String.IsNullOrWhiteSpace(hostname))
                throw new ArgumentNullException(nameof(hostname));
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            _Hostname = hostname.Trim();
            _Port = port;
        }

        /// <summary>
        /// Register or replace one MCP tool.
        /// </summary>
        public void RegisterTool(
            string name,
            string description,
            object inputSchema,
            Func<JsonElement?, Task<object>> handler)
        {
            if (String.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrWhiteSpace(description))
                throw new ArgumentNullException(nameof(description));
            if (inputSchema == null)
                throw new ArgumentNullException(nameof(inputSchema));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (_Application != null)
                throw new InvalidOperationException("MCP tools cannot be registered after the HTTP server starts.");

            JsonElement schema = inputSchema is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(inputSchema, _JsonOptions);

            // Constructing the official SDK model validates that this is an MCP object schema.
            Tool protocolTool = new Tool
            {
                Name = name.Trim(),
                Description = description.Trim(),
                InputSchema = schema
            };

            lock (_Sync)
            {
                _Tools[protocolTool.Name] = new ToolRegistration(protocolTool, handler);
            }
        }

        /// <summary>
        /// Start the Streamable HTTP server.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_Application != null)
                throw new InvalidOperationException("The MCP HTTP server is already started.");

            WebApplicationOptions applicationOptions = new WebApplicationOptions
            {
                ApplicationName = typeof(ArmadaMcpHttpServer).Assembly.GetName().Name,
                Args = Array.Empty<string>()
            };
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(applicationOptions);
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://" + _Hostname + ":" + _Port.ToString(CultureInfo.InvariantCulture));

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation
                    {
                        Name = ServerName,
                        Version = ServerVersion
                    };
                })
                .WithHttpTransport(options => options.Stateless = true)
                .WithListToolsHandler(ListToolsAsync)
                .WithCallToolHandler(CallToolAsync);

            WebApplication application = builder.Build();
            application.MapMcp("/mcp");
            application.MapMcp("/rpc");

            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            _Application = application;
        }

        /// <summary>
        /// Stop the Streamable HTTP server.
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            WebApplication? application = _Application;
            _Application = null;
            if (application == null) return;

            await application.StopAsync(cancellationToken).ConfigureAwait(false);
            await application.DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return new ValueTask(StopAsync());
        }

        private ValueTask<ListToolsResult> ListToolsAsync(
            RequestContext<ListToolsRequestParams> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // MCP permits tools/list to omit params. Some clients use that form for
            // initial discovery, so treat a missing parameter object as an empty one.
            int offset = ParseCursor(request.Params?.Cursor);
            List<Tool> tools;
            lock (_Sync)
            {
                tools = _Tools.Values
                    .Select(registration => registration.Tool)
                    .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                    .ToList();
            }

            if (offset > tools.Count)
                throw new McpProtocolException("The tools/list cursor is out of range.", McpErrorCode.InvalidParams);

            List<Tool> page = tools.Skip(offset).Take(_PAGE_SIZE).ToList();
            int nextOffset = offset + page.Count;
            ListToolsResult result = new ListToolsResult
            {
                Tools = page,
                NextCursor = nextOffset < tools.Count
                    ? nextOffset.ToString(CultureInfo.InvariantCulture)
                    : null,
                TimeToLive = TimeSpan.FromMinutes(5),
                CacheScope = CacheScope.Public
            };

            return ValueTask.FromResult(result);
        }

        private async ValueTask<CallToolResult> CallToolAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken)
        {
            ToolRegistration? registration;
            lock (_Sync)
            {
                _Tools.TryGetValue(request.Params.Name, out registration);
            }

            if (registration == null)
                throw new McpProtocolException(
                    "Tool not found: " + request.Params.Name,
                    McpErrorCode.InvalidParams);

            JsonElement? arguments = request.Params.Arguments == null
                ? null
                : JsonSerializer.SerializeToElement(request.Params.Arguments, _JsonOptions);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                object result = await registration.Handler(arguments).ConfigureAwait(false);
                return ConvertToolResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InternalError);
            }
        }

        private static CallToolResult ConvertToolResult(object result)
        {
            if (result is CallToolResult callToolResult)
                return callToolResult;

            JsonElement serialized = result is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(result, _JsonOptions);

            if (serialized.ValueKind == JsonValueKind.Object
                && serialized.TryGetProperty("content", out _))
            {
                CallToolResult? protocolResult =
                    JsonSerializer.Deserialize<CallToolResult>(serialized.GetRawText(), _JsonOptions);
                if (protocolResult != null)
                    return protocolResult;
            }

            string text = result is string stringResult
                ? stringResult
                : serialized.GetRawText();
            return CreateTextResult(text, isError: false);
        }

        private static CallToolResult CreateTextResult(string text, bool isError)
        {
            return new CallToolResult
            {
                Content = new List<ContentBlock>
                {
                    new TextContentBlock { Text = text ?? "" }
                },
                IsError = isError
            };
        }

        private static int ParseCursor(string? cursor)
        {
            if (String.IsNullOrWhiteSpace(cursor)) return 0;
            if (!Int32.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int offset)
                || offset < 0)
            {
                throw new McpProtocolException(
                    "The tools/list cursor is invalid.",
                    McpErrorCode.InvalidParams);
            }

            return offset;
        }

        private sealed record ToolRegistration(
            Tool Tool,
            Func<JsonElement?, Task<object>> Handler);
    }
}
