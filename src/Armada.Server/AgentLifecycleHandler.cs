namespace Armada.Server
{
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server.WebSocket;

    /// <summary>
    /// Handles agent process lifecycle: launching, heartbeats, output parsing, process exit, and stopping.
    /// </summary>
    public class AgentLifecycleHandler
    {
        #region Private-Members

        private string _Header = "[AgentLifecycle] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private AgentRuntimeFactory _RuntimeFactory;
        private MuxCliService _MuxCli;
        private IAdmiralService _Admiral;
        private IMessageTemplateService _TemplateService;
        private IPromptTemplateService? _PromptTemplateService;
        private ArmadaWebSocketHub? _WebSocketHub;
        private Func<string, string, string?, string?, string?, string?, string?, string?, Task> _EmitEventAsync;
        private readonly TimeSpan _ModelValidationTimeout;
        private readonly TimeSpan _MissionHeartbeatPersistInterval = TimeSpan.FromSeconds(15);
        private ProviderProgressTracker? _ProviderProgress;

        /// <summary>
        /// Maximum characters retained per mission for streamed agent output.
        /// Verbose runtimes (notably Codex on stderr) can otherwise grow this buffer
        /// to multi-GB and pin server memory until completion.
        /// </summary>
        private const int _MissionOutputCapChars = 256 * 1024;

        /// <summary>
        /// Marker inserted in the streamed buffer when output is truncated to retain only the tail.
        /// </summary>
        private const string _MissionOutputTruncationMarker = "[ARMADA: streamed output truncated to retain tail]";

        /// <summary>
        /// Accumulates agent stdout per mission for pipeline handoff. Bounded per mission
        /// to a finite cap with tail-retention to prevent unbounded growth on verbose runtimes.
        /// </summary>
        private System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.StringBuilder> _MissionOutput = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.StringBuilder>();

        /// <summary>
        /// Maximum papercuts stored per mission. A captain that reports more than this is describing its
        /// own difficulty rather than repository friction, and the surplus would drown the real reports.
        /// </summary>
        private const int _MaxPapercutsPerMission = 10;

        /// <summary>
        /// Counts papercuts stored per mission so the per-mission cap is enforced across output lines.
        /// </summary>
        private System.Collections.Concurrent.ConcurrentDictionary<string, int> _MissionPapercutCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

        /// <summary>
        /// Throttles mission heartbeat persistence so verbose logs do not rewrite mission/voyage rows on every output line.
        /// </summary>
        private System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _MissionHeartbeatWrites = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();

        /// <summary>
        /// Tracks per-mission final response artifacts so canonical agent output can be recovered even if live streaming is noisy.
        /// </summary>
        private System.Collections.Concurrent.ConcurrentDictionary<string, string> _MissionFinalMessageFiles = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        /// <summary>
        /// Tracks launches that have started but have not yet completed HandleLaunchAgentAsync registration.
        /// This closes the race where a fast process can emit output or exit before the PID mapping is written.
        /// </summary>
        private System.Collections.Concurrent.ConcurrentDictionary<string, (string CaptainId, string MissionId)> _PendingLaunches = new System.Collections.Concurrent.ConcurrentDictionary<string, (string CaptainId, string MissionId)>();

        /// <summary>
        /// Maps process IDs to captain IDs for progress tracking.
        /// </summary>
        private Dictionary<int, string> _ProcessToCaptain = new Dictionary<int, string>();

        /// <summary>
        /// Maps process IDs to mission IDs for per-mission progress tracking.
        /// </summary>
        private Dictionary<int, string> _ProcessToMission = new Dictionary<int, string>();

        /// <summary>
        /// Tracks process IDs whose exit has been received via the OnProcessExited callback.
        /// Used by the health check to avoid racing with the async exit handler.
        /// Entries are pruned after 5 minutes.
        /// </summary>
        private System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _HandledProcessExits = new System.Collections.Concurrent.ConcurrentDictionary<int, DateTime>();

        /// <summary>
        /// Tracks per-process liveness heartbeat loops so silent-but-busy runtimes still refresh telemetry.
        /// </summary>
        private System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _ProcessHeartbeatLoops = new System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="runtimeFactory">Agent runtime factory.</param>
        /// <param name="admiral">Admiral service.</param>
        /// <param name="templateService">Message template service.</param>
        /// <param name="promptTemplateService">Prompt template service (optional).</param>
        /// <param name="webSocketHub">WebSocket hub (nullable).</param>
        /// <param name="emitEventAsync">Delegate to emit events.</param>
        public AgentLifecycleHandler(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            AgentRuntimeFactory runtimeFactory,
            IAdmiralService admiral,
            IMessageTemplateService templateService,
            IPromptTemplateService? promptTemplateService,
            ArmadaWebSocketHub? webSocketHub,
            Func<string, string, string?, string?, string?, string?, string?, string?, Task> emitEventAsync,
            TimeSpan? modelValidationTimeout = null)
        {
            // Overridable so a test can drive the timeout path in seconds rather than needing a fake
            // runtime that outlasts the production ceiling. Defaults to the production value.
            _ModelValidationTimeout = modelValidationTimeout ?? TimeSpan.FromSeconds(30);
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _RuntimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
            _MuxCli = new MuxCliService(_Logging);
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _TemplateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
            _PromptTemplateService = promptTemplateService;
            _WebSocketHub = webSocketHub;
            _EmitEventAsync = emitEventAsync ?? throw new ArgumentNullException(nameof(emitEventAsync));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Set the provider-progress tracker. The handler records authoritative provider-progress
        /// signals (runtime token usage) keyed by captain id into this tracker so the autonomous
        /// recovery orchestrator can classify a provider-silent stall separately from a captain-wide
        /// heartbeat stall. Set after construction, mirroring <c>SetWebSocketHub</c>.
        /// </summary>
        /// <param name="tracker">Provider-progress tracker.</param>
        public void SetProviderProgress(ProviderProgressTracker tracker)
        {
            _ProviderProgress = tracker ?? throw new ArgumentNullException(nameof(tracker));
        }

        /// <summary>
        /// Set or update the WebSocket hub reference (created after this handler).
        /// </summary>
        /// <summary>
        /// Retrieve and clear accumulated stdout output for a mission.
        /// Used by pipeline handoff to pass agent output to the next stage.
        /// </summary>
        public string? GetAndClearMissionOutput(string missionId)
        {
            if (String.IsNullOrEmpty(missionId)) return null;
            _MissionPapercutCounts.TryRemove(missionId, out _);
            string? streamedOutput = null;
            if (_MissionOutput.TryRemove(missionId, out System.Text.StringBuilder? sb))
            {
                lock (sb)
                {
                    streamedOutput = sb.ToString().Trim();
                }
                if (String.IsNullOrEmpty(streamedOutput))
                    streamedOutput = null;
            }

            if (_MissionFinalMessageFiles.TryRemove(missionId, out string? finalMessageFilePath) &&
                !String.IsNullOrEmpty(finalMessageFilePath))
            {
                try
                {
                    if (File.Exists(finalMessageFilePath))
                    {
                        string finalMessage = File.ReadAllText(finalMessageFilePath).Trim();
                        try { File.Delete(finalMessageFilePath); } catch { }
                        if (!String.IsNullOrEmpty(finalMessage))
                            return finalMessage;
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error reading final message artifact for mission " + missionId + ": " + ex.Message);
                }
            }

            return streamedOutput;
        }

        /// <summary>
        /// Check whether a process exit has already been received for the given PID.
        /// The health check uses this to avoid triggering recovery for a process
        /// whose exit is already being handled by the async exit callback.
        /// </summary>
        /// <param name="processId">OS process ID to check.</param>
        /// <returns>True if the exit callback has already fired for this PID.</returns>
        public bool IsProcessExitHandled(int processId)
        {
            // Prune stale entries older than 5 minutes
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
            foreach (System.Collections.Generic.KeyValuePair<int, DateTime> kvp in _HandledProcessExits)
            {
                if (kvp.Value < cutoff)
                    _HandledProcessExits.TryRemove(kvp.Key, out _);
            }

            return _HandledProcessExits.ContainsKey(processId);
        }

        /// <summary>
        /// Set or update the WebSocket hub reference (created after this handler).
        /// </summary>
        /// <param name="hub">WebSocket hub instance, or null.</param>
        public void SetWebSocketHub(ArmadaWebSocketHub? hub)
        {
            _WebSocketHub = hub;
        }

        /// <summary>
        /// Validate that the captain's configured model can be launched by its runtime.
        /// Returns null if validation succeeds, otherwise an error message.
        /// </summary>
        /// <param name="captain">Captain to validate.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Null if valid, otherwise an error message.</returns>
        public Task<string?> ValidateCaptainModelAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (captain.Runtime == AgentRuntimeEnum.Mux)
            {
                return ValidateMuxCaptainBypassingQuotaAsync(captain, token);
            }

            return ValidateModelBypassingQuotaAsync(captain.Runtime, captain.Model, captain, token);
        }

        private async Task<string?> ValidateMuxCaptainBypassingQuotaAsync(Captain captain, CancellationToken token)
        {
            string? error = await ValidateMuxCaptainAsync(captain, token).ConfigureAwait(false);
            if (error != null && ProviderQuotaLimitDetector.IsQuotaLimitSignal(error))
            {
                return null;
            }

            return error;
        }

        private async Task<string?> ValidateModelBypassingQuotaAsync(AgentRuntimeEnum runtimeType, string? model, Captain? captain, CancellationToken token)
        {
            string? error = await ValidateModelAsync(runtimeType, model, captain, token).ConfigureAwait(false);
            if (error != null && ProviderQuotaLimitDetector.IsQuotaLimitSignal(error))
            {
                return null;
            }

            return error;
        }

        /// <summary>
        /// Validate that the given runtime can start with the requested model.
        /// Returns null if validation succeeds, otherwise an error message.
        /// </summary>
        /// <param name="runtimeType">Runtime to validate.</param>
        /// <param name="model">Model to validate.</param>
        /// <param name="captain">Captain being validated; its per-captain provider credential is
        /// used for the probe when set.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Null if valid, otherwise an error message.</returns>
        public async Task<string?> ValidateModelAsync(AgentRuntimeEnum runtimeType, string? model, Captain? captain = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(model))
                return null;

            string validationDirectory = Path.Combine(Path.GetTempPath(), "armada-model-validation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(validationDirectory);

            Armada.Runtimes.Interfaces.IAgentRuntime runtime;
            try
            {
                runtime = _RuntimeFactory.Create(runtimeType);
            }
            catch (Exception ex)
            {
                try { Directory.Delete(validationDirectory, true); } catch { }
                return "Unable to create runtime " + runtimeType + " for model validation: " + ex.Message;
            }

            object outputLock = new object();
            System.Text.StringBuilder output = new System.Text.StringBuilder();
            TaskCompletionSource<int?> exitSource = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.OnOutputReceived += (processId, line) =>
            {
                lock (outputLock)
                {
                    if (output.Length < 4096)
                    {
                        output.AppendLine(line);
                    }
                }
            };
            runtime.OnProcessExited += (processId, exitCode) => exitSource.TrySetResult(exitCode);

            int? processId = null;

            try
            {
                await InitializeValidationWorkspaceAsync(runtimeType, validationDirectory, token).ConfigureAwait(false);

                processId = await runtime.StartAsync(
                    validationDirectory,
                    "Respond with the single word OK.",
                    model: model,
                    captain: captain,
                    token: token).ConfigureAwait(false);

                Task completedTask = await Task.WhenAny(
                    exitSource.Task,
                    Task.Delay(_ModelValidationTimeout, token)).ConfigureAwait(false);

                if (completedTask == exitSource.Task)
                {
                    int? exitCode = await exitSource.Task.ConfigureAwait(false);
                    if (!exitCode.HasValue || exitCode.Value == 0)
                    {
                        return null;
                    }

                    string? details;
                    lock (outputLock)
                    {
                        details = ExtractModelValidationError(output.ToString());
                    }

                    if (!String.IsNullOrEmpty(details))
                    {
                        return "Model '" + model + "' failed validation for runtime " + runtimeType + ": " + details;
                    }

                    return "Model '" + model + "' failed validation for runtime " + runtimeType + " with exit code " + exitCode.Value + ".";
                }

                token.ThrowIfCancellationRequested();

                string? timeoutDetails;
                lock (outputLock)
                {
                    timeoutDetails = ExtractModelValidationError(output.ToString());
                }

                string timeoutMessage =
                    "Model '" + model + "' failed validation for runtime " + runtimeType +
                    ": validation timed out after " + _ModelValidationTimeout.TotalSeconds.ToString("0") + " seconds.";

                if (!String.IsNullOrEmpty(timeoutDetails))
                {
                    timeoutMessage += " " + timeoutDetails;
                }

                return timeoutMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return "Model '" + model + "' failed validation for runtime " + runtimeType + ": " + ex.Message;
            }
            finally
            {
                if (processId.HasValue)
                {
                    try
                    {
                        await runtime.StopAsync(processId.Value, token).ConfigureAwait(false);
                    }
                    catch { }
                }

                try { Directory.Delete(validationDirectory, true); } catch { }
            }
        }

        /// <summary>
        /// Launch an agent process for the given captain, mission, and dock.
        /// </summary>
        public async Task<int> HandleLaunchAgentAsync(Captain captain, Mission mission, Dock dock)
        {
            _Logging.Info(_Header + "launching " + captain.Runtime + " agent for captain " + captain.Id);
            Armada.Runtimes.Interfaces.IAgentRuntime runtime = _RuntimeFactory.Create(captain.Runtime);
            string launchKey = captain.Id + ":" + mission.Id;
            _PendingLaunches[launchKey] = (captain.Id, mission.Id);
            runtime.OnProcessStarted += processId => HandleProcessStarted(processId, launchKey);
            runtime.OnOutputReceived += HandleAgentOutput;
            runtime.OnOutputReceived += HandleAgentHeartbeat;
            runtime.OnTokenUsageReceived += HandleTokenUsage;
            runtime.OnProviderProgressReceived += HandleProviderProgress;
            runtime.OnProcessExited += HandleAgentProcessExited;

            Vessel? vessel = null;
            if (!String.IsNullOrEmpty(mission.VesselId))
            {
                vessel = await _Database.Vessels.ReadAsync(mission.VesselId).ConfigureAwait(false);
            }
            Vessel launchVessel = vessel ?? new Vessel
            {
                Name = "Unknown Vessel",
                DefaultBranch = dock.BranchName ?? mission.BranchName ?? "main"
            };
            string prompt = await MissionPromptBuilder.BuildLaunchPromptAsync(
                mission,
                launchVessel,
                captain,
                dock,
                _PromptTemplateService).ConfigureAwait(false);

            if (_Settings.MessageTemplates.EnableCommitMetadata)
            {
                Dictionary<string, string> templateContext = _TemplateService.BuildContext(mission, captain, null, null, dock);
                string commitInstructions = _TemplateService.RenderCommitInstructions(_Settings.MessageTemplates, templateContext);
                if (!String.IsNullOrEmpty(commitInstructions))
                    prompt += "\n\n" + commitInstructions;
            }

            // Record what the launch prompt actually cost. Measured here rather than asked of the captain:
            // a captain can only estimate its own prompt, and some runtimes report no token usage at all.
            await RecordLaunchPromptBytesAsync(mission, captain, prompt).ConfigureAwait(false);

            string missionLogDir = Path.Combine(_Settings.LogDirectory, "missions");
            string logFilePath = Path.Combine(missionLogDir, mission.Id + ".log");
            string finalMessageDir = Path.Combine(_Settings.LogDirectory, "final-messages");
            Directory.CreateDirectory(finalMessageDir);
            string finalMessageFilePath = Path.Combine(finalMessageDir, mission.Id + ".txt");
            try
            {
                if (File.Exists(finalMessageFilePath))
                    File.Delete(finalMessageFilePath);
            }
            catch { }
            _MissionFinalMessageFiles[mission.Id] = finalMessageFilePath;
            string captainLogDir = Path.Combine(_Settings.LogDirectory, "captains");
            Directory.CreateDirectory(captainLogDir);
            string captainLogPointer = Path.Combine(captainLogDir, captain.Id + ".current");
            File.WriteAllText(captainLogPointer, logFilePath);

            int processId;
            try
            {
                processId = await runtime.StartAsync(
                    dock.WorktreePath ?? throw new InvalidOperationException("Dock worktree path is null"),
                    prompt,
                    logFilePath: logFilePath,
                    finalMessageFilePath: finalMessageFilePath,
                    model: captain.Model,
                    captain: captain).ConfigureAwait(false);
            }
            catch
            {
                _PendingLaunches.TryRemove(launchKey, out _);
                _MissionFinalMessageFiles.TryRemove(mission.Id, out _);
                throw;
            }

            await PersistStartedProcessIdAsync(processId, launchKey).ConfigureAwait(false);

            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain[processId] = captain.Id;
                _ProcessToMission[processId] = mission.Id;
            }
            _PendingLaunches.TryRemove(launchKey, out _);

            _Logging.Info(_Header + "agent process " + processId + " started for captain " + captain.Id + " (log: " + logFilePath + ")");
            StartProcessLivenessHeartbeat(processId, captain.Id, mission.Id);

            await _EmitEventAsync("captain.launched", "Agent process started for captain " + captain.Name,
                "captain", captain.Id,
                captain.Id, mission.Id, mission.VesselId, mission.VoyageId).ConfigureAwait(false);

            if (_WebSocketHub != null)
            {
                _WebSocketHub.BroadcastCaptainChange(captain.Id, captain.State.ToString(), captain.Name);
                _WebSocketHub.BroadcastMissionChange(mission.Id, mission.Status.ToString(), mission.Title);
            }

            return processId;
        }

        /// <summary>
        /// Start a periodic heartbeat loop for a tracked process so telemetry stays fresh
        /// even when the runtime is busy but not emitting output.
        /// </summary>
        private void StartProcessLivenessHeartbeat(int processId, string captainId, string missionId)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            if (!_ProcessHeartbeatLoops.TryAdd(processId, cts))
            {
                cts.Dispose();
                return;
            }

            TimeSpan interval = TimeSpan.FromSeconds(Math.Max(5, _Settings.HeartbeatIntervalSeconds));

            // Read the token HERE, while the source is guaranteed alive. Reading cts.Token inside the
            // task body raced with StopProcessLivenessHeartbeat disposing the source: the property
            // throws ObjectDisposedException, and because it sat outside the try block the
            // fire-and-forget task faulted with nobody observing it, so the exception resurfaced from
            // the finalizer thread. A CancellationToken captured before disposal stays usable.
            CancellationToken token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(interval, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) break;
                        if (!IsTrackedProcessAlive(processId)) break;

                        string? mappedCaptainId = null;
                        string? mappedMissionId = null;
                        lock (_ProcessToCaptain)
                        {
                            _ProcessToCaptain.TryGetValue(processId, out mappedCaptainId);
                            _ProcessToMission.TryGetValue(processId, out mappedMissionId);
                        }

                        if (!String.Equals(mappedCaptainId, captainId, StringComparison.Ordinal) ||
                            !String.Equals(mappedMissionId, missionId, StringComparison.Ordinal))
                        {
                            break;
                        }

                        try { await _Database.Captains.UpdateHeartbeatAsync(captainId).ConfigureAwait(false); }
                        catch { }

                        try { await _Database.Missions.UpdateHeartbeatAsync(missionId).ConfigureAwait(false); }
                        catch { }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                finally
                {
                    if (_ProcessHeartbeatLoops.TryRemove(processId, out CancellationTokenSource? removed))
                    {
                        removed.Dispose();
                    }
                }
            });
        }

        /// <summary>
        /// Stop the periodic heartbeat loop for a tracked process.
        /// </summary>
        private void StopProcessLivenessHeartbeat(int processId)
        {
            if (_ProcessHeartbeatLoops.TryRemove(processId, out CancellationTokenSource? cts))
            {
                try { cts.Cancel(); }
                catch { }
                cts.Dispose();
            }
        }

        /// <summary>
        /// Determine whether the tracked wrapper process is still alive.
        /// </summary>
        private static bool IsTrackedProcessAlive(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Register PID-to-captain/mission mapping as soon as a launched process exposes a PID.
        /// </summary>
        private void HandleProcessStarted(int processId, string launchKey)
        {
            if (!_PendingLaunches.TryGetValue(launchKey, out (string CaptainId, string MissionId) launch))
                return;

            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain[processId] = launch.CaptainId;
                _ProcessToMission[processId] = launch.MissionId;
            }

            _ = PersistStartedProcessIdAsync(processId, launchKey);
        }

        /// <summary>
        /// Persist a launch PID while the launch key still matches the active assignment.
        /// </summary>
        private async Task PersistStartedProcessIdAsync(int processId, string launchKey)
        {
            if (!_PendingLaunches.TryGetValue(launchKey, out (string CaptainId, string MissionId) launch))
                return;

            try
            {
                Captain? captain = await _Database.Captains.ReadAsync(launch.CaptainId).ConfigureAwait(false);
                Mission? mission = await _Database.Missions.ReadAsync(launch.MissionId).ConfigureAwait(false);

                if (!_PendingLaunches.TryGetValue(launchKey, out (string CaptainId, string MissionId) currentLaunch) ||
                    !String.Equals(currentLaunch.CaptainId, launch.CaptainId, StringComparison.Ordinal) ||
                    !String.Equals(currentLaunch.MissionId, launch.MissionId, StringComparison.Ordinal))
                {
                    return;
                }

                bool captainMatches = captain != null &&
                    captain.State == CaptainStateEnum.Working &&
                    String.Equals(captain.CurrentMissionId, launch.MissionId, StringComparison.Ordinal) &&
                    (!captain.ProcessId.HasValue || captain.ProcessId.Value == processId);

                bool missionMatches = mission != null &&
                    String.Equals(mission.CaptainId, launch.CaptainId, StringComparison.Ordinal) &&
                    (mission.Status == MissionStatusEnum.Assigned ||
                     mission.Status == MissionStatusEnum.InProgress ||
                     mission.Status == MissionStatusEnum.Review ||
                     mission.Status == MissionStatusEnum.Testing) &&
                    (!mission.ProcessId.HasValue || mission.ProcessId.Value == processId);

                if (!captainMatches || !missionMatches)
                    return;

                captain!.ProcessId = processId;
                await _Database.Captains.UpdateAsync(captain).ConfigureAwait(false);

                mission!.ProcessId = processId;
                if (!mission.StartedUtc.HasValue)
                    mission.StartedUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to persist launch process " + processId + " for launch " + launchKey + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Handle heartbeat from an agent process output line.
        /// </summary>
        public void HandleAgentHeartbeat(int processId, string line)
        {
            string? captainId = null;
            string? missionId = null;
            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain.TryGetValue(processId, out captainId);
                _ProcessToMission.TryGetValue(processId, out missionId);
            }
            if (String.IsNullOrEmpty(captainId)) return;

            bool persistMissionHeartbeat = false;
            DateTime nowUtc = DateTime.UtcNow;
            if (!String.IsNullOrEmpty(missionId))
            {
                string capturedMissionId = missionId;
                _MissionHeartbeatWrites.AddOrUpdate(
                    capturedMissionId,
                    _ =>
                    {
                        persistMissionHeartbeat = true;
                        return nowUtc;
                    },
                    (_, previous) =>
                    {
                        if (nowUtc - previous >= _MissionHeartbeatPersistInterval)
                        {
                            persistMissionHeartbeat = true;
                            return nowUtc;
                        }

                        return previous;
                    });
            }

            string capturedCaptainId = captainId;
            _ = Task.Run(async () =>
            {
                try { await _Database.Captains.UpdateHeartbeatAsync(captainId).ConfigureAwait(false); }
                catch { }

                if (!persistMissionHeartbeat || String.IsNullOrEmpty(missionId)) return;

                try
                {
                    await _Database.Missions.UpdateHeartbeatAsync(missionId).ConfigureAwait(false);
                }
                catch
                {
                    _MissionHeartbeatWrites.TryRemove(missionId, out _);
                }
            });
        }

        /// <summary>
        /// Handle output from an agent process, parsing progress signals.
        /// </summary>
        public void HandleAgentOutput(int processId, string line)
        {
            // Accumulate stdout for pipeline handoff
            string? outputMissionId = null;
            lock (_ProcessToCaptain)
            {
                _ProcessToMission.TryGetValue(processId, out outputMissionId);
            }
            if (!String.IsNullOrEmpty(outputMissionId) && !IsMissionActivityRecord(line))
            {
                System.Text.StringBuilder sb = _MissionOutput.GetOrAdd(outputMissionId, _ => new System.Text.StringBuilder());
                AppendBounded(sb, line);
            }

            ProgressParser.ProgressSignal? signal = ProgressParser.TryParse(line);
            if (signal == null) return;

            string? captainId = null;
            string? missionId = null;
            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain.TryGetValue(processId, out captainId);
                _ProcessToMission.TryGetValue(processId, out missionId);
            }

            if (String.IsNullOrEmpty(captainId)) return;

            // A papercut is a report about the work, not a report of progress. It takes its own path so
            // it never transitions a mission and never lands in the progress signal stream.
            if (String.Equals(signal.Type, PapercutParser.SignalType, StringComparison.OrdinalIgnoreCase))
            {
                HandlePapercutSignal(captainId, missionId, signal.Value);
                return;
            }

            _Logging.Info(_Header + "progress signal from captain " + captainId + ": [" + signal.Type + "] " + signal.Value);

            string capturedCaptainId = captainId;
            string? capturedMissionId = missionId;
            _ = Task.Run(async () =>
            {
                try
                {
                    string? targetMissionId = capturedMissionId;
                    if (String.IsNullOrEmpty(targetMissionId))
                    {
                        Captain? captain = await _Database.Captains.ReadAsync(capturedCaptainId).ConfigureAwait(false);
                        targetMissionId = captain?.CurrentMissionId;
                    }

                    if (String.IsNullOrEmpty(targetMissionId)) return;

                    if (signal.Type == "status" && signal.MissionStatus.HasValue)
                    {
                        Mission? mission = await _Database.Missions.ReadAsync(targetMissionId).ConfigureAwait(false);
                        if (mission != null && IsValidTransition(mission.Status, signal.MissionStatus.Value))
                        {
                            mission.Status = signal.MissionStatus.Value;
                            mission.LastUpdateUtc = DateTime.UtcNow;
                            await _Database.Missions.UpdateAsync(mission).ConfigureAwait(false);
                            _Logging.Info(_Header + "mission " + mission.Id + " transitioned to " + signal.MissionStatus.Value + " via agent signal");
                        }
                    }

                    Signal dbSignal = new Signal(SignalTypeEnum.Progress, "[" + signal.Type + "] " + signal.Value);
                    dbSignal.FromCaptainId = capturedCaptainId;
                    await _Database.Signals.CreateAsync(dbSignal).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error processing progress signal: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Store one captain-reported papercut as an event. Never throws into the output path: a
        /// malformed complaint must not disturb the mission that reported it.
        /// </summary>
        /// <param name="captainId">Reporting captain identifier.</param>
        /// <param name="missionId">Mission identifier, when the process is mapped to one.</param>
        /// <param name="value">Marker value that followed [ARMADA:PAPERCUT].</param>
        private void HandlePapercutSignal(string captainId, string? missionId, string value)
        {
            Papercut? parsed = PapercutParser.TryParseValue(value);
            if (parsed == null)
            {
                _Logging.Debug(_Header + "unparseable papercut from captain " + captainId);
                return;
            }

            string capturedCaptainId = captainId;
            string? capturedMissionId = missionId;

            _ = Task.Run(async () =>
            {
                try
                {
                    string? targetMissionId = capturedMissionId;
                    if (String.IsNullOrEmpty(targetMissionId))
                    {
                        Captain? owner = await _Database.Captains.ReadAsync(capturedCaptainId).ConfigureAwait(false);
                        targetMissionId = owner?.CurrentMissionId;
                    }

                    Mission? mission = null;
                    if (!String.IsNullOrEmpty(targetMissionId))
                    {
                        mission = await _Database.Missions.ReadAsync(targetMissionId!).ConfigureAwait(false);
                    }

                    // A judge reviews the work under review and already has a verdict channel for what
                    // it finds there. Letting it file papercuts as well splits review feedback across two
                    // surfaces, and the operator reads only one of them.
                    if (mission != null && PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge))
                    {
                        _Logging.Debug(_Header + "ignoring papercut from judge mission " + mission.Id);
                        return;
                    }

                    if (!String.IsNullOrEmpty(targetMissionId))
                    {
                        int stored = _MissionPapercutCounts.AddOrUpdate(targetMissionId!, 1, (_, existing) => existing + 1);
                        if (stored > _MaxPapercutsPerMission)
                        {
                            if (stored == _MaxPapercutsPerMission + 1)
                            {
                                _Logging.Info(_Header + "mission " + targetMissionId +
                                    " reached the papercut cap of " + _MaxPapercutsPerMission + "; later reports are dropped");
                            }

                            return;
                        }
                    }

                    Captain? captain = await _Database.Captains.ReadAsync(capturedCaptainId).ConfigureAwait(false);

                    parsed.CaptainId = capturedCaptainId;
                    parsed.MissionId = targetMissionId;
                    parsed.VesselId = mission?.VesselId;
                    parsed.VoyageId = mission?.VoyageId;
                    parsed.Persona = mission?.Persona;
                    parsed.Runtime = captain?.Runtime.ToString();
                    parsed.ReportedUtc = DateTime.UtcNow;

                    await _Database.Events.CreateAsync(PapercutService.ToEvent(parsed)).ConfigureAwait(false);

                    _Logging.Info(_Header + "papercut from captain " + capturedCaptainId + " [" +
                        parsed.Category + "/" + parsed.Severity + "] " + parsed.Title);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error storing papercut: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Persist an authoritative runtime token-usage sample for dashboard aggregation.
        /// </summary>
        /// <param name="processId">Agent process identifier.</param>
        /// <param name="usage">Usage reported directly by the runtime/provider.</param>
        public void HandleTokenUsage(int processId, RuntimeTokenUsage usage)
        {
            string? captainId = null;
            string? missionId = null;
            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain.TryGetValue(processId, out captainId);
                _ProcessToMission.TryGetValue(processId, out missionId);
            }

            if (String.IsNullOrEmpty(captainId) || String.IsNullOrEmpty(missionId))
            {
                _Logging.Warn(_Header + "discarding token usage for unmapped process " + processId);
                return;
            }

            string capturedCaptainId = captainId;
            string capturedMissionId = missionId;
            _ = Task.Run(async () =>
            {
                try
                {
                    Captain? captain = await _Database.Captains.ReadAsync(capturedCaptainId).ConfigureAwait(false);
                    Mission? mission = await _Database.Missions.ReadAsync(capturedMissionId).ConfigureAwait(false);
                    if (captain == null || mission == null)
                        return;

                    usage.Runtime = captain.Runtime.ToString();
                    if (String.IsNullOrWhiteSpace(usage.Model))
                        usage.Model = String.IsNullOrWhiteSpace(captain.Model) ? "(runtime default)" : captain.Model.Trim();

                    ArmadaEvent evt = new ArmadaEvent(
                        "mission.token_usage",
                        "Authoritative token usage reported by " + usage.Runtime + " for " + usage.Model);
                    evt.TenantId = mission.TenantId;
                    evt.UserId = mission.UserId;
                    evt.EntityType = "mission";
                    evt.EntityId = mission.Id;
                    evt.CaptainId = captain.Id;
                    evt.MissionId = mission.Id;
                    evt.VesselId = mission.VesselId;
                    evt.VoyageId = mission.VoyageId;
                    evt.Payload = JsonSerializer.Serialize(usage);
                    await _Database.Events.CreateAsync(evt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error persisting token usage: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Record authoritative provider-progress into the recovery tracker. This is the signal the
        /// autonomous recovery orchestrator uses to distinguish a provider-silent stall (process
        /// alive, heartbeat fresh, but the underlying provider has stopped making forward motion)
        /// from a captain-wide heartbeat stall. Cheap and synchronous: the tracker is in-memory and
        /// keyed by captain id, so this path must not do DB I/O.
        /// </summary>
        /// <param name="processId">Agent process identifier.</param>
        /// <param name="usage">Usage reported directly by the runtime/provider.</param>
        public void HandleProviderProgress(int processId, RuntimeTokenUsage usage)
        {
            if (_ProviderProgress == null) return;
            if (usage == null) return;

            string? captainId = null;
            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain.TryGetValue(processId, out captainId);
            }

            if (String.IsNullOrEmpty(captainId))
            {
                _Logging.Warn(_Header + "discarding provider progress for unmapped process " + processId);
                return;
            }

            _ProviderProgress.Record(captainId, DateTime.UtcNow);
        }

        /// <summary>
        /// Activity records are durable viewer telemetry, not captain prose. Keeping them out of
        /// AgentOutput prevents tool and lifecycle noise from polluting pipeline handoff prompts.
        /// </summary>
        private static bool IsMissionActivityRecord(string line)
        {
            return !String.IsNullOrEmpty(line) &&
                line.StartsWith("[ARMADA:ACTIVITY]", StringComparison.Ordinal);
        }

        /// <summary>
        /// Handle agent process exit event.
        /// </summary>
        public void HandleAgentProcessExited(int processId, int? exitCode)
        {
            StopProcessLivenessHeartbeat(processId);

            string? captainId = null;
            string? missionId = null;
            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain.TryGetValue(processId, out captainId);
                _ProcessToMission.TryGetValue(processId, out missionId);
            }

            // The process exit event can fire before HandleLaunchAgentAsync finishes
            // registering the PID-to-captain/mission mapping (race between process.Start()
            // returning and the mapping being written). Retry briefly to close this window.
            if (String.IsNullOrEmpty(captainId) || String.IsNullOrEmpty(missionId))
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    Thread.Sleep(100);
                    lock (_ProcessToCaptain)
                    {
                        _ProcessToCaptain.TryGetValue(processId, out captainId);
                        _ProcessToMission.TryGetValue(processId, out missionId);
                    }
                    if (!String.IsNullOrEmpty(captainId) && !String.IsNullOrEmpty(missionId))
                    {
                        _Logging.Info(_Header + "process " + processId + " exit handler resolved mapping after " + (attempt + 1) + " retries");
                        break;
                    }
                }
            }

            if (String.IsNullOrEmpty(captainId) || String.IsNullOrEmpty(missionId))
            {
                _Logging.Warn(_Header + "process " + processId + " exited (code " + (exitCode?.ToString() ?? "unknown") + ") but no captain/mission mapping found after retries -- exit may be lost");
                return;
            }

            _Logging.Info(_Header + "process " + processId + " exited (code " + (exitCode?.ToString() ?? "unknown") + ") for captain " + captainId + " mission " + missionId);

            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain.Remove(processId);
                _ProcessToMission.Remove(processId);
            }
            _MissionHeartbeatWrites.TryRemove(missionId, out _);

            // Track this PID as handled BEFORE the async work begins.
            // The health check consults this set to avoid racing with the async exit handler
            // (e.g. triggering recovery for a process that exited cleanly but whose completion
            // handler hasn't finished yet).
            _HandledProcessExits[processId] = DateTime.UtcNow;

            string capturedCaptainId = captainId;
            string capturedMissionId = missionId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAgentProcessExitedAsync(processId, exitCode, capturedCaptainId, capturedMissionId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error handling process exit for captain " + capturedCaptainId + " mission " + capturedMissionId + ": " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Async handler for agent process exit, delegating to the admiral service.
        /// After admiral processing completes, discards any unclaimed streamed output buffer
        /// so failure/cancel exits do not leak per-mission StringBuilders into long-lived memory.
        /// The successful pipeline-handoff path drains the buffer via OnGetMissionOutput before this point;
        /// final-message artifacts consumed by handoff are removed by GetAndClearMissionOutput, so this
        /// follow-up only cleans up entries that no successful path claimed.
        /// </summary>
        public async Task HandleAgentProcessExitedAsync(int processId, int? exitCode, string captainId, string missionId)
        {
            try
            {
                await _Admiral.HandleProcessExitAsync(processId, exitCode, captainId, missionId).ConfigureAwait(false);
            }
            finally
            {
                DiscardUnclaimedMissionOutput(missionId);
            }
        }

        /// <summary>
        /// Discard any streamed output buffer and final-message artifact registration for the given mission
        /// if the successful handoff path did not already claim them. Safe to call repeatedly.
        /// Used to ensure failure/cancel/lost-output terminal paths do not retain unbounded buffers.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        public void DiscardUnclaimedMissionOutput(string missionId)
        {
            if (String.IsNullOrEmpty(missionId)) return;

            _MissionOutput.TryRemove(missionId, out _);
            _MissionPapercutCounts.TryRemove(missionId, out _);

            if (_MissionFinalMessageFiles.TryRemove(missionId, out string? finalMessageFilePath) &&
                !String.IsNullOrEmpty(finalMessageFilePath))
            {
                try
                {
                    if (File.Exists(finalMessageFilePath))
                        File.Delete(finalMessageFilePath);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error discarding final message artifact for mission " + missionId + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Stop the agent process for the given captain.
        /// </summary>
        public async Task HandleStopAgentAsync(Captain captain)
        {
            if (!captain.ProcessId.HasValue) return;
            _Logging.Info(_Header + "stopping agent process " + captain.ProcessId.Value + " for captain " + captain.Id);
            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain.Remove(captain.ProcessId.Value);
                _ProcessToMission.Remove(captain.ProcessId.Value);
            }
            Armada.Runtimes.Interfaces.IAgentRuntime runtime = _RuntimeFactory.Create(captain.Runtime);
            await runtime.StopAsync(captain.ProcessId.Value).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Records the launch-prompt size the admiral handed to the runtime, as a
        /// mission.launch_prompt_budget event. This is the Armada-measured counterpart to the
        /// runtime-reported token usage captured after the run: OpenCode and Cursor report no token
        /// counts at all, so the measured byte size is the only figure available on every runtime.
        /// Telemetry must never block a launch, so failures are swallowed with a warning.
        /// </summary>
        /// <param name="mission">Mission being launched.</param>
        /// <param name="captain">Captain being launched.</param>
        /// <param name="prompt">Final launch prompt, including any appended commit instructions.</param>
        private async Task RecordLaunchPromptBytesAsync(Mission mission, Captain captain, string prompt)
        {
            if (mission == null) return;

            try
            {
                int promptBytes = System.Text.Encoding.UTF8.GetByteCount(prompt ?? string.Empty);

                ArmadaEvent promptEvent = new ArmadaEvent(
                    "mission.launch_prompt_budget",
                    "Launch prompt: " + promptBytes + " bytes");
                promptEvent.TenantId = mission.TenantId;
                promptEvent.UserId = mission.UserId;
                promptEvent.EntityType = "mission";
                promptEvent.EntityId = mission.Id;
                promptEvent.CaptainId = captain?.Id;
                promptEvent.MissionId = mission.Id;
                promptEvent.VesselId = mission.VesselId;
                promptEvent.VoyageId = mission.VoyageId;
                promptEvent.Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    MissionId = mission.Id,
                    Runtime = captain != null ? captain.Runtime.ToString() : null,
                    LaunchPromptBytes = promptBytes
                });

                await _Database.Events.CreateAsync(promptEvent).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record launch prompt telemetry for " + mission.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Append a line to a per-mission output buffer with a tail-retention cap.
        /// When the buffer would exceed the configured cap, drop the older half and insert
        /// a single truncation marker so consumers can tell the streamed transcript was clipped.
        /// StringBuilder is not thread-safe; the caller-provided instance is locked here.
        /// </summary>
        /// <param name="sb">Per-mission StringBuilder.</param>
        /// <param name="line">Output line to append (will be followed by a newline).</param>
        private static void AppendBounded(System.Text.StringBuilder sb, string line)
        {
            if (sb == null) return;
            string lineText = line ?? string.Empty;

            lock (sb)
            {
                sb.Append(lineText);
                sb.Append(Environment.NewLine);

                if (sb.Length <= _MissionOutputCapChars) return;

                int markerLength = _MissionOutputTruncationMarker.Length + Environment.NewLine.Length;
                int retainChars = Math.Max(0, _MissionOutputCapChars - markerLength);
                string tail = retainChars > 0 && sb.Length > retainChars
                    ? sb.ToString(sb.Length - retainChars, retainChars)
                    : string.Empty;

                sb.Clear();
                sb.Append(_MissionOutputTruncationMarker);
                sb.Append(Environment.NewLine);
                if (!String.IsNullOrEmpty(tail))
                {
                    sb.Append(tail);
                }
            }
        }

        private static bool IsValidTransition(MissionStatusEnum current, MissionStatusEnum target)
        {
            return (current, target) switch
            {
                (MissionStatusEnum.Pending, MissionStatusEnum.Assigned) => true,
                (MissionStatusEnum.Pending, MissionStatusEnum.Cancelled) => true,
                (MissionStatusEnum.Assigned, MissionStatusEnum.InProgress) => true,
                (MissionStatusEnum.Assigned, MissionStatusEnum.Cancelled) => true,
                (MissionStatusEnum.InProgress, MissionStatusEnum.WorkProduced) => true,
                (MissionStatusEnum.InProgress, MissionStatusEnum.Testing) => true,
                (MissionStatusEnum.InProgress, MissionStatusEnum.Review) => true,
                (MissionStatusEnum.InProgress, MissionStatusEnum.Complete) => true,
                (MissionStatusEnum.InProgress, MissionStatusEnum.Failed) => true,
                (MissionStatusEnum.InProgress, MissionStatusEnum.Cancelled) => true,
                (MissionStatusEnum.InProgress, MissionStatusEnum.WaitingForInput) => true,
                (MissionStatusEnum.WaitingForInput, MissionStatusEnum.Pending) => true,
                (MissionStatusEnum.WaitingForInput, MissionStatusEnum.Failed) => true,
                (MissionStatusEnum.WaitingForInput, MissionStatusEnum.Cancelled) => true,
                (MissionStatusEnum.WorkProduced, MissionStatusEnum.PullRequestOpen) => true,
                (MissionStatusEnum.WorkProduced, MissionStatusEnum.Complete) => true,
                (MissionStatusEnum.WorkProduced, MissionStatusEnum.LandingFailed) => true,
                (MissionStatusEnum.WorkProduced, MissionStatusEnum.Cancelled) => true,
                (MissionStatusEnum.PullRequestOpen, MissionStatusEnum.Complete) => true,
                (MissionStatusEnum.PullRequestOpen, MissionStatusEnum.LandingFailed) => true,
                (MissionStatusEnum.PullRequestOpen, MissionStatusEnum.Cancelled) => true,
                (MissionStatusEnum.Testing, MissionStatusEnum.Review) => true,
                (MissionStatusEnum.Testing, MissionStatusEnum.InProgress) => true,
                (MissionStatusEnum.Testing, MissionStatusEnum.Complete) => true,
                (MissionStatusEnum.Testing, MissionStatusEnum.Failed) => true,
                (MissionStatusEnum.Review, MissionStatusEnum.Complete) => true,
                (MissionStatusEnum.Review, MissionStatusEnum.InProgress) => true,
                (MissionStatusEnum.Review, MissionStatusEnum.Failed) => true,
                (MissionStatusEnum.LandingFailed, MissionStatusEnum.WorkProduced) => true,
                (MissionStatusEnum.LandingFailed, MissionStatusEnum.Failed) => true,
                (MissionStatusEnum.LandingFailed, MissionStatusEnum.Cancelled) => true,
                _ => false
            };
        }

        private async Task<string?> ValidateMuxCaptainAsync(Captain captain, CancellationToken token)
        {
            MuxCaptainOptions? options;
            try
            {
                options = CaptainRuntimeOptions.GetMuxOptions(captain);
            }
            catch (Exception ex)
            {
                return "Mux runtime options are invalid JSON: " + ex.Message;
            }

            if (options == null)
            {
                options = new MuxCaptainOptions();
            }

            try
            {
                MuxProbeResult probe = await _MuxCli.ProbeAsync(captain, token).ConfigureAwait(false);
                if (probe.ContractVersion != 1)
                {
                    return "Mux returned structured output contract version " + probe.ContractVersion +
                        ", but Armada currently supports version 1.";
                }

                if (!probe.Success)
                {
                    string error = !String.IsNullOrWhiteSpace(probe.ErrorMessage)
                        ? probe.ErrorMessage
                        : "Mux probe failed with error code " + (String.IsNullOrWhiteSpace(probe.ErrorCode) ? "unknown" : probe.ErrorCode) + ".";
                    return "Mux CLI failed validation: " + error;
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return "Mux CLI failed validation: " + ex.Message;
            }
        }

        private static string? ExtractModelValidationError(string output)
        {
            if (String.IsNullOrWhiteSpace(output))
                return null;

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (String.IsNullOrEmpty(line))
                    continue;

                if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return line;
                }
            }

            return lines[lines.Length - 1].Trim();
        }

        private static async Task InitializeValidationWorkspaceAsync(AgentRuntimeEnum runtimeType, string workingDirectory, CancellationToken token)
        {
            if (runtimeType != AgentRuntimeEnum.Codex)
                return;

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("init");
            startInfo.ArgumentList.Add("--quiet");

            using (Process process = new Process { StartInfo = startInfo })
            {
                if (!process.Start())
                    throw new InvalidOperationException("Failed to initialize temporary validation repository.");

                await process.WaitForExitAsync(token).ConfigureAwait(false);
                if (process.ExitCode == 0)
                    return;

                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string details = !String.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                if (String.IsNullOrWhiteSpace(details))
                    details = "git init exited with code " + process.ExitCode + ".";

                throw new InvalidOperationException("Failed to initialize temporary validation repository: " + details);
            }
        }

        #endregion
    }
}
