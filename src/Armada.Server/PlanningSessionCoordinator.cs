namespace Armada.Server
{
    using System.Diagnostics;
    using System.Text;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server.WebSocket;
    using SyslogLogging;

    /// <summary>
    /// Coordinates planning session lifecycle, captain reservation, dock ownership, turn execution, and dispatch conversion.
    /// </summary>
    public class PlanningSessionCoordinator
    {
        #region Private-Members

        private sealed class TurnState
        {
            public bool StopRequested { get; set; } = false;
        }

        private readonly string _Header = "[PlanningSessionCoordinator] ";
        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly IDockService _Docks;
        private readonly IAdmiralService _Admiral;
        private readonly AgentRuntimeFactory _RuntimeFactory;
        private readonly Func<string, string, string?, string?, string?, string?, string?, string?, Task> _EmitEventAsync;
        private readonly ArmadaWebSocketHub? _WebSocketHub;
        private readonly PlaybookService _Playbooks;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TurnState> _ActiveTurns =
            new System.Collections.Concurrent.ConcurrentDictionary<string, TurnState>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PlanningSessionCoordinator(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IDockService docks,
            IAdmiralService admiral,
            AgentRuntimeFactory runtimeFactory,
            Func<string, string, string?, string?, string?, string?, string?, string?, Task> emitEventAsync,
            ArmadaWebSocketHub? webSocketHub = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Docks = docks ?? throw new ArgumentNullException(nameof(docks));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _RuntimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
            _EmitEventAsync = emitEventAsync ?? throw new ArgumentNullException(nameof(emitEventAsync));
            _WebSocketHub = webSocketHub;
            _Playbooks = new PlaybookService(_Database, _Logging);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create and provision a planning session.
        /// </summary>
        public async Task<PlanningSession> CreateAsync(
            string? tenantId,
            string? userId,
            Captain captain,
            Vessel vessel,
            PlanningSessionCreateRequest request,
            CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (captain.State != CaptainStateEnum.Idle)
                throw new InvalidOperationException("Captain " + captain.Name + " is not idle.");

            List<PlanningSession> captainSessions = await _Database.PlanningSessions
                .EnumerateByCaptainAsync(captain.Id, token)
                .ConfigureAwait(false);
            if (captainSessions.Any(s => s.Status == PlanningSessionStatusEnum.Active || s.Status == PlanningSessionStatusEnum.Responding || s.Status == PlanningSessionStatusEnum.Stopping))
                throw new InvalidOperationException("Captain " + captain.Name + " already has an active planning session.");

            if (!String.IsNullOrEmpty(tenantId) && request.SelectedPlaybooks.Count > 0)
            {
                await _Playbooks.ResolveSelectionsAsync(tenantId, request.SelectedPlaybooks, token).ConfigureAwait(false);
            }

            PlanningSession session = new PlanningSession
            {
                TenantId = tenantId,
                UserId = userId,
                CaptainId = captain.Id,
                VesselId = vessel.Id,
                FleetId = request.FleetId ?? vessel.FleetId,
                Title = !String.IsNullOrWhiteSpace(request.Title) ? request.Title.Trim() : "Planning: " + vessel.Name,
                Status = PlanningSessionStatusEnum.Created,
                PipelineId = request.PipelineId,
                SelectedPlaybooks = request.SelectedPlaybooks ?? new List<SelectedPlaybook>(),
                CreatedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };

            session = await _Database.PlanningSessions.CreateAsync(session, token).ConfigureAwait(false);

            try
            {
                string branchName = Constants.BranchPrefix + "planning/" + session.Id;
                Dock? dock = await _Docks.ProvisionAsync(vessel, captain, branchName, session.Id, token).ConfigureAwait(false);
                if (dock == null)
                    throw new InvalidOperationException("Dock provisioning failed for planning session " + session.Id + ".");

                session.DockId = dock.Id;
                session.BranchName = branchName;
                session.Status = PlanningSessionStatusEnum.Active;
                session.StartedUtc = DateTime.UtcNow;
                session.LastUpdateUtc = DateTime.UtcNow;
                await _Database.PlanningSessions.UpdateAsync(session, token).ConfigureAwait(false);

                captain.State = CaptainStateEnum.Planning;
                captain.CurrentMissionId = null;
                captain.CurrentDockId = dock.Id;
                captain.ProcessId = null;
                captain.LastHeartbeatUtc = DateTime.UtcNow;
                captain.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                _WebSocketHub?.BroadcastCaptainChange(captain.Id, captain.State.ToString(), captain.Name);

                BroadcastSessionChanged(session);
                await _EmitEventAsync(
                    "planning-session.created",
                    "Planning session " + session.Id + " created for captain " + captain.Name,
                    "planning-session",
                    session.Id,
                    captain.Id,
                    null,
                    vessel.Id,
                    null).ConfigureAwait(false);

                return session;
            }
            catch (Exception ex)
            {
                if (!String.IsNullOrWhiteSpace(session.DockId))
                {
                    try
                    {
                        await _Docks.ReclaimAsync(session.DockId, session.TenantId, token).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                try
                {
                    Captain? failedCaptain = await _Database.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
                    if (failedCaptain != null)
                    {
                        failedCaptain.State = CaptainStateEnum.Idle;
                        failedCaptain.CurrentMissionId = null;
                        failedCaptain.CurrentDockId = null;
                        failedCaptain.ProcessId = null;
                        failedCaptain.LastHeartbeatUtc = DateTime.UtcNow;
                        failedCaptain.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Captains.UpdateAsync(failedCaptain, token).ConfigureAwait(false);
                        _WebSocketHub?.BroadcastCaptainChange(failedCaptain.Id, failedCaptain.State.ToString(), failedCaptain.Name);
                    }
                }
                catch
                {
                }

                session.Status = PlanningSessionStatusEnum.Failed;
                session.FailureReason = ex.Message;
                session.CompletedUtc = DateTime.UtcNow;
                session.LastUpdateUtc = DateTime.UtcNow;
                await _Database.PlanningSessions.UpdateAsync(session, token).ConfigureAwait(false);
                BroadcastSessionChanged(session);
                throw;
            }
        }

        /// <summary>
        /// Append a user message and start a background planning turn.
        /// </summary>
        public async Task<PlanningSessionMessage> SendMessageAsync(PlanningSession session, string content, CancellationToken token = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (String.IsNullOrWhiteSpace(content)) throw new ArgumentNullException(nameof(content));

            session = await RequireSessionAsync(session.Id, token).ConfigureAwait(false);
            if (session.Status != PlanningSessionStatusEnum.Active)
                throw new InvalidOperationException("Planning session " + session.Id + " is not ready for a new message.");

            if (!_ActiveTurns.TryAdd(session.Id, new TurnState()))
                throw new InvalidOperationException("Planning session " + session.Id + " is already generating a response.");

            List<PlanningSessionMessage> existingMessages = await _Database.PlanningSessionMessages
                .EnumerateBySessionAsync(session.Id, token)
                .ConfigureAwait(false);
            int nextSequence = existingMessages.Count == 0 ? 1 : existingMessages.Max(m => m.Sequence) + 1;

            PlanningSessionMessage userMessage = new PlanningSessionMessage
            {
                PlanningSessionId = session.Id,
                TenantId = session.TenantId,
                UserId = session.UserId,
                Role = "User",
                Sequence = nextSequence,
                Content = content.Trim(),
                CreatedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };
            userMessage = await _Database.PlanningSessionMessages.CreateAsync(userMessage, token).ConfigureAwait(false);
            BroadcastMessageCreated(userMessage);

            PlanningSessionMessage assistantMessage = new PlanningSessionMessage
            {
                PlanningSessionId = session.Id,
                TenantId = session.TenantId,
                UserId = session.UserId,
                Role = "Assistant",
                Sequence = nextSequence + 1,
                Content = String.Empty,
                CreatedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };
            assistantMessage = await _Database.PlanningSessionMessages.CreateAsync(assistantMessage, token).ConfigureAwait(false);
            BroadcastMessageCreated(assistantMessage);

            session.Status = PlanningSessionStatusEnum.Responding;
            session.ProcessId = null;
            session.FailureReason = null;
            session.LastUpdateUtc = DateTime.UtcNow;
            session = await _Database.PlanningSessions.UpdateAsync(session, token).ConfigureAwait(false);
            BroadcastSessionChanged(session);

            _ = Task.Run(() => ExecuteTurnAsync(session.Id, assistantMessage.Id), CancellationToken.None);
            return userMessage;
        }

        /// <summary>
        /// Stop a planning session and release its resources.
        /// </summary>
        public async Task<PlanningSession> StopAsync(PlanningSession session, CancellationToken token = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            session = await RequireSessionAsync(session.Id, token).ConfigureAwait(false);
            Captain? captain = await _Database.Captains.ReadAsync(session.CaptainId, token).ConfigureAwait(false);

            if (_ActiveTurns.TryGetValue(session.Id, out TurnState? turnState))
                turnState.StopRequested = true;

            session.Status = PlanningSessionStatusEnum.Stopping;
            session.LastUpdateUtc = DateTime.UtcNow;
            session = await _Database.PlanningSessions.UpdateAsync(session, token).ConfigureAwait(false);
            BroadcastSessionChanged(session);

            if (session.ProcessId.HasValue && captain != null)
            {
                try
                {
                    Armada.Runtimes.Interfaces.IAgentRuntime runtime = _RuntimeFactory.Create(captain.Runtime);
                    await runtime.StopAsync(session.ProcessId.Value, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error stopping planning process " + session.ProcessId.Value + " for session " + session.Id + ": " + ex.Message);
                }
            }

            if (!String.IsNullOrEmpty(session.DockId))
            {
                try
                {
                    await _Docks.ReclaimAsync(session.DockId, session.TenantId, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error reclaiming planning dock " + session.DockId + " for session " + session.Id + ": " + ex.Message);
                }
            }

            if (captain != null)
            {
                captain.State = CaptainStateEnum.Idle;
                captain.CurrentMissionId = null;
                captain.CurrentDockId = null;
                captain.ProcessId = null;
                captain.LastHeartbeatUtc = DateTime.UtcNow;
                captain.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                _WebSocketHub?.BroadcastCaptainChange(captain.Id, captain.State.ToString(), captain.Name);
            }

            session.Status = PlanningSessionStatusEnum.Stopped;
            session.ProcessId = null;
            session.CompletedUtc = DateTime.UtcNow;
            session.LastUpdateUtc = DateTime.UtcNow;
            session = await _Database.PlanningSessions.UpdateAsync(session, token).ConfigureAwait(false);
            BroadcastSessionChanged(session);

            await _EmitEventAsync(
                "planning-session.stopped",
                "Planning session " + session.Id + " stopped",
                "planning-session",
                session.Id,
                session.CaptainId,
                null,
                session.VesselId,
                null).ConfigureAwait(false);

            return session;
        }

        /// <summary>
        /// Create a voyage from a planning session.
        /// </summary>
        public async Task<Voyage> DispatchAsync(PlanningSession session, PlanningSessionDispatchRequest request, CancellationToken token = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (request == null) throw new ArgumentNullException(nameof(request));

            session = await RequireSessionAsync(session.Id, token).ConfigureAwait(false);

            PlanningSessionMessage? sourceMessage = null;
            if (!String.IsNullOrWhiteSpace(request.MessageId))
            {
                sourceMessage = await _Database.PlanningSessionMessages.ReadAsync(request.MessageId, token).ConfigureAwait(false);
                if (sourceMessage == null || sourceMessage.PlanningSessionId != session.Id)
                    throw new InvalidOperationException("Planning message not found: " + request.MessageId);
            }
            else
            {
                List<PlanningSessionMessage> messages = await _Database.PlanningSessionMessages
                    .EnumerateBySessionAsync(session.Id, token)
                    .ConfigureAwait(false);
                sourceMessage = messages
                    .Where(m => String.Equals(m.Role, "Assistant", StringComparison.OrdinalIgnoreCase))
                    .Where(m => !String.IsNullOrWhiteSpace(m.Content))
                    .OrderByDescending(m => m.Sequence)
                    .FirstOrDefault();
            }

            if (sourceMessage == null)
                throw new InvalidOperationException("No assistant planning output is available to dispatch.");

            string title = !String.IsNullOrWhiteSpace(request.Title)
                ? request.Title.Trim()
                : (!String.IsNullOrWhiteSpace(session.Title) ? session.Title.Trim() : sourceMessage.Content.Trim().Substring(0, Math.Min(80, sourceMessage.Content.Trim().Length)));
            string description = !String.IsNullOrWhiteSpace(request.Description)
                ? request.Description.Trim()
                : sourceMessage.Content.Trim();

            List<MissionDescription> missions = new List<MissionDescription>
            {
                new MissionDescription(title, description)
            };

            Voyage voyage = await _Admiral.DispatchVoyageAsync(
                title,
                "Created from planning session " + session.Id,
                session.VesselId,
                missions,
                session.PipelineId,
                session.SelectedPlaybooks,
                token).ConfigureAwait(false);

            voyage.SourcePlanningSessionId = session.Id;
            voyage.SourcePlanningMessageId = sourceMessage.Id;
            voyage = await _Database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);

            _WebSocketHub?.BroadcastEvent(
                "planning-session.dispatch.created",
                "Dispatch created from planning session " + session.Id,
                new
                {
                    sessionId = session.Id,
                    voyageId = voyage.Id,
                    messageId = sourceMessage.Id
                });

            await _EmitEventAsync(
                "planning-session.dispatch.created",
                "Planning session " + session.Id + " created voyage " + voyage.Id,
                "planning-session",
                session.Id,
                session.CaptainId,
                null,
                session.VesselId,
                voyage.Id).ConfigureAwait(false);

            return voyage;
        }

        /// <summary>
        /// Recover persisted planning sessions on server start.
        /// </summary>
        public async Task RecoverSessionsAsync(CancellationToken token = default)
        {
            List<PlanningSession> active = await _Database.PlanningSessions
                .EnumerateAsync(token)
                .ConfigureAwait(false);

            foreach (PlanningSession session in active.Where(s =>
                s.Status == PlanningSessionStatusEnum.Active ||
                s.Status == PlanningSessionStatusEnum.Responding ||
                s.Status == PlanningSessionStatusEnum.Stopping))
            {
                try
                {
                    Captain? captain = await _Database.Captains.ReadAsync(session.CaptainId, token).ConfigureAwait(false);
                    if (captain == null)
                    {
                        session.Status = PlanningSessionStatusEnum.Failed;
                        session.FailureReason = "Captain not found during recovery.";
                        session.ProcessId = null;
                        session.CompletedUtc = DateTime.UtcNow;
                        await _Database.PlanningSessions.UpdateAsync(session, token).ConfigureAwait(false);
                        continue;
                    }

                    if (session.Status == PlanningSessionStatusEnum.Responding && session.ProcessId.HasValue)
                    {
                        bool running = false;
                        try
                        {
                            Process process = Process.GetProcessById(session.ProcessId.Value);
                            running = !process.HasExited;
                        }
                        catch
                        {
                            running = false;
                        }

                        if (running)
                        {
                            try
                            {
                                Armada.Runtimes.Interfaces.IAgentRuntime runtime = _RuntimeFactory.Create(captain.Runtime);
                                await runtime.StopAsync(session.ProcessId.Value, token).ConfigureAwait(false);
                            }
                            catch
                            {
                            }
                        }

                        List<PlanningSessionMessage> messages = await _Database.PlanningSessionMessages
                            .EnumerateBySessionAsync(session.Id, token)
                            .ConfigureAwait(false);
                        int nextSequence = messages.Count == 0 ? 1 : messages.Max(m => m.Sequence) + 1;
                        PlanningSessionMessage interruption = new PlanningSessionMessage
                        {
                            PlanningSessionId = session.Id,
                            TenantId = session.TenantId,
                            UserId = session.UserId,
                            Role = "System",
                            Sequence = nextSequence,
                            Content = "The previous planning response was interrupted during server recovery. You can continue the session from here.",
                            CreatedUtc = DateTime.UtcNow,
                            LastUpdateUtc = DateTime.UtcNow
                        };
                        await _Database.PlanningSessionMessages.CreateAsync(interruption, token).ConfigureAwait(false);
                    }

                    captain.State = CaptainStateEnum.Planning;
                    captain.CurrentMissionId = null;
                    captain.CurrentDockId = session.DockId;
                    captain.ProcessId = null;
                    captain.LastHeartbeatUtc = DateTime.UtcNow;
                    captain.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                    _WebSocketHub?.BroadcastCaptainChange(captain.Id, captain.State.ToString(), captain.Name);

                    if (session.Status == PlanningSessionStatusEnum.Responding || session.Status == PlanningSessionStatusEnum.Stopping)
                    {
                        session.Status = PlanningSessionStatusEnum.Active;
                        session.ProcessId = null;
                        session.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.PlanningSessions.UpdateAsync(session, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "planning session recovery error for " + session.Id + ": " + ex.Message);
                }
            }
        }

        #endregion

        #region Private-Methods

        private async Task ExecuteTurnAsync(string sessionId, string assistantMessageId)
        {
            try
            {
                PlanningSession session = await RequireSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                PlanningSessionMessage assistantMessage = await RequireMessageAsync(assistantMessageId, CancellationToken.None).ConfigureAwait(false);
                Captain captain = await RequireCaptainAsync(session.CaptainId, CancellationToken.None).ConfigureAwait(false);
                Vessel vessel = await RequireVesselAsync(session.VesselId, CancellationToken.None).ConfigureAwait(false);
                Dock dock = await RequireDockAsync(session.DockId, CancellationToken.None).ConfigureAwait(false);
                List<PlanningSessionMessage> messages = await _Database.PlanningSessionMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false);

                string promptFilePath = await WritePromptFileAsync(session, captain, vessel, dock, messages, CancellationToken.None).ConfigureAwait(false);
                string prompt = "Read `" + promptFilePath + "` and continue this Armada planning conversation. Stay in planning mode and answer the latest user message directly.";

                string turnDir = Path.Combine(_Settings.LogDirectory, "planning-sessions", session.Id);
                Directory.CreateDirectory(turnDir);
                string logFilePath = Path.Combine(turnDir, "session.log");
                string finalMessageFilePath = Path.Combine(turnDir, "final-" + assistantMessage.Id + ".txt");

                Armada.Runtimes.Interfaces.IAgentRuntime runtime = _RuntimeFactory.Create(captain.Runtime);
                TaskCompletionSource<int?> exitSource = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                object outputLock = new object();
                StringBuilder output = new StringBuilder();

                runtime.OnOutputReceived += (processId, line) =>
                {
                    string updatedContent;
                    lock (outputLock)
                    {
                        if (output.Length > 0) output.AppendLine();
                        output.Append(line);
                        updatedContent = output.ToString();
                    }

                    assistantMessage.Content = updatedContent;
                    assistantMessage.LastUpdateUtc = DateTime.UtcNow;
                    BroadcastMessageUpdated(assistantMessage);
                };
                runtime.OnProcessExited += (processId, exitCode) => exitSource.TrySetResult(exitCode);

                int processId = await runtime.StartAsync(
                    dock.WorktreePath ?? vessel.WorkingDirectory ?? vessel.LocalPath ?? Directory.GetCurrentDirectory(),
                    prompt,
                    logFilePath: logFilePath,
                    finalMessageFilePath: finalMessageFilePath,
                    model: captain.Model).ConfigureAwait(false);

                session.ProcessId = processId;
                session.LastUpdateUtc = DateTime.UtcNow;
                await _Database.PlanningSessions.UpdateAsync(session).ConfigureAwait(false);
                BroadcastSessionChanged(session);

                captain.ProcessId = processId;
                captain.LastHeartbeatUtc = DateTime.UtcNow;
                captain.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Captains.UpdateAsync(captain).ConfigureAwait(false);

                int? exitCode = await exitSource.Task.ConfigureAwait(false);

                string finalContent = assistantMessage.Content ?? String.Empty;
                try
                {
                    if (File.Exists(finalMessageFilePath))
                    {
                        string artifact = await File.ReadAllTextAsync(finalMessageFilePath).ConfigureAwait(false);
                        if (!String.IsNullOrWhiteSpace(artifact))
                            finalContent = artifact.Trim();
                    }
                }
                catch
                {
                }

                if (String.IsNullOrWhiteSpace(finalContent) && exitCode.HasValue && exitCode.Value != 0)
                {
                    finalContent = "Planning turn exited with code " + exitCode.Value + " before producing a final response.";
                }

                assistantMessage.Content = finalContent.Trim();
                assistantMessage.LastUpdateUtc = DateTime.UtcNow;
                await _Database.PlanningSessionMessages.UpdateAsync(assistantMessage).ConfigureAwait(false);
                BroadcastMessageUpdated(assistantMessage);

                captain = await RequireCaptainAsync(session.CaptainId, CancellationToken.None).ConfigureAwait(false);
                captain.ProcessId = null;
                captain.LastHeartbeatUtc = DateTime.UtcNow;
                captain.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Captains.UpdateAsync(captain).ConfigureAwait(false);

                session = await RequireSessionAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
                bool stopRequested = _ActiveTurns.TryGetValue(session.Id, out TurnState? turnState) && turnState.StopRequested;
                session.ProcessId = null;
                if (!stopRequested)
                {
                    session.Status = PlanningSessionStatusEnum.Active;
                    session.FailureReason = null;
                }
                session.LastUpdateUtc = DateTime.UtcNow;
                await _Database.PlanningSessions.UpdateAsync(session).ConfigureAwait(false);
                BroadcastSessionChanged(session);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "planning turn error for session " + sessionId + ": " + ex.Message);
                try
                {
                    PlanningSession? session = await _Database.PlanningSessions.ReadAsync(sessionId).ConfigureAwait(false);
                    PlanningSessionMessage? assistantMessage = await _Database.PlanningSessionMessages.ReadAsync(assistantMessageId).ConfigureAwait(false);
                    if (assistantMessage != null)
                    {
                        assistantMessage.Content = "Planning response failed: " + ex.Message;
                        assistantMessage.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.PlanningSessionMessages.UpdateAsync(assistantMessage).ConfigureAwait(false);
                        BroadcastMessageUpdated(assistantMessage);
                    }

                    if (session != null)
                    {
                        session.ProcessId = null;
                        if (!_ActiveTurns.TryGetValue(session.Id, out TurnState? turnState) || !turnState.StopRequested)
                        {
                            session.Status = PlanningSessionStatusEnum.Active;
                            session.FailureReason = ex.Message;
                        }
                        session.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.PlanningSessions.UpdateAsync(session).ConfigureAwait(false);
                        BroadcastSessionChanged(session);
                    }

                    if (session != null)
                    {
                        Captain? captain = await _Database.Captains.ReadAsync(session.CaptainId).ConfigureAwait(false);
                        if (captain != null)
                        {
                            captain.ProcessId = null;
                            if (captain.State != CaptainStateEnum.Planning)
                                captain.State = CaptainStateEnum.Planning;
                            captain.LastHeartbeatUtc = DateTime.UtcNow;
                            captain.LastUpdateUtc = DateTime.UtcNow;
                            await _Database.Captains.UpdateAsync(captain).ConfigureAwait(false);
                            _WebSocketHub?.BroadcastCaptainChange(captain.Id, captain.State.ToString(), captain.Name);
                        }
                    }
                }
                catch (Exception nestedEx)
                {
                    _Logging.Warn(_Header + "planning turn recovery error for session " + sessionId + ": " + nestedEx.Message);
                }
            }
            finally
            {
                _ActiveTurns.TryRemove(sessionId, out _);
            }
        }

        private async Task<string> WritePromptFileAsync(
            PlanningSession session,
            Captain captain,
            Vessel vessel,
            Dock dock,
            List<PlanningSessionMessage> messages,
            CancellationToken token)
        {
            string turnDir = Path.Combine(_Settings.LogDirectory, "planning-sessions", session.Id);
            Directory.CreateDirectory(turnDir);

            string filePath = Path.Combine(turnDir, "context.md");
            string playbookMarkdown = await RenderPlaybooksMarkdownAsync(session, token).ConfigureAwait(false);
            string transcript = RenderTranscript(messages, 24000);

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("# Armada Planning Session");
            builder.AppendLine();
            builder.AppendLine("You are helping a human plan work before dispatching it through Armada.");
            builder.AppendLine("Stay in planning mode. You may inspect the repository and reason concretely, but do not claim that work has been dispatched, landed, or merged unless the transcript explicitly says that already happened.");
            builder.AppendLine();
            builder.AppendLine("## Session Context");
            builder.AppendLine("- Session ID: `" + session.Id + "`");
            builder.AppendLine("- Captain: `" + captain.Name + "` (`" + captain.Runtime + "`)");
            builder.AppendLine("- Vessel: `" + vessel.Name + "`");
            builder.AppendLine("- Branch: `" + (dock.BranchName ?? session.BranchName ?? vessel.DefaultBranch) + "`");
            if (!String.IsNullOrWhiteSpace(session.PipelineId))
                builder.AppendLine("- Pipeline ID: `" + session.PipelineId + "`");
            if (!String.IsNullOrWhiteSpace(captain.SystemInstructions))
            {
                builder.AppendLine();
                builder.AppendLine("## Captain Instructions");
                builder.AppendLine(captain.SystemInstructions.Trim());
            }
            if (!String.IsNullOrWhiteSpace(vessel.ProjectContext))
            {
                builder.AppendLine();
                builder.AppendLine("## Project Context");
                builder.AppendLine(vessel.ProjectContext.Trim());
            }
            if (!String.IsNullOrWhiteSpace(vessel.StyleGuide))
            {
                builder.AppendLine();
                builder.AppendLine("## Style Guide");
                builder.AppendLine(vessel.StyleGuide.Trim());
            }
            if (vessel.EnableModelContext && !String.IsNullOrWhiteSpace(vessel.ModelContext))
            {
                builder.AppendLine();
                builder.AppendLine("## Model Context");
                builder.AppendLine(vessel.ModelContext.Trim());
            }
            if (!String.IsNullOrWhiteSpace(playbookMarkdown))
            {
                builder.AppendLine();
                builder.AppendLine("## Selected Playbooks");
                builder.AppendLine(playbookMarkdown);
            }
            builder.AppendLine();
            builder.AppendLine("## Conversation Transcript");
            builder.AppendLine(transcript);
            builder.AppendLine();
            builder.AppendLine("## Response Guidance");
            builder.AppendLine("- Continue the conversation naturally.");
            builder.AppendLine("- Be concrete about architecture, implementation order, and risk when relevant.");
            builder.AppendLine("- If useful, include a short `Dispatch Draft` section that could be sent to Armada as a mission.");

            await File.WriteAllTextAsync(filePath, builder.ToString(), token).ConfigureAwait(false);
            return filePath;
        }

        private async Task<string> RenderPlaybooksMarkdownAsync(PlanningSession session, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(session.TenantId) || session.SelectedPlaybooks.Count == 0)
                return String.Empty;

            try
            {
                List<Playbook> playbooks = await _Playbooks.ResolveSelectionsAsync(session.TenantId!, session.SelectedPlaybooks, token).ConfigureAwait(false);
                List<string> sections = new List<string>();
                for (int i = 0; i < playbooks.Count; i++)
                {
                    Playbook playbook = playbooks[i];
                    SelectedPlaybook selection = session.SelectedPlaybooks[i];
                    sections.Add(
                        "### " + playbook.FileName + "\n" +
                        "- Delivery Mode: `" + selection.DeliveryMode + "`\n" +
                        (!String.IsNullOrWhiteSpace(playbook.Description) ? playbook.Description!.Trim() + "\n\n" : "\n") +
                        playbook.Content.Trim());
                }

                return String.Join("\n\n", sections);
            }
            catch (Exception ex)
            {
                return "_Playbook context could not be loaded: " + ex.Message + "_";
            }
        }

        private static string RenderTranscript(List<PlanningSessionMessage> messages, int maxChars)
        {
            List<string> parts = messages
                .OrderBy(m => m.Sequence)
                .Select(m => "### " + m.Role + "\n" + (m.Content ?? String.Empty).Trim())
                .ToList();
            string transcript = String.Join("\n\n", parts).Trim();
            if (transcript.Length <= maxChars)
                return transcript;

            return "... transcript truncated ...\n\n" + transcript.Substring(transcript.Length - maxChars, maxChars);
        }

        private void BroadcastSessionChanged(PlanningSession session)
        {
            _WebSocketHub?.BroadcastEvent(
                "planning-session.changed",
                "Planning session updated",
                new { session });
        }

        private void BroadcastMessageCreated(PlanningSessionMessage message)
        {
            _WebSocketHub?.BroadcastEvent(
                "planning-session.message.created",
                "Planning session message created",
                new
                {
                    sessionId = message.PlanningSessionId,
                    message
                });
        }

        private void BroadcastMessageUpdated(PlanningSessionMessage message)
        {
            _WebSocketHub?.BroadcastEvent(
                "planning-session.message.updated",
                "Planning session message updated",
                new
                {
                    sessionId = message.PlanningSessionId,
                    message
                });
        }

        private async Task<PlanningSession> RequireSessionAsync(string sessionId, CancellationToken token)
        {
            PlanningSession? session = await _Database.PlanningSessions.ReadAsync(sessionId, token).ConfigureAwait(false);
            if (session == null) throw new InvalidOperationException("Planning session not found: " + sessionId);
            return session;
        }

        private async Task<PlanningSessionMessage> RequireMessageAsync(string messageId, CancellationToken token)
        {
            PlanningSessionMessage? message = await _Database.PlanningSessionMessages.ReadAsync(messageId, token).ConfigureAwait(false);
            if (message == null) throw new InvalidOperationException("Planning session message not found: " + messageId);
            return message;
        }

        private async Task<Captain> RequireCaptainAsync(string captainId, CancellationToken token)
        {
            Captain? captain = await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
            if (captain == null) throw new InvalidOperationException("Captain not found: " + captainId);
            return captain;
        }

        private async Task<Vessel> RequireVesselAsync(string vesselId, CancellationToken token)
        {
            Vessel? vessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (vessel == null) throw new InvalidOperationException("Vessel not found: " + vesselId);
            return vessel;
        }

        private async Task<Dock> RequireDockAsync(string? dockId, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(dockId))
                throw new InvalidOperationException("Planning session does not have a dock.");
            Dock? dock = await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
            if (dock == null) throw new InvalidOperationException("Dock not found: " + dockId);
            return dock;
        }

        #endregion
    }
}
