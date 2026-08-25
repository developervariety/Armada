namespace Armada.Server.Mcp
{
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Primitives;
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

        // A wake banner rides on every tool result until it is acknowledged, so it stays
        // small on purpose: enough to make the session stop, never the whole board.
        private const int _WAKE_BANNER_MAX_ITEMS = 5;
        private const int _WAKE_BANNER_MAX_ITEM_LENGTH = 400;
        private const int _PARTICIPANT_KEY_MAX_LENGTH = 128;

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Request header naming the coordination-board participant behind a tool call.
        /// The transport is stateless, so each request must carry its own identity;
        /// there is no session the server could read it from.
        /// </summary>
        public const string ParticipantHeaderName = "X-Armada-Participant";

        /// <summary>
        /// Gets the participant identity assigned to the current MCP request.
        /// </summary>
        public static string? CurrentParticipantKey => _RequestParticipantKey.Value;

        // Set by the middleware below and read inside the tool handler. An AsyncLocal
        // flows down the request's async chain, which is what a stateless transport
        // leaves us: the header is the only place the caller's identity exists.
        private static readonly AsyncLocal<string?> _RequestParticipantKey = new AsyncLocal<string?>();

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
        /// Optional lookup for directed wakes waiting on a participant. Called with the
        /// participant key from <see cref="ParticipantHeaderName"/> after a tool produces
        /// its result; each returned string is one pending wake, oldest first.
        /// <para>
        /// This exists because MCP has no channel that can interrupt an agent. A server
        /// cannot push to this transport at all, and no client turns an inbound
        /// notification into a model turn. A tool result is the only content the model
        /// is guaranteed to read, so a pending wake rides back on whichever tool the
        /// session calls next.
        /// </para>
        /// <para>
        /// Delivery is not acknowledgement. This never marks a wake read, so the banner
        /// repeats until the session calls the acknowledgement tool. A wake that stopped
        /// being shown before it was read would be a lost wake.
        /// </para>
        /// </summary>
        public Func<string, CancellationToken, Task<IReadOnlyList<string>>>? PendingWakeProvider { get; set; }

        /// <summary>
        /// Optional bearer token required for every request. Leave null only for a private listener.
        /// </summary>
        public string? BearerToken { get; set; } = null;

        /// <summary>
        /// Optional server-assigned participant identity. When set, the server rejects a different
        /// request header and does not trust the caller to select its Armada identity.
        /// </summary>
        public string? FixedParticipantKey { get; set; } = null;

        /// <summary>
        /// Optional audit sink. It receives each tool outcome and, when required,
        /// a Started record before the handler runs.
        /// </summary>
        public Func<McpToolCallAudit, CancellationToken, Task>? ToolCallAuditSink { get; set; } = null;

        /// <summary>
        /// When true, a durable Started audit must succeed before a tool handler can run.
        /// Outcome audit failures also fail the response. The Started record then shows
        /// that the outcome is incomplete.
        /// </summary>
        public bool RequireToolCallAudit { get; set; } = false;

        /// <summary>
        /// Tools that must not receive the appended wake banner, because they already
        /// return pending wakes in their own structured payload. The server holds the
        /// set but never populates it; naming tools is the caller's policy.
        /// </summary>
        public ISet<string> WakeBannerExcludedTools { get; } = new HashSet<string>(StringComparer.Ordinal);

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

            // Capture the caller's participant key before the MCP handler runs, so the
            // tool handler can read it without depending on SDK transport internals.
            application.Use(async (context, next) =>
            {
                if (!IsAuthorized(context))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("Unauthorized").ConfigureAwait(false);
                    return;
                }

                string? suppliedParticipant = ReadParticipantHeader(context);
                if (!String.IsNullOrEmpty(FixedParticipantKey)
                    && !String.IsNullOrEmpty(suppliedParticipant)
                    && !String.Equals(FixedParticipantKey, suppliedParticipant, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("The participant identity is assigned by the server.").ConfigureAwait(false);
                    return;
                }

                _RequestParticipantKey.Value = FixedParticipantKey ?? suppliedParticipant;
                try
                {
                    await next(context).ConfigureAwait(false);
                }
                finally
                {
                    _RequestParticipantKey.Value = null;
                }
            });

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
            arguments = McpToolArgumentNormalizer.Normalize(arguments, registration.Tool.InputSchema);

            if (RequireToolCallAudit)
            {
                try
                {
                    await WriteToolAuditAsync(
                        request.Params.Name,
                        arguments,
                        "Started",
                        false,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new McpProtocolException(
                        "The required tool-call audit is unavailable: " + ex.Message,
                        McpErrorCode.InternalError);
                }
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                object result = await registration.Handler(arguments).ConfigureAwait(false);
                CallToolResult toolResult = ConvertToolResult(result);
                await WriteToolAuditAsync(
                    request.Params.Name,
                    arguments,
                    "Succeeded",
                    true,
                    null,
                    cancellationToken).ConfigureAwait(false);
                return await AppendPendingWakesAsync(
                    request.Params.Name,
                    toolResult,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await WriteToolAuditAsync(
                    request.Params.Name,
                    arguments,
                    "Failed",
                    false,
                    ex.Message,
                    cancellationToken).ConfigureAwait(false);
                throw new McpProtocolException(ex.Message, McpErrorCode.InternalError);
            }
        }

        private bool IsAuthorized(HttpContext context)
        {
            if (String.IsNullOrEmpty(BearerToken)) return true;
            string authorization = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string supplied = authorization.Substring(prefix.Length).Trim();
            byte[] expectedBytes = Encoding.UTF8.GetBytes(BearerToken!);
            byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
            return expectedBytes.Length == suppliedBytes.Length
                && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }

        private async Task WriteToolAuditAsync(
            string toolName,
            JsonElement? arguments,
            string phase,
            bool succeeded,
            string? error,
            CancellationToken cancellationToken)
        {
            Func<McpToolCallAudit, CancellationToken, Task>? sink = ToolCallAuditSink;
            if (sink == null) return;
            try
            {
                string? argumentsJson = arguments?.GetRawText();
                if (argumentsJson != null && argumentsJson.Length > 8192)
                    argumentsJson = argumentsJson.Substring(0, 8192);
                McpToolCallAudit audit = new McpToolCallAudit
                {
                    ToolName = toolName,
                    ParticipantKey = _RequestParticipantKey.Value,
                    ArgumentsJson = argumentsJson,
                    Phase = phase,
                    Succeeded = succeeded,
                    Error = error,
                    CompletedUtc = DateTime.UtcNow
                };
                await sink(audit, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (RequireToolCallAudit) throw;
                // Auditing is best effort at the transport layer. The restricted gateway sink
                // writes to Armada's durable event store and reports its own failures.
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

        private static string? ReadParticipantHeader(HttpContext context)
        {
            if (!context.Request.Headers.TryGetValue(ParticipantHeaderName, out StringValues values))
                return null;

            string? value = values.Count == 0 ? null : values[0];
            if (String.IsNullOrWhiteSpace(value)) return null;

            // The key is echoed into a tool result the model reads, so refuse anything
            // that is not a plain identifier rather than passing control characters on.
            string trimmed = value!.Trim();
            if (trimmed.Length > _PARTICIPANT_KEY_MAX_LENGTH) return null;
            foreach (char character in trimmed)
            {
                bool allowed = Char.IsLetterOrDigit(character)
                    || character == '-' || character == '_' || character == '.' || character == ':';
                if (!allowed) return null;
            }

            return trimmed;
        }

        /// <summary>
        /// Append pending directed wakes to a tool result as one extra text block. Any
        /// failure here is swallowed: a wake is a courtesy, and losing the caller's
        /// actual tool result to a board lookup would be the worse outcome.
        /// </summary>
        private async Task<CallToolResult> AppendPendingWakesAsync(
            string toolName,
            CallToolResult result,
            CancellationToken cancellationToken)
        {
            Func<string, CancellationToken, Task<IReadOnlyList<string>>>? provider = PendingWakeProvider;
            if (provider == null) return result;

            string? participantKey = _RequestParticipantKey.Value;
            if (String.IsNullOrEmpty(participantKey)) return result;
            if (WakeBannerExcludedTools.Contains(toolName)) return result;

            try
            {
                IReadOnlyList<string> wakes =
                    await provider(participantKey!, cancellationToken).ConfigureAwait(false);
                if (wakes == null || wakes.Count == 0) return result;

                // Build a new result rather than appending to the handler's. Nothing
                // returns a shared CallToolResult today, and a banner that accumulated
                // on a cached one would be a slow leak nobody would look for here.
                List<ContentBlock> content = result.Content == null
                    ? new List<ContentBlock>()
                    : new List<ContentBlock>(result.Content);
                content.Add(new TextContentBlock { Text = BuildWakeBanner(wakes) });

                return new CallToolResult
                {
                    Content = content,
                    StructuredContent = result.StructuredContent,
                    IsError = result.IsError
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return result;
            }
        }

        private static string BuildWakeBanner(IReadOnlyList<string> wakes)
        {
            StringBuilder banner = new StringBuilder();
            banner.Append("[ARMADA WAKE] ")
                .Append(wakes.Count.ToString(CultureInfo.InvariantCulture))
                .Append(wakes.Count == 1 ? " directed message is" : " directed messages are")
                .Append(" waiting on you. PAUSE the current work, address them, then acknowledge each with armada_mark_signal_read.");

            int shown = 0;
            foreach (string wake in wakes)
            {
                if (shown == _WAKE_BANNER_MAX_ITEMS) break;
                if (String.IsNullOrWhiteSpace(wake)) continue;

                string text = wake.Trim();
                if (text.Length > _WAKE_BANNER_MAX_ITEM_LENGTH)
                    text = text.Substring(0, _WAKE_BANNER_MAX_ITEM_LENGTH) + "...";

                banner.Append("\n- ").Append(text);
                shown++;
            }

            if (wakes.Count > shown)
            {
                banner.Append("\n- (")
                    .Append((wakes.Count - shown).ToString(CultureInfo.InvariantCulture))
                    .Append(" more; read the board for the rest)");
            }

            return banner.ToString();
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
