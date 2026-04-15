namespace Armada.Server.WebSocket
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using WatsonWebsocket;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// WebSocket hub for real-time event broadcasting.
    /// Supports subscribe/command routes and broadcasts mission/captain state changes.
    /// Provides full command parity with the REST API and MCP tools.
    /// </summary>
    public class ArmadaWebSocketHub
    {
        #region Private-Members

        private string _Header = "[WebSocketHub] ";
        private LoggingModule _Logging;
        private WatsonWsServer _WsServer;
        private IAdmiralService _Admiral;
        private WebSocketCommandHandler _CommandHandler;
        private TaskCompletionSource<bool> _StopSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the WebSocket hub.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="port">WebSocket port.</param>
        /// <param name="ssl">Whether to enable SSL/TLS.</param>
        /// <param name="admiral">Admiral service for command handling.</param>
        /// <param name="database">Database driver for data access.</param>
        /// <param name="mergeQueue">Merge queue service.</param>
        /// <param name="settings">Optional Armada settings for log/diff paths.</param>
        /// <param name="git">Optional git service for diff generation.</param>
        /// <param name="onStop">Optional callback invoked when stop_server is requested.</param>
        public ArmadaWebSocketHub(LoggingModule logging, int port, bool ssl, IAdmiralService admiral, DatabaseDriver database, IMergeQueueService mergeQueue, ArmadaSettings? settings = null, IGitService? git = null, Action? onStop = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));

            _CommandHandler = new WebSocketCommandHandler(
                _Admiral,
                database ?? throw new ArgumentNullException(nameof(database)),
                mergeQueue ?? throw new ArgumentNullException(nameof(mergeQueue)),
                settings,
                git,
                onStop,
                _JsonOptions,
                BroadcastMissionChange,
                BroadcastVoyageChange);

            WebsocketSettings wsSettings = new WebsocketSettings
            {
                Hostnames = new List<string> { "localhost" },
                Port = port,
                Ssl = ssl
            };
            _WsServer = new WatsonWsServer(wsSettings);

            _WsServer.ClientConnected += (sender, args) =>
            {
                _Logging.Info(_Header + "client connected: " + args.Client.IpPort);
            };

            _WsServer.ClientDisconnected += (sender, args) =>
            {
                _Logging.Info(_Header + "client disconnected: " + args.Client.IpPort);
            };

            _WsServer.MessageReceived += (sender, args) =>
            {
                _ = HandleMessageAsync(args);
            };
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Start the WebSocket server.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task StartAsync(CancellationToken token = default)
        {
            _WsServer.Start();
            using (token.Register(() => _StopSignal.TrySetResult(true)))
            {
                await _StopSignal.Task.ConfigureAwait(false);
            }
            _WsServer.Stop();
        }

        /// <summary>
        /// Broadcast a mission state change to all connected clients.
        /// </summary>
        /// <param name="missionId">Mission ID.</param>
        /// <param name="status">New status.</param>
        /// <param name="title">Mission title.</param>
        public void BroadcastMissionChange(string missionId, string status, string? title = null)
        {
            object payload = new
            {
                type = "mission.changed",
                data = new
                {
                    id = missionId,
                    title = title,
                    status = status
                },
                timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload);
        }

        /// <summary>
        /// Broadcast a voyage state change to all connected clients.
        /// </summary>
        /// <param name="voyageId">Voyage ID.</param>
        /// <param name="status">New status.</param>
        /// <param name="title">Voyage title.</param>
        public void BroadcastVoyageChange(string voyageId, string status, string? title = null)
        {
            object payload = new
            {
                type = "voyage.changed",
                data = new
                {
                    id = voyageId,
                    title = title,
                    status = status
                },
                timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload);
        }

        /// <summary>
        /// Broadcast a captain state change to all connected clients.
        /// </summary>
        /// <param name="captainId">Captain ID.</param>
        /// <param name="state">New state.</param>
        /// <param name="name">Captain name.</param>
        public void BroadcastCaptainChange(string captainId, string state, string? name = null)
        {
            object payload = new
            {
                type = "captain.changed",
                data = new
                {
                    id = captainId,
                    name = name,
                    state = state
                },
                timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload);
        }

        /// <summary>
        /// Broadcast a generic event to all connected clients.
        /// </summary>
        /// <param name="eventType">Event type string.</param>
        /// <param name="message">Event message.</param>
        /// <param name="data">Optional additional data.</param>
        public void BroadcastEvent(string eventType, string message, object? data = null)
        {
            object payload = new
            {
                type = eventType,
                message = message,
                data = data,
                timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload);
        }

        #endregion

        #region Private-Methods

        private async Task HandleMessageAsync(MessageReceivedEventArgs args)
        {
            // Messages arrive as JSON envelopes. The "route" field selects the handler
            // (subscribe, command); anything else falls through to the default branch
            // so clients get a discoverable error.
            string body;
            try
            {
                body = System.Text.Encoding.UTF8.GetString(args.Data.ToArray());
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error decoding message: " + ex.Message);
                return;
            }

            string? route = null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("route", out JsonElement routeEl))
                        route = routeEl.GetString();
                }
            }
            catch
            {
                // Non-JSON or missing route falls through to default branch below.
            }

            Guid clientGuid = args.Client.Guid;

            try
            {
                if (route == "subscribe")
                {
                    ArmadaStatus status = await _Admiral.GetStatusAsync().ConfigureAwait(false);
                    object initial = new
                    {
                        type = "status.snapshot",
                        data = status,
                        timestamp = DateTime.UtcNow
                    };
                    await SendAsync(clientGuid, JsonSerializer.Serialize(initial, _JsonOptions)).ConfigureAwait(false);
                    return;
                }

                if (route == "command")
                {
                    WebSocketCommand command = JsonSerializer.Deserialize<WebSocketCommand>(body, _JsonOptions) ?? new WebSocketCommand();
                    object result = await _CommandHandler.HandleCommandAsync(command.Action, command, body).ConfigureAwait(false);
                    await SendAsync(clientGuid, JsonSerializer.Serialize(result, _JsonOptions)).ConfigureAwait(false);
                    return;
                }

                string errorJson = JsonSerializer.Serialize(
                    new { type = "error", message = "Unknown route: " + (route ?? "null") + ". Send a message with route 'subscribe' or 'command'" },
                    _JsonOptions);
                await SendAsync(clientGuid, errorJson).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error handling message: " + ex.Message);
                try
                {
                    string errorJson = JsonSerializer.Serialize(new { type = "command.error", error = ex.Message }, _JsonOptions);
                    await SendAsync(clientGuid, errorJson).ConfigureAwait(false);
                }
                catch
                {
                    // Client may have disconnected before we could reply.
                }
            }
        }

        private async Task SendAsync(Guid clientGuid, string text)
        {
            try
            {
                await _WsServer.SendAsync(clientGuid, text).ConfigureAwait(false);
            }
            catch
            {
                // Client may have disconnected.
            }
        }

        private void BroadcastEvent(object payload)
        {
            try
            {
                string json = JsonSerializer.Serialize(payload, _JsonOptions);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

                List<ClientMetadata> clients = _WsServer.ListClients().ToList();
                foreach (ClientMetadata client in clients)
                {
                    try
                    {
                        _WsServer.SendAsync(client.Guid, bytes).Wait();
                    }
                    catch
                    {
                        // Client may have disconnected
                    }
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "broadcast error: " + ex.Message);
            }
        }

        #endregion
    }
}
