namespace Armada.Server.WebSocket
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Net.WebSockets;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.WebSockets;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// WebSocket hub for real-time event broadcasting.
    /// Runs on the main Watson7 REST server at the /ws path.
    /// Supports subscribe/command message routing and broadcasts mission/captain state changes.
    /// Commands follow the declared rules in <see cref="WebSocketCommandRegistry"/>, which are never looser than the
    /// matching REST routes and MCP tools.
    /// </summary>
    public class ArmadaWebSocketHub
    {
        #region Private-Members

        private string _Header = "[WebSocketHub] ";
        private LoggingModule _Logging;
        private IAdmiralService _Admiral;
        private readonly DatabaseDriver _Database;
        private readonly IAuthenticationService _Authentication;
        private WebSocketCommandHandler _CommandHandler;
        private const int ClientOutputQueueCapacity = 256;
        private const int AuthenticationWindowSeconds = 15;
        private ConcurrentDictionary<Guid, ClientConnection> _Sessions = new ConcurrentDictionary<Guid, ClientConnection>();
        private readonly object _BroadcastLock = new object();
        private readonly WebSocketReplayBuffer _ReplayBuffer = new WebSocketReplayBuffer();
        private readonly FleetReconciliationSnapshotService _SnapshotService;

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the WebSocket hub.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="admiral">Admiral service for command handling.</param>
        /// <param name="database">Database driver for data access.</param>
        /// <param name="mergeQueue">Merge queue service.</param>
        /// <param name="authentication">Authentication service that resolves session credentials.</param>
        /// <param name="settings">Optional Armada settings for log/diff paths.</param>
        /// <param name="git">Optional git service for diff generation.</param>
        /// <param name="onStop">Optional callback invoked when stop_server is requested.</param>
        public ArmadaWebSocketHub(LoggingModule logging, IAdmiralService admiral, DatabaseDriver database, IMergeQueueService mergeQueue, IAuthenticationService authentication, ArmadaSettings? settings = null, IGitService? git = null, Action? onStop = null, MissionStatusTransitionService? statusTransitions = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            _SnapshotService = new FleetReconciliationSnapshotService(_Database);

            _CommandHandler = new WebSocketCommandHandler(
                _Admiral,
                _Database,
                mergeQueue ?? throw new ArgumentNullException(nameof(mergeQueue)),
                settings,
                git,
                onStop,
                _JsonOptions,
                BroadcastMissionChange,
                BroadcastVoyageChange,
                statusTransitions,
                settings != null ? new DatabaseBackupService(_Database, settings) : null);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Use the shared captain stop-all and deletion service for the captain commands, so WebSocket applies
        /// the same rules as REST and MCP.
        /// </summary>
        /// <param name="captainAdministration">Shared captain administration service.</param>
        public void SetCaptainAdministration(CaptainAdministrationService captainAdministration)
        {
            _CommandHandler.CaptainAdministration = captainAdministration ?? throw new ArgumentNullException(nameof(captainAdministration));
        }

        /// <summary>
        /// Use the shared mission and voyage operations REST and MCP use, so cancel, purge and restart apply the
        /// same rules and report the same events on WebSocket.
        /// </summary>
        /// <param name="operations">Shared mission and voyage operations.</param>
        public void SetMissionOperations(MissionOperations operations)
        {
            _CommandHandler.Operations = operations ?? throw new ArgumentNullException(nameof(operations));
        }

        /// <summary>
        /// Use the shared voyage dispatch service REST and MCP use for <c>create_voyage</c>, built per command with
        /// the server's code-index service, objective service and dispatch preview.
        /// </summary>
        /// <param name="factory">Builds the shared voyage dispatch service.</param>
        public void SetVoyageDispatchFactory(Func<VoyageDispatchService> factory)
        {
            _CommandHandler.VoyageDispatchFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>
        /// Watson7 WebSocket route handler. Registered on the main server at /ws.
        /// Manages the full session lifecycle: connect, read loop, disconnect.
        /// </summary>
        /// <param name="ctx">HTTP context for the upgrade request.</param>
        /// <param name="session">Watson7 WebSocket session.</param>
        public async Task HandleWebSocketAsync(HttpContextBase ctx, WebSocketSession session)
        {
            WebSocketClientOutputQueue output = new WebSocketClientOutputQueue(
                ClientOutputQueueCapacity,
                (message, token) => session.SendTextAsync(message, token));
            ClientConnection connection = new ClientConnection(session, output);
            output.Terminated += failure => HandleOutputTermination(session.Id, connection, failure);

            if (!_Sessions.TryAdd(session.Id, connection))
            {
                await output.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("A WebSocket session with this ID is already registered.");
            }
            _Logging.Info(_Header + "client connected: " + session.RemoteIp + ":" + session.RemotePort);

            try
            {
                if (!await AuthenticateFromHeadersAsync(session.Id, connection, ctx).ConfigureAwait(false)) return;

                // A session that never authenticates would otherwise hold a socket until the client leaves.
                if (connection.Auth == null) _ = CloseIfUnauthenticatedAsync(session.Id, connection, ctx.Token);

                await foreach (WebSocketMessage message in session.ReadMessagesAsync(ctx.Token))
                {
                    if (message.MessageType != WebSocketMessageType.Text) continue;
                    await HandleMessageAsync(session.Id, message.Text).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Server shutting down or client disconnected normally.
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "session error: " + ex.Message);
            }
            finally
            {
                RemoveConnection(session.Id, connection);
                await output.DisposeAsync().ConfigureAwait(false);
                _Logging.Info(_Header + "client disconnected: " + session.RemoteIp + ":" + session.RemotePort);
            }
        }

        /// <summary>
        /// Broadcast a mission state change to global administrators. Use the overload that takes a
        /// delivery scope to reach the mission's owner as well.
        /// </summary>
        /// <param name="missionId">Mission ID.</param>
        /// <param name="status">New status.</param>
        /// <param name="title">Mission title.</param>
        /// <param name="voyageId">Parent voyage ID, or null for a standalone mission.</param>
        public void BroadcastMissionChange(string missionId, string status, string? title, string? voyageId)
        {
            BroadcastMissionChange(missionId, status, title, voyageId, WebSocketDeliveryScope.AdminOnly);
        }

        /// <summary>
        /// Broadcast a mission state change to the sessions that may read the mission.
        /// </summary>
        /// <param name="missionId">Mission ID.</param>
        /// <param name="status">New status.</param>
        /// <param name="title">Mission title.</param>
        /// <param name="voyageId">Parent voyage ID, or null for a standalone mission.</param>
        /// <param name="scope">Who may receive the event.</param>
        public void BroadcastMissionChange(string missionId, string status, string? title, string? voyageId, WebSocketDeliveryScope scope)
        {
            WebSocketEventEnvelope payload = new WebSocketEventEnvelope
            {
                Type = "mission.changed",
                Data = new
                {
                    id = missionId,
                    title = title,
                    status = status,
                    voyageId = voyageId
                },
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload, scope);
        }

        /// <summary>
        /// Broadcast a mission state change to the sessions that may read the mission.
        /// </summary>
        /// <param name="mission">Changed mission.</param>
        public void BroadcastMissionChange(Mission mission)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            BroadcastMissionChange(mission.Id, mission.Status.ToString(), mission.Title, mission.VoyageId,
                WebSocketDeliveryScope.ForOwner(mission.TenantId, mission.UserId));
        }

        /// <summary>
        /// Broadcast a voyage state change to global administrators. Use the overload that takes a
        /// delivery scope to reach the voyage's owner as well.
        /// </summary>
        /// <param name="voyageId">Voyage ID.</param>
        /// <param name="status">New status.</param>
        /// <param name="title">Voyage title.</param>
        public void BroadcastVoyageChange(string voyageId, string status, string? title = null)
        {
            BroadcastVoyageChange(voyageId, status, title, WebSocketDeliveryScope.AdminOnly);
        }

        /// <summary>
        /// Broadcast a voyage state change to the sessions that may read the voyage.
        /// </summary>
        /// <param name="voyageId">Voyage ID.</param>
        /// <param name="status">New status.</param>
        /// <param name="title">Voyage title.</param>
        /// <param name="scope">Who may receive the event.</param>
        public void BroadcastVoyageChange(string voyageId, string status, string? title, WebSocketDeliveryScope scope)
        {
            WebSocketEventEnvelope payload = new WebSocketEventEnvelope
            {
                Type = "voyage.changed",
                Data = new
                {
                    id = voyageId,
                    title = title,
                    status = status
                },
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload, scope);
        }

        /// <summary>
        /// Broadcast a voyage state change to the sessions that may read the voyage.
        /// </summary>
        /// <param name="voyage">Changed voyage.</param>
        public void BroadcastVoyageChange(Voyage voyage)
        {
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));
            BroadcastVoyageChange(voyage.Id, voyage.Status.ToString(), voyage.Title,
                WebSocketDeliveryScope.ForOwner(voyage.TenantId, voyage.UserId));
        }

        /// <summary>
        /// Broadcast a captain state change to global administrators. Use the overload that takes the
        /// captain to reach its owner as well.
        /// </summary>
        /// <param name="captainId">Captain ID.</param>
        /// <param name="state">New state.</param>
        /// <param name="name">Captain name.</param>
        public void BroadcastCaptainChange(string captainId, string state, string? name = null)
        {
            BroadcastCaptainChange(captainId, state, name, WebSocketDeliveryScope.AdminOnly);
        }

        /// <summary>
        /// Broadcast a captain state change to the sessions that may read the captain.
        /// </summary>
        /// <param name="captain">Changed captain.</param>
        public void BroadcastCaptainChange(Captain captain)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            BroadcastCaptainChange(captain.Id, captain.State.ToString(), captain.Name,
                WebSocketDeliveryScope.ForOwner(captain.TenantId, captain.UserId));
        }

        private void BroadcastCaptainChange(string captainId, string state, string? name, WebSocketDeliveryScope scope)
        {
            WebSocketEventEnvelope payload = new WebSocketEventEnvelope
            {
                Type = "captain.changed",
                Data = new
                {
                    id = captainId,
                    name = name,
                    state = state
                },
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload, scope);
        }

        /// <summary>
        /// Broadcast a structured check-run change to the sessions that may read the check run.
        /// </summary>
        /// <param name="run">Changed check run.</param>
        public void BroadcastCheckRunChange(CheckRun run)
        {
            WebSocketEventEnvelope payload = new WebSocketEventEnvelope
            {
                Type = "check-run.changed",
                Data = run,
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload, WebSocketDeliveryScope.ForOwner(run.TenantId, run.UserId));
        }

        /// <summary>
        /// Broadcast an objective change to the sessions that may read the objective.
        /// </summary>
        /// <param name="objective">Changed objective.</param>
        public void BroadcastObjectiveChange(Objective objective)
        {
            WebSocketEventEnvelope payload = new WebSocketEventEnvelope
            {
                Type = "objective.changed",
                Data = objective,
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload, WebSocketDeliveryScope.ForOwner(objective.TenantId, objective.UserId));
        }

        /// <summary>
        /// Broadcast an objective deletion to the sessions that could read the objective.
        /// </summary>
        /// <param name="objective">Deleted objective; only its identity and owner are sent.</param>
        public void BroadcastObjectiveDeleted(Objective objective)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            WebSocketEventEnvelope payload = new WebSocketEventEnvelope
            {
                Type = "objective.deleted",
                Data = new ObjectiveDeletedEventData
                {
                    Id = objective.Id,
                    TenantId = objective.TenantId,
                    UserId = objective.UserId
                },
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload, WebSocketDeliveryScope.ForOwner(objective.TenantId, objective.UserId));
        }

        /// <summary>
        /// Broadcast a deployment change to the sessions that may read the deployment.
        /// </summary>
        /// <param name="deployment">Changed deployment.</param>
        public void BroadcastDeploymentChange(Deployment deployment)
        {
            WebSocketDeliveryScope deploymentScope = WebSocketDeliveryScope.ForOwner(deployment.TenantId, deployment.UserId);
            WebSocketEventEnvelope changedPayload = new WebSocketEventEnvelope
            {
                Type = "deployment.changed",
                Data = deployment,
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(changedPayload, deploymentScope);

            WebSocketEventEnvelope progressPayload = new WebSocketEventEnvelope
            {
                Type = "deployment.progress",
                Data = new
                {
                    deployment.Id,
                    deployment.Title,
                    deployment.Status,
                    deployment.VerificationStatus,
                    deployment.EnvironmentId,
                    deployment.EnvironmentName,
                    deployment.StartedUtc,
                    deployment.CompletedUtc,
                    deployment.LastUpdateUtc
                },
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(progressPayload, deploymentScope);

            if (!String.IsNullOrWhiteSpace(deployment.EnvironmentId) || !String.IsNullOrWhiteSpace(deployment.EnvironmentName))
            {
                WebSocketEventEnvelope environmentPayload = new WebSocketEventEnvelope
                {
                    Type = "environment.health",
                    Data = new
                    {
                        deployment.EnvironmentId,
                        deployment.EnvironmentName,
                        deployment.Id,
                        deployment.Title,
                        deployment.Status,
                        deployment.VerificationStatus,
                        deployment.LastMonitoredUtc,
                        deployment.LastRegressionAlertUtc,
                        deployment.LatestMonitoringSummary,
                        deployment.MonitoringFailureCount
                    },
                    Timestamp = DateTime.UtcNow
                };

                BroadcastEvent(environmentPayload, deploymentScope);
            }
        }

        /// <summary>
        /// Broadcast an incident change to the sessions that may read the incident.
        /// </summary>
        /// <param name="incident">Changed incident.</param>
        public void BroadcastIncidentChange(Incident incident)
        {
            WebSocketEventEnvelope payload = new WebSocketEventEnvelope
            {
                Type = "incident.changed",
                Data = incident,
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload, WebSocketDeliveryScope.ForOwner(incident.TenantId, incident.UserId));
        }

        /// <summary>
        /// Broadcast a runbook execution change to the sessions that may read the execution.
        /// </summary>
        /// <param name="execution">Changed runbook execution.</param>
        public void BroadcastRunbookExecutionChange(RunbookExecution execution)
        {
            WebSocketEventEnvelope payload = new WebSocketEventEnvelope
            {
                Type = "runbook-execution.changed",
                Data = execution,
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload, WebSocketDeliveryScope.ForOwner(execution.TenantId, execution.UserId));
        }

        /// <summary>
        /// Broadcast an approval-needed notification, to the sessions that may read the mission, when a
        /// mission enters review.
        /// </summary>
        /// <param name="mission">Mission awaiting approval.</param>
        public void BroadcastApprovalNeeded(Mission mission)
        {
            WebSocketEventEnvelope payload = new WebSocketEventEnvelope
            {
                Type = "approval-needed",
                Data = new
                {
                    entityType = "mission",
                    entityId = mission.Id,
                    missionId = mission.Id,
                    title = mission.Title,
                    status = mission.Status.ToString(),
                    vesselId = mission.VesselId,
                    voyageId = mission.VoyageId,
                    reviewRequestedUtc = mission.ReviewRequestedUtc
                },
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload, WebSocketDeliveryScope.ForOwner(mission.TenantId, mission.UserId));
        }

        /// <summary>
        /// Broadcast a generic event to global administrators. Use the overload that takes a delivery
        /// scope to reach the owner of the record the event describes.
        /// </summary>
        /// <param name="eventType">Event type string.</param>
        /// <param name="message">Event message.</param>
        /// <param name="data">Optional additional data.</param>
        public void BroadcastEvent(string eventType, string message, object? data = null)
        {
            BroadcastEvent(eventType, message, data, WebSocketDeliveryScope.AdminOnly);
        }

        /// <summary>
        /// Broadcast a generic event to the sessions a delivery scope allows.
        /// </summary>
        /// <param name="eventType">Event type string.</param>
        /// <param name="message">Event message.</param>
        /// <param name="data">Optional additional data.</param>
        /// <param name="scope">Who may receive the event.</param>
        public void BroadcastEvent(string eventType, string message, object? data, WebSocketDeliveryScope scope)
        {
            WebSocketEventEnvelope payload = new WebSocketEventEnvelope
            {
                Type = eventType,
                Message = message,
                Data = data,
                Timestamp = DateTime.UtcNow
            };

            BroadcastEvent(payload, scope);
        }

        #endregion

        #region Private-Methods

        private async Task HandleMessageAsync(Guid sessionId, string body)
        {
            string? route = null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("route", out JsonElement routeEl))
                        route = routeEl.GetString();
                    if (route == null && doc.RootElement.TryGetProperty("Route", out JsonElement routeElPascal))
                        route = routeElPascal.GetString();
                }
            }
            catch
            {
                // Non-JSON or missing route falls through to default branch below.
            }

            if (!_Sessions.TryGetValue(sessionId, out ClientConnection? connection)) return;

            try
            {
                if (string.Equals(route, "authenticate", StringComparison.OrdinalIgnoreCase))
                {
                    await AuthenticateSessionAsync(sessionId, connection, body).ConfigureAwait(false);
                    return;
                }

                // Every other route reads or changes fleet state, so an anonymous session
                // gets nothing: no snapshot, no broadcasts and no command.
                if (connection.Auth == null)
                {
                    await RefuseAsync(sessionId, connection, "auth.required", "Authenticate this session before sending route '" + (route ?? "null") + "'.").ConfigureAwait(false);
                    return;
                }

                if (string.Equals(route, "subscribe", StringComparison.OrdinalIgnoreCase))
                {
                    // Any authenticated session may subscribe. Each event carries a delivery scope and
                    // reaches only the sessions that may read the record it describes; fleet-wide
                    // aggregates stay with global administrators.
                    WebSocketSubscribeRequest request = JsonSerializer.Deserialize<WebSocketSubscribeRequest>(body, _JsonOptions)
                        ?? new WebSocketSubscribeRequest { Route = "subscribe" };
                    await ActivateSubscriptionAsync(sessionId, request).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(route, "command", StringComparison.OrdinalIgnoreCase))
                {
                    // Every command is authorized by its declared rule in WebSocketCommandRegistry, which the
                    // handler enforces before the command runs; an undeclared command is refused.
                    WebSocketCommand command = JsonSerializer.Deserialize<WebSocketCommand>(body, _JsonOptions) ?? new WebSocketCommand();
                    object result = await _CommandHandler.HandleCommandAsync(command.Action, command, body, connection.Auth).ConfigureAwait(false);
                    EnqueueOrDisconnect(sessionId, JsonSerializer.Serialize(result, _JsonOptions));
                    return;
                }

                string errorJson = JsonSerializer.Serialize(
                    new { type = "error", message = "Unknown route: " + (route ?? "null") + ". Send a message with route 'subscribe' or 'command'" },
                    _JsonOptions);
                EnqueueOrDisconnect(sessionId, errorJson);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error handling message: " + ex.Message);
                try
                {
                    string errorJson = JsonSerializer.Serialize(new { type = "command.error", error = ex.Message }, _JsonOptions);
                    EnqueueOrDisconnect(sessionId, errorJson);
                }
                catch
                {
                    // Client may have disconnected before we could reply.
                }
            }
        }

        private void BroadcastEvent(WebSocketEventEnvelope payload, WebSocketDeliveryScope scope)
        {
            try
            {
                List<KeyValuePair<Guid, ClientConnection>> disconnected = new List<KeyValuePair<Guid, ClientConnection>>();

                lock (_BroadcastLock)
                {
                    WebSocketReplayRecord record = _ReplayBuffer.Append((streamId, cursor) =>
                    {
                        payload.StreamId = streamId;
                        payload.Cursor = cursor;
                        return JsonSerializer.Serialize(payload, _JsonOptions);
                    }, scope);
                    foreach (KeyValuePair<Guid, ClientConnection> kvp in _Sessions)
                    {
                        ClientConnection connection = kvp.Value;
                        if (!connection.Subscribed) continue;
                        if (!record.Scope.CanReceive(connection.Auth)) continue;
                        if (!connection.Session.IsConnected || !connection.Output.TryEnqueue(record.Frame))
                            disconnected.Add(kvp);
                    }
                }

                foreach (KeyValuePair<Guid, ClientConnection> kvp in disconnected)
                {
                    string reason = kvp.Value.Session.IsConnected
                        ? "Outbound event queue reached its capacity; reconnect and reconcile state."
                        : "WebSocket session disconnected.";
                    DisconnectConnection(kvp.Key, kvp.Value, reason);
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "broadcast error: " + ex.Message);
            }
        }

        private async Task ActivateSubscriptionAsync(Guid sessionId, WebSocketSubscribeRequest request)
        {
            WebSocketReplayReadResult? replay = null;
            long snapshotWatermark;
            lock (_BroadcastLock)
            {
                if (!_Sessions.TryGetValue(sessionId, out ClientConnection? pending)) return;
                if (pending.SubscriptionRequested)
                    throw new InvalidOperationException("This WebSocket connection is already subscribed.");
                pending.SubscriptionRequested = true;
                pending.Subscribed = false;
                snapshotWatermark = _ReplayBuffer.CurrentCursor;
                if (!String.IsNullOrWhiteSpace(request.StreamId) && request.Cursor.HasValue)
                    replay = _ReplayBuffer.ReadAfter(request.StreamId, request.Cursor.Value);
            }

            // The status and reconciliation snapshots aggregate every tenant, so only a global
            // administrator receives them. A narrower session gets a scoped snapshot and reloads its
            // own records through the REST API, which applies its scope.
            if (!_Sessions.TryGetValue(sessionId, out ClientConnection? subscriber)) return;
            bool fleetWide = subscriber.Auth != null && subscriber.Auth.IsAdmin;
            ArmadaStatus? status = fleetWide ? await _Admiral.GetStatusAsync().ConfigureAwait(false) : null;
            FleetReconciliationSnapshot? reconciliation = fleetWide
                ? await _SnapshotService.GetAsync(request.VoyageId).ConfigureAwait(false)
                : null;

            ClientConnection? failedConnection = null;
            lock (_BroadcastLock)
            {
                if (!_Sessions.TryGetValue(sessionId, out ClientConnection? connection)) return;
                WebSocketReplayReadResult catchUp = _ReplayBuffer.ReadAfter(_ReplayBuffer.StreamId, snapshotWatermark);
                string? gapReason = replay?.GapReason;
                IReadOnlyList<WebSocketReplayRecord> initialRecords = FilterForSession(replay?.Records ?? Array.Empty<WebSocketReplayRecord>(), connection.Auth);
                IReadOnlyList<WebSocketReplayRecord> catchUpRecords = FilterForSession(catchUp.Records, connection.Auth);
                long readyCursor = _ReplayBuffer.CurrentCursor;

                if (catchUp.HasGap)
                {
                    gapReason = "snapshot_overflow";
                    catchUpRecords = Array.Empty<WebSocketReplayRecord>();
                    // Do not acknowledge events that the snapshot and catch-up did not
                    // cover. The next live event will expose a cursor discontinuity and
                    // make the client reconnect from this safe watermark.
                    readyCursor = snapshotWatermark;
                }
                if (initialRecords.Count + catchUpRecords.Count > ClientOutputQueueCapacity - 3)
                {
                    gapReason = "replay_overflow";
                    initialRecords = Array.Empty<WebSocketReplayRecord>();
                }

                List<string> frames = new List<string>();
                foreach (WebSocketReplayRecord record in initialRecords) frames.Add(record.Frame);
                if (gapReason != null)
                {
                    frames.Add(JsonSerializer.Serialize(new
                    {
                        type = "event.gap",
                        streamId = _ReplayBuffer.StreamId,
                        data = new
                        {
                            reason = gapReason,
                            requestedStreamId = request.StreamId,
                            requestedCursor = request.Cursor,
                            oldestCursor = replay?.OldestAvailableCursor ?? catchUp.OldestAvailableCursor,
                            currentCursor = _ReplayBuffer.CurrentCursor
                        },
                        timestamp = DateTime.UtcNow
                    }, _JsonOptions));
                }

                frames.Add(JsonSerializer.Serialize(new
                {
                    type = "status.snapshot",
                    streamId = _ReplayBuffer.StreamId,
                    cursor = snapshotWatermark,
                    data = new { status, reconciliation, scoped = !fleetWide },
                    timestamp = DateTime.UtcNow
                }, _JsonOptions));
                foreach (WebSocketReplayRecord record in catchUpRecords) frames.Add(record.Frame);
                frames.Add(JsonSerializer.Serialize(new
                {
                    type = "stream.ready",
                    streamId = _ReplayBuffer.StreamId,
                    cursor = readyCursor,
                    data = new { replayed = initialRecords.Count + catchUpRecords.Count, gapDetected = gapReason != null },
                    timestamp = DateTime.UtcNow
                }, _JsonOptions));

                foreach (string frame in frames)
                {
                    if (connection.Session.IsConnected && connection.Output.TryEnqueue(frame)) continue;
                    failedConnection = connection;
                    break;
                }
                if (failedConnection == null) connection.Subscribed = true;
            }

            if (failedConnection != null)
                DisconnectConnection(sessionId, failedConnection, "Outbound subscription queue reached its capacity.");
        }

        private static IReadOnlyList<WebSocketReplayRecord> FilterForSession(IReadOnlyList<WebSocketReplayRecord> records, AuthContext? auth)
        {
            // Replayed events obey the same delivery scope as live ones, so reconnecting never reveals
            // an event the session could not have received.
            List<WebSocketReplayRecord> allowed = new List<WebSocketReplayRecord>(records.Count);
            foreach (WebSocketReplayRecord record in records)
            {
                if (record.Scope.CanReceive(auth)) allowed.Add(record);
            }
            return allowed;
        }

        private void EnqueueOrDisconnect(Guid sessionId, string json)
        {
            if (!_Sessions.TryGetValue(sessionId, out ClientConnection? connection)) return;
            if (!connection.Output.TryEnqueue(json))
                DisconnectConnection(sessionId, connection, "Outbound response queue reached its capacity.");
        }

        private void HandleOutputTermination(Guid sessionId, ClientConnection connection, Exception? failure)
        {
            if (failure == null) return;
            _Logging.Warn(_Header + "client output failed: " + failure.Message);
            DisconnectConnection(sessionId, connection, "Outbound send failed.");
        }

        private void DisconnectConnection(Guid sessionId, ClientConnection connection, string reason)
        {
            RemoveConnection(sessionId, connection);
            connection.Output.Stop();
            _Logging.Warn(_Header + "disconnecting client " + sessionId + ": " + reason);
            _ = CloseSessionAsync(connection.Session, reason);
        }

        private void RemoveConnection(Guid sessionId, ClientConnection connection)
        {
            if (_Sessions.TryGetValue(sessionId, out ClientConnection? current)
                && ReferenceEquals(current, connection))
                _Sessions.TryRemove(sessionId, out _);
        }

        private static async Task CloseSessionAsync(WebSocketSession session, string reason)
        {
            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await session.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // The failed or slow client can already be disconnected.
            }
        }

        /// <summary>
        /// Authenticate from upgrade request headers when a non-browser client sends them.
        /// A session without headers stays unauthenticated until it sends an authenticate route.
        /// </summary>
        /// <returns>False when header credentials were supplied and refused.</returns>
        private async Task<bool> AuthenticateFromHeadersAsync(Guid sessionId, ClientConnection connection, HttpContextBase ctx)
        {
            string? authorization = ctx.Request.Headers.Get("Authorization");
            string? sessionToken = ctx.Request.Headers.Get("X-Token");
            string? apiKey = ctx.Request.Headers.Get("X-Api-Key");
            if (String.IsNullOrEmpty(authorization) && String.IsNullOrEmpty(sessionToken) && String.IsNullOrEmpty(apiKey))
                return true;

            AuthContext auth = await _Authentication.AuthenticateAsync(authorization, sessionToken, apiKey).ConfigureAwait(false);
            if (!auth.IsAuthenticated)
            {
                await RefuseAsync(sessionId, connection, "auth.failed", "The supplied credentials are not valid.").ConfigureAwait(false);
                return false;
            }

            connection.Auth = auth;
            EnqueueOrDisconnect(sessionId, BuildAuthResult(auth));
            return true;
        }

        /// <summary>
        /// Close a session that has not authenticated when the authentication window ends.
        /// </summary>
        private async Task CloseIfUnauthenticatedAsync(Guid sessionId, ClientConnection connection, CancellationToken token)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(AuthenticationWindowSeconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (connection.Auth != null) return;
            if (!_Sessions.TryGetValue(sessionId, out ClientConnection? current) || !ReferenceEquals(current, connection)) return;

            await RefuseAsync(
                sessionId,
                connection,
                "auth.required",
                "Authenticate within " + AuthenticationWindowSeconds + " seconds of connecting.").ConfigureAwait(false);
        }

        private async Task AuthenticateSessionAsync(Guid sessionId, ClientConnection connection, string body)
        {
            if (connection.Auth != null)
            {
                EnqueueOrDisconnect(sessionId, JsonSerializer.Serialize(
                    new { type = "error", message = "This WebSocket session is already authenticated." },
                    _JsonOptions));
                return;
            }

            WebSocketAuthenticateRequest request = JsonSerializer.Deserialize<WebSocketAuthenticateRequest>(body, _JsonOptions)
                ?? new WebSocketAuthenticateRequest();
            string? token = String.IsNullOrWhiteSpace(request.Token) ? null : request.Token.Trim();
            string? apiKey = String.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim();
            string? bearer = token == null ? null : "Bearer " + token;

            AuthContext auth = await _Authentication.AuthenticateAsync(bearer, token, apiKey).ConfigureAwait(false);
            if (!auth.IsAuthenticated)
            {
                await RefuseAsync(sessionId, connection, "auth.failed", "The supplied credentials are not valid.").ConfigureAwait(false);
                return;
            }

            connection.Auth = auth;
            EnqueueOrDisconnect(sessionId, BuildAuthResult(auth));
        }

        private string BuildAuthResult(AuthContext auth)
        {
            return JsonSerializer.Serialize(new
            {
                type = "auth.result",
                data = new
                {
                    authenticated = true,
                    tenantId = auth.TenantId,
                    userId = auth.UserId,
                    isAdmin = auth.IsAdmin,
                    isTenantAdmin = auth.IsTenantAdmin
                },
                timestamp = DateTime.UtcNow
            }, _JsonOptions);
        }

        /// <summary>
        /// Send one refusal frame and close. The frame is sent directly because stopping the
        /// output queue discards queued frames; an unauthenticated session has no queued frames
        /// that the direct send could overtake.
        /// </summary>
        private async Task RefuseAsync(Guid sessionId, ClientConnection connection, string type, string message)
        {
            RemoveConnection(sessionId, connection);
            connection.Output.Stop();
            _Logging.Warn(_Header + "refusing client " + sessionId + " " + connection.Session.RemoteIp + ": " + type);

            string json = JsonSerializer.Serialize(new { type = type, message = message, timestamp = DateTime.UtcNow }, _JsonOptions);
            try
            {
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    await connection.Session.SendTextAsync(json, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _Logging.Debug(_Header + "refusal frame not delivered to " + sessionId + ": " + ex.Message);
            }

            await CloseSessionAsync(connection.Session, message).ConfigureAwait(false);
        }

        private sealed class ClientConnection
        {
            public WebSocketSession Session { get; }
            public WebSocketClientOutputQueue Output { get; }
            public bool Subscribed { get; set; }
            public bool SubscriptionRequested { get; set; }
            public AuthContext? Auth { get; set; }

            public ClientConnection(WebSocketSession session, WebSocketClientOutputQueue output)
            {
                Session = session;
                Output = output;
            }
        }

        #endregion
    }
}
