namespace Armada.Server
{
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using Armada.Server.WebSocket;

    /// <summary>
    /// Handles agent process lifecycle: launching, heartbeats, output parsing, process exit, and stopping.
    /// </summary>
    public class AgentLifecycleHandler
    {
        #region Private-Members

        private string _Header = "[AgentLifecycle] ";
        private TimeSpan? _ProcessLivenessInterval = null;
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
        private TerminalMarkerTracker? _TerminalMarkers;
        private IHarborProcessHost? _HarborHost;

        // Mints the mission owner's scoped session token for a mission captain's MCP credential. Null in a
        // context that issues no tokens (a test host), which fails the mission launch credential closed:
        // the captain presents no token and reaches no MCP tool, never the admiral launch credential.
        private readonly Armada.Core.Services.Interfaces.ISessionTokenService? _SessionTokens;

        // Processes stopped on purpose. A process this handler stops because it outlived its terminal
        // marker is registered as a completion; the admiral registers the processes it supersedes.
        private IntentionalProcessStops _IntentionalStops = new IntentionalProcessStops();

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
        /// Process IDs whose exit handling is still running. An entry is added when the exit
        /// callback fires and removed only when the completion or failure handling finishes.
        /// It is never pruned by age: a completion that takes longer than any timer (a gate, a
        /// branch advance, a dock that provisions siblings) must still hold the marker, or the
        /// health check reads a finished process as a crash while its completion is being written.
        /// </summary>
        private System.Collections.Concurrent.ConcurrentDictionary<int, byte> _InFlightProcessExits = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        /// <summary>
        /// Process IDs whose exit handling has completed, with the completion time. Kept for
        /// <see cref="HandledExitRetention"/> so a health check that runs right after completion
        /// still recognises the PID, then pruned to bound memory and to survive PID reuse.
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
        /// <param name="modelValidationTimeout">Optional validation timeout override.</param>
        /// <param name="sessionTokens">Session token service that mints a mission captain's own scoped MCP
        /// credential; null issues no token, failing the mission launch credential closed.</param>
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
            TimeSpan? modelValidationTimeout = null,
            Armada.Core.Services.Interfaces.ISessionTokenService? sessionTokens = null)
        {
            _SessionTokens = sessionTokens;
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

        #region Public-Members

        /// <summary>
        /// How long a COMPLETED exit stays recognisable to the health check. An exit whose handling
        /// is still in flight is recognised regardless of this value.
        /// </summary>
        public TimeSpan HandledExitRetention { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Delay between process-liveness refreshes for a tracked process. Null, the default, uses
        /// <see cref="ArmadaSettings.HeartbeatIntervalSeconds"/> with a five-second floor; a host that needs faster
        /// ticks sets a shorter positive interval before the process is tracked.
        /// </summary>
        public TimeSpan? ProcessLivenessInterval
        {
            get => _ProcessLivenessInterval;
            set
            {
                if (value.HasValue && value.Value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(ProcessLivenessInterval), "Must be positive.");
                _ProcessLivenessInterval = value;
            }
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
        /// Wire the shared terminal-marker tracker. The handler records each mission's first
        /// terminal marker into it and completes the stage once the grace period has passed.
        /// </summary>
        /// <param name="tracker">Shared tracker, also read by the recovery orchestrator.</param>
        public void SetTerminalMarkers(TerminalMarkerTracker tracker)
        {
            _TerminalMarkers = tracker ?? throw new ArgumentNullException(nameof(tracker));
        }

        /// <summary>
        /// Wire the shared intentional-stop registry, so the processes the admiral supersedes and the
        /// processes this handler stops after their terminal marker are recorded in one place.
        /// </summary>
        /// <param name="stops">Shared registry, also used by the admiral.</param>
        public void SetIntentionalStops(IntentionalProcessStops stops)
        {
            _IntentionalStops = stops ?? throw new ArgumentNullException(nameof(stops));
        }

        /// <summary>
        /// Wire the Harbor process host. It is set only when Harbor is enabled and its link is registered; a
        /// mission routed to a runner while no host is set is refused, never launched locally.
        /// </summary>
        /// <param name="host">Harbor process host, or null.</param>
        public void SetHarborHost(IHarborProcessHost? host)
        {
            _HarborHost = host;
        }

        /// <summary>
        /// Stop a process that has outlived its terminal marker by the configured grace period.
        /// Called on every process-liveness tick. The stop is owned here: the process is marked
        /// before it is stopped, so its exit completes the stage from the recorded output instead
        /// of reading as a crash.
        /// </summary>
        /// <param name="processId">Tracked process identifier.</param>
        /// <param name="captainId">Captain that owns the process.</param>
        /// <param name="missionId">Mission the process is running.</param>
        /// <param name="nowUtc">Current UTC time.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the process was stopped.</returns>
        public async Task<bool> EnforceTerminalMarkerGraceAsync(int processId, string captainId, string missionId, DateTime nowUtc, CancellationToken token = default)
        {
            if (_TerminalMarkers == null) return false;
            if (!_TerminalMarkers.TryGet(missionId, out TerminalMarkerRecord? marker) || marker == null) return false;

            double graceSeconds = _Settings.AutonomousRecovery.TerminalMarkerGraceSeconds;
            if ((nowUtc - marker.FirstSeenUtc).TotalSeconds < graceSeconds) return false;
            if (!_IntentionalStops.TryRegister(processId, captainId, missionId, IntentionalStopKindEnum.Completion)) return false;

            try
            {
                Captain? captain = await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
                if (captain == null)
                {
                    _IntentionalStops.Forget(processId);
                    _Logging.Warn(_Header + "cannot stop process " + processId + " after terminal marker: captain " + captainId + " not found");
                    return false;
                }

                string detail = "process " + processId + " kept running " + Math.Round((nowUtc - marker.FirstSeenUtc).TotalSeconds)
                    + "s after its first terminal marker [" + marker.MarkerType + "] " + marker.Value
                    + " (grace " + graceSeconds + "s); stopping it and completing mission " + missionId + " from the recorded output";
                _Logging.Info(_Header + detail);
                await _EmitEventAsync("captain.terminal_marker_stop", "Captain " + captainId + " " + detail,
                    "mission", missionId, captainId, missionId, null, null).ConfigureAwait(false);

                Armada.Runtimes.Interfaces.IAgentRuntime runtime = _RuntimeFactory.Create(captain.Runtime);
                await runtime.StopAsync(processId, token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _IntentionalStops.Forget(processId);
                throw;
            }
            catch (Exception ex)
            {
                _IntentionalStops.Forget(processId);
                _Logging.Warn(_Header + "failed to stop process " + processId + " after its terminal marker for mission " + missionId + ": " + ex.Message);
                return false;
            }
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
                        try { File.Delete(finalMessageFilePath); }
                        catch (Exception deleteEx)
                        {
                            _Logging.Warn(_Header + "read the final message of mission " + missionId + " but could not delete " + finalMessageFilePath
                                + "; a later attempt clears it before launch: " + deleteEx.Message);
                        }
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
            if (_InFlightProcessExits.ContainsKey(processId)) return true;

            DateTime cutoff = DateTime.UtcNow - HandledExitRetention;
            foreach (System.Collections.Generic.KeyValuePair<int, DateTime> kvp in _HandledProcessExits)
            {
                if (kvp.Value < cutoff)
                    _HandledProcessExits.TryRemove(kvp.Key, out _);
            }

            return _HandledProcessExits.ContainsKey(processId);
        }

        /// <summary>
        /// Prove whether a mission still owns a live captain process. The persisted mission and
        /// captain bindings, the registered process mapping, and the runtime liveness probe must
        /// all agree. The registered mapping is authoritative when a persisted PID is missing or
        /// stale during launch recovery. A PID by itself is not sufficient because the operating
        /// system can reuse it.
        /// </summary>
        /// <param name="mission">Mission whose process ownership is checked.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True only for a live process still registered for this mission.</returns>
        public async Task<bool> IsMissionProcessActiveAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null || String.IsNullOrWhiteSpace(mission.CaptainId))
            {
                return false;
            }

            Captain? captain = await _Database.Captains.ReadAsync(mission.CaptainId, token).ConfigureAwait(false);
            if (captain == null
                || !String.Equals(captain.CurrentMissionId, mission.Id, StringComparison.Ordinal))
            {
                return false;
            }

            List<int> processIds = new List<int>();
            lock (_ProcessToCaptain)
            {
                foreach (System.Collections.Generic.KeyValuePair<int, string> mapping in _ProcessToMission)
                {
                    if (mapping.Key <= 0 || !String.Equals(mapping.Value, mission.Id, StringComparison.Ordinal)) continue;
                    if (_ProcessToCaptain.TryGetValue(mapping.Key, out string? mappedCaptainId)
                        && String.Equals(mappedCaptainId, captain.Id, StringComparison.Ordinal))
                    {
                        processIds.Add(mapping.Key);
                    }
                }
            }
            if (processIds.Count == 0) return false;

            if (captain.Runtime == AgentRuntimeEnum.Custom)
                throw new InvalidOperationException("manual_completion_process_liveness_unknown");

            // An API-endpoint captain runs in-process under a synthetic identifier. Its liveness is the
            // registration its loop holds until exit; the runtime cannot be recreated without its endpoint.
            if (captain.Runtime == AgentRuntimeEnum.ApiEndpoint)
            {
                foreach (int processId in processIds)
                {
                    if (IsProcessExitHandled(processId)) continue;
                    if (ProcessSupervisor.IsSyntheticProcessAlive(processId)) return true;
                }
                return false;
            }

            IAgentRuntime runtime;
            try
            {
                runtime = _RuntimeFactory.Create(captain.Runtime);
                foreach (int processId in processIds)
                {
                    if (IsProcessExitHandled(processId)) continue;
                    if (await runtime.IsRunningAsync(processId, token).ConfigureAwait(false)) return true;
                }
                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("manual_completion_process_liveness_unknown", ex);
            }
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
            if (captain.Runtime == AgentRuntimeEnum.ApiEndpoint)
                return ValidateApiEndpointCaptainAsync(captain, token);
            if (captain.Runtime == AgentRuntimeEnum.Mux)
            {
                return ValidateMuxCaptainBypassingQuotaAsync(captain, token);
            }

            return ValidateModelBypassingQuotaAsync(captain.Runtime, captain.Model, captain, token);
        }

        /// <summary>
        /// Validate an API-endpoint captain against the captain tenant. The endpoint must be enabled and
        /// provide inference capabilities before the captain can be admitted for launch.
        /// </summary>
        /// <param name="captain">Captain to validate.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Null when valid, otherwise a safe validation message.</returns>
        private async Task<string?> ValidateApiEndpointCaptainAsync(Captain captain, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(captain.ModelEndpointId))
                return "An API-endpoint captain must reference an inference model endpoint.";
            if (String.IsNullOrWhiteSpace(captain.TenantId))
                return "An API-endpoint captain must belong to a tenant.";
            if (!String.IsNullOrWhiteSpace(captain.ApiKey) || !String.IsNullOrWhiteSpace(captain.ApiBaseUrl))
                return "API-endpoint captain credentials must be configured on the model endpoint.";

            ModelEndpoint? endpoint = await _Database.ModelEndpoints.ReadAsync(captain.TenantId!, captain.ModelEndpointId!, token).ConfigureAwait(false);
            return ValidateApiEndpointAdmission(captain, endpoint, _Settings.ApiCaptainCloudProviders);
        }

        /// <summary>
        /// Whether a provider is a hosted cloud service that needs explicit operator opt-in for API captains.
        /// Ollama and OpenAI-compatible endpoints are operator-hosted and need no opt-in.
        /// </summary>
        /// <param name="provider">Endpoint provider.</param>
        /// <returns>True for hosted cloud inference providers.</returns>
        public static bool IsHostedCloudProvider(ModelProviderEnum provider)
        {
            return provider == ModelProviderEnum.OpenAI
                || provider == ModelProviderEnum.Anthropic
                || provider == ModelProviderEnum.Gemini;
        }

        /// <summary>
        /// The one admission rule for an API-endpoint captain and the endpoint snapshot it will run against.
        /// Every entry point that creates an API runtime (mission launch, Ask chat) must call this with the
        /// same snapshot it then passes to the runtime factory.
        /// </summary>
        /// <param name="captain">Captain to admit.</param>
        /// <param name="endpoint">Endpoint snapshot read for the captain tenant and endpoint identifier.</param>
        /// <param name="enabledCloudProviders">Hosted cloud providers the operator explicitly enabled for API captains.</param>
        /// <returns>Null when admitted, otherwise a safe admission error.</returns>
        public static string? ValidateApiEndpointAdmission(Captain captain, ModelEndpoint? endpoint, IReadOnlyCollection<ModelProviderEnum>? enabledCloudProviders)
        {
            if (captain == null)
                return "The captain is required.";
            if (captain.Runtime != AgentRuntimeEnum.ApiEndpoint)
                return "The captain runtime is not an API endpoint.";
            if (String.IsNullOrWhiteSpace(captain.ModelEndpointId) || String.IsNullOrWhiteSpace(captain.TenantId))
                return "An API-endpoint captain must reference an authorized tenant-owned model endpoint.";
            if (!String.IsNullOrWhiteSpace(captain.ApiKey) || !String.IsNullOrWhiteSpace(captain.ApiBaseUrl))
                return "API-endpoint captain credentials must be configured on the model endpoint.";
            if (endpoint == null
                || !String.Equals(endpoint.Id, captain.ModelEndpointId, StringComparison.Ordinal)
                || !String.Equals(endpoint.TenantId, captain.TenantId, StringComparison.Ordinal))
                return "The referenced model endpoint is not available to this captain.";
            if (IsHostedCloudProvider(endpoint.Provider)
                && (enabledCloudProviders == null || !enabledCloudProviders.Contains(endpoint.Provider)))
                return "The " + endpoint.Provider + " provider is disabled for API captains. List it in apiCaptainCloudProviders to enable it.";
            if (endpoint.Scope == ScopeEnum.UserSpecific
                && !String.Equals(endpoint.UserId, captain.UserId, StringComparison.Ordinal))
                return "The referenced model endpoint is not available to this captain.";
            if (endpoint.Kind != ModelEndpointKindEnum.Inference)
                return "An API-endpoint captain requires an inference model endpoint.";
            if (!endpoint.Enabled)
                return "The referenced model endpoint is disabled.";
            if (!String.IsNullOrWhiteSpace(captain.Model)
                && !String.Equals(captain.Model, endpoint.Model, StringComparison.Ordinal))
                return "The captain model must match the configured model endpoint model.";
            return null;
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
                try { Directory.Delete(validationDirectory, true); }
                catch (Exception deleteEx) { _Logging.Warn(_Header + "could not delete model-validation directory " + validationDirectory + ": " + deleteEx.Message); }
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
                    catch (Exception stopEx)
                    {
                        _Logging.Warn(_Header + "could not stop model-validation process " + processId.Value + " for " + runtimeType + "; it may still be running: " + stopEx.Message);
                    }
                }

                try { Directory.Delete(validationDirectory, true); }
                catch (Exception deleteEx) { _Logging.Warn(_Header + "could not delete model-validation directory " + validationDirectory + ": " + deleteEx.Message); }
            }
        }

        /// <summary>
        /// Launch an agent process for the given captain, mission, and dock.
        /// </summary>
        public async Task<int> HandleLaunchAgentAsync(Captain captain, Mission mission, Dock dock)
        {
            _Logging.Info(_Header + "launching " + captain.Runtime + " agent for captain " + captain.Id);

            string? capabilityError = ValidateRuntimeSupportsPersona(captain, mission.Persona);
            if (capabilityError != null)
            {
                _Logging.Warn(_Header + "refusing to launch captain " + captain.Id + " for mission " + mission.Id + ": " + capabilityError);
                throw new InvalidOperationException(capabilityError);
            }

            Armada.Core.Settings.HarborMissionRoute? harborRoute = HarborMissionRouting.Resolve(_Settings.Harbor, captain, mission);
            BaseAgentRuntime? harborAdapter = harborRoute != null ? CreateHarborAdapter(captain) : null;
            Armada.Runtimes.Interfaces.IAgentRuntime runtime = harborAdapter ?? await CreateRuntimeAsync(captain).ConfigureAwait(false);
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

            // Commit trailers are dead weight on a read-only mission: the rules block forbids
            // commits, so the trailer block contradicts the brief and costs tokens (probe
            // papercut, 2026-08-09). Skip it for Audit and Research missions.
            if (_Settings.MessageTemplates.EnableCommitMetadata && !mission.IsReadOnlyMode)
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
            catch (Exception deleteEx)
            {
                _Logging.Warn(_Header + "could not delete stale final-message file " + finalMessageFilePath
                    + " for mission " + mission.Id + "; the previous attempt's final message may be read: " + deleteEx.Message);
            }
            _MissionFinalMessageFiles[mission.Id] = finalMessageFilePath;
            string captainLogDir = Path.Combine(_Settings.LogDirectory, "captains");
            Directory.CreateDirectory(captainLogDir);
            string captainLogPointer = Path.Combine(captainLogDir, captain.Id + ".current");
            File.WriteAllText(captainLogPointer, logFilePath);

            int processId;
            try
            {
                if (harborRoute != null && harborAdapter != null)
                {
                    processId = await StartOnHarborAsync(harborAdapter, harborRoute, captain, mission, dock, prompt, logFilePath, launchKey).ConfigureAwait(false);
                }
                else
                {
                    CaptainLaunchIsolationPlan? launchIsolation = await PrepareCaptainLaunchIsolationAsync(
                        captain,
                        mission).ConfigureAwait(false);

                    processId = await runtime.StartAsync(
                        dock.WorktreePath ?? throw new InvalidOperationException("Dock worktree path is null"),
                        prompt,
                        logFilePath: logFilePath,
                        finalMessageFilePath: finalMessageFilePath,
                        model: captain.Model,
                        captain: captain,
                        isolationPlan: launchIsolation).ConfigureAwait(false);
                }
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
                _WebSocketHub.BroadcastCaptainChange(captain);
                _WebSocketHub.BroadcastMissionChange(mission);
            }

            return processId;
        }

        /// <summary>
        /// Run a routed mission on its Harbor runner. A routed launch runs on that runner or not at all: every
        /// refusal names its reason and nothing falls back to a local process. The dock, branch and landing stay
        /// on the Admiral; the runner receives only the launch plan.
        /// </summary>
        private async Task<int> StartOnHarborAsync(
            BaseAgentRuntime processRuntime,
            Armada.Core.Settings.HarborMissionRoute route,
            Captain captain,
            Mission mission,
            Dock dock,
            string prompt,
            string logFilePath,
            string launchKey)
        {
            if (_HarborHost == null) throw new HarborLaunchException("harbor_mission_execution_unavailable");
            if (String.IsNullOrWhiteSpace(mission.TenantId) || String.IsNullOrWhiteSpace(mission.UserId))
                throw new HarborLaunchException("harbor_mission_owner_missing");

            // A mission is worked only by a captain of its own tenant; routing to a runner does not widen that rule.
            if (!String.Equals(
                    Armada.Core.Authorization.OwnershipPolicy.TenantOfRecord(captain.TenantId),
                    Armada.Core.Authorization.OwnershipPolicy.TenantOfRecord(mission.TenantId),
                    StringComparison.Ordinal))
                throw new HarborLaunchException("harbor_captain_tenant_mismatch");

            // An account login is a local home directory or key on the Admiral; it cannot travel to a runner.
            UsageAccountSettings? account = CaptainAccountLaunch.FindAccount(_Settings.ModelTier.UsageRouting, captain.Id);
            CaptainAccountLaunch.RequireLaunchIdentity(captain, account, _Settings.ModelTier.UsageRouting.RequireAccountLogin);
            if (CaptainAccountLaunch.HasLaunchIdentity(account))
                throw new HarborLaunchException("harbor_account_login_unsupported", account!.Id);

            if (!HarborMissionRouting.TryMapWorkingDirectory(route, dock.WorktreePath, out string runnerDirectory, out string mappingReason))
                throw new HarborLaunchException(mappingReason);

            HarborProcessLaunch launch = new HarborProcessLaunch
            {
                RunnerId = route.RunnerId.Trim(),
                LaunchKey = launchKey,
                MissionId = mission.Id,
                CaptainId = captain.Id,
                OwnerTenantId = mission.TenantId!,
                OwnerUserId = mission.UserId!,
                WorkingDirectory = runnerDirectory
            };
            _Logging.Info(_Header + "mission " + mission.Id + " is routed to Harbor runner " + launch.RunnerId);
            int processId = await processRuntime.StartOnHarborAsync(_HarborHost, launch, prompt, logFilePath, captain.Model, captain).ConfigureAwait(false);
            await _EmitEventAsync("captain.harbor_launched", "Mission " + mission.Id + " runs on Harbor runner " + launch.RunnerId,
                "captain", captain.Id, captain.Id, mission.Id, mission.VesselId, mission.VoyageId).ConfigureAwait(false);
            return processId;
        }

        /// <summary>
        /// The built-in adapter a routed launch builds its plan with. A factory that replaces local launches, such as
        /// a test host's non-launching factory, still yields the real plan, because this path never starts a local
        /// process. A runtime with no CLI launch plan is refused by name.
        /// </summary>
        private BaseAgentRuntime CreateHarborAdapter(Captain captain)
        {
            try
            {
                return _RuntimeFactory.CreateLaunchPlanAdapter(captain.Runtime);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentOutOfRangeException)
            {
                throw new HarborLaunchException("harbor_runtime_unsupported", captain.Runtime.ToString());
            }
        }

        internal async Task<CaptainLaunchIsolationPlan?> PrepareCaptainLaunchIsolationAsync(
            Captain captain,
            Mission mission,
            CancellationToken token = default)
        {
            string scopedDirectory = CaptainLaunchIsolationPlanner.MissionScopedDirectory(_Settings.LogDirectory, mission.Id, captain.Id);
            // A mission captain authenticates to Armada MCP with the mission owner's own scoped session
            // token, which the endpoint scopes to that owner's tenant and user. The admiral launch
            // credential, which maps to global admin, never enters a mission captain's environment, so a
            // mission (even one a tenant admin dispatched, or an autonomous mission) reaches only its owner's
            // records and no operator-only tool.
            McpCredentialReference credential = await ResolveMissionMcpCredentialAsync(mission, token).ConfigureAwait(false);
            CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.PlanForLaunch(
                captain.Runtime,
                _Settings.SeedDockRuntimeMcpConfig,
                _Settings.McpPort,
                scopedDirectory,
                credential);

            // The account login switch applies whether or not MCP isolation is seeded. A captain on an account whose
            // login is missing fails this launch with a named reason instead of running on the shared login.
            UsageAccountSettings? account = CaptainAccountLaunch.FindAccount(_Settings.ModelTier.UsageRouting, captain.Id);
            CaptainLaunchIsolationPlanner.ApplyAccount(plan, captain, account, requireAccountLogin: _Settings.ModelTier.UsageRouting.RequireAccountLogin);
            if (account != null)
            {
                // A login the runtime last reported as rejected refuses the launch too; this reads the cached probe only.
                string? loginProblem = UsageRoutingService.For(_Settings).GetLoginProblem(account, DateTime.UtcNow);
                if (loginProblem != null) throw new CaptainAccountLaunchException(loginProblem, account.Id);
            }
            if (plan.IsEmpty) return null;

            Directory.CreateDirectory(scopedDirectory);
            foreach (IsolationConfigFile file in plan.FilesToWrite)
            {
                string? destination = PathContainment.TryResolve(scopedDirectory, file.RelativePath);
                if (destination == null)
                    throw new InvalidOperationException("Captain runtime configuration escaped its scoped directory.");

                string? parent = Path.GetDirectoryName(destination);
                if (!String.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(destination, file.Contents).ConfigureAwait(false);
            }

            return plan;
        }

        /// <summary>
        /// Build the MCP credential a mission captain launches with: the mission owner's own scoped session
        /// token. The owner is the mission's tenant and user, and when the mission carries neither (older
        /// records, or a mission created without them), the objective owner carried on the mission's voyage.
        /// The token the endpoint scopes to that owner, exactly as an authenticated caller of that scope would
        /// receive, so the mission reaches only that tenant and user's records and no operator-only tool.
        /// When no owner resolves, or no session-token service is available, the credential carries no value
        /// (fail closed): the launch presents nothing and the endpoint refuses it, never the admiral launch
        /// credential.
        /// </summary>
        /// <param name="mission">The mission being launched.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The mission's scoped MCP credential; never null.</returns>
        internal async Task<McpCredentialReference> ResolveMissionMcpCredentialAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (_SessionTokens == null) return McpCredentialReference.MissionUnresolvedOwner;

            string? tenantId = mission.TenantId;
            string? userId = mission.UserId;

            // Autonomous dispatch has no interactive caller: the owner is the objective owner, which the
            // mission carries directly and, failing that, its voyage carries.
            if ((String.IsNullOrWhiteSpace(tenantId) || String.IsNullOrWhiteSpace(userId))
                && !String.IsNullOrWhiteSpace(mission.VoyageId))
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(mission.VoyageId!, token).ConfigureAwait(false);
                if (voyage != null)
                {
                    if (String.IsNullOrWhiteSpace(tenantId)) tenantId = voyage.TenantId;
                    if (String.IsNullOrWhiteSpace(userId)) userId = voyage.UserId;
                }
            }

            if (String.IsNullOrWhiteSpace(tenantId) || String.IsNullOrWhiteSpace(userId))
            {
                _Logging.Warn(_Header + "mission " + mission.Id + " has no resolvable owner; its captain launches without an Armada MCP credential");
                return McpCredentialReference.MissionUnresolvedOwner;
            }

            AuthenticateResult issued = _SessionTokens.CreateToken(tenantId!, userId!);
            if (String.IsNullOrWhiteSpace(issued.Token))
            {
                _Logging.Warn(_Header + "no session token was issued for mission " + mission.Id + "; its captain launches without an Armada MCP credential");
                return McpCredentialReference.MissionUnresolvedOwner;
            }

            return McpCredentialReference.ForMission(issued.Token!);
        }

        /// <summary>
        /// Start a periodic heartbeat loop for a tracked process so telemetry stays fresh
        /// even when the runtime is busy but not emitting output.
        /// </summary>
        private void StartProcessLivenessHeartbeat(int processId, string captainId, string missionId)
        {
            CancellationTokenSource cts = new CancellationTokenSource();

            // Read the token BEFORE the source is published. Once it is in the map, the process exit
            // handler (StopProcessLivenessHeartbeat, on its own thread) may remove and dispose it at any
            // instant, and CancellationTokenSource.Token throws ObjectDisposedException after disposal.
            // A CancellationToken captured before disposal stays usable, so nothing below touches the
            // source again except the owner that removes it from the map.
            CancellationToken token = cts.Token;
            if (!_ProcessHeartbeatLoops.TryAdd(processId, cts))
            {
                cts.Dispose();
                return;
            }

            TimeSpan interval = _ProcessLivenessInterval ?? TimeSpan.FromSeconds(Math.Max(5, _Settings.HeartbeatIntervalSeconds));

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

                        // Refresh process-liveness telemetry ONLY. The output heartbeat
                        // (LastHeartbeatUtc, advanced by HandleAgentHeartbeat on real agent output)
                        // must NOT be touched here: stall detection measures time since last output,
                        // so refreshing the heartbeat for a merely-alive process would mask a stalled
                        // agent that is running but producing nothing. This loop previously called
                        // UpdateHeartbeatAsync on the same interval as the stall threshold is
                        // measured against, which is why no captain was ever detected as stalled.
                        try { await _Database.Captains.UpdateProcessAliveAsync(captainId).ConfigureAwait(false); }
                        catch (Exception aliveEx)
                        {
                            _Logging.Warn(_Header + "could not record process liveness for captain " + captainId + ": " + aliveEx.Message);
                        }

                        // The mission-side call is kept: it advances the mission's last_update_utc and
                        // touches the voyage, which the WorkProduced and assignment-age watchdogs read.
                        // It does not feed stall detection, so it is not part of the masking above.
                        try { await _Database.Missions.UpdateHeartbeatAsync(missionId).ConfigureAwait(false); }
                        catch (Exception heartbeatEx)
                        {
                            _Logging.Warn(_Header + "could not record mission heartbeat for " + missionId
                                + "; WorkProduced and assignment-age watchdogs read a stale time: " + heartbeatEx.Message);
                        }

                        if (await EnforceTerminalMarkerGraceAsync(processId, captainId, missionId, DateTime.UtcNow, token).ConfigureAwait(false))
                            break;
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
                    // Remove only this loop's own source: a later launch that reused the process id owns
                    // any newer entry.
                    if (_ProcessHeartbeatLoops.TryRemove(new KeyValuePair<int, CancellationTokenSource>(processId, cts)))
                    {
                        cts.Dispose();
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
                catch (Exception cancelEx)
                {
                    _Logging.Warn(_Header + "could not cancel the liveness heartbeat loop for process " + processId + "; it stops on its next mapping check: " + cancelEx.Message);
                }
                cts.Dispose();
            }
        }

        /// <summary>
        /// Determine whether the tracked wrapper process is still alive.
        /// </summary>
        private static bool IsTrackedProcessAlive(int processId)
        {
            return ProcessSupervisor.IsTrackedProcessAlive(processId);
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
                catch (Exception captainEx)
                {
                    _Logging.Warn(_Header + "could not record the output heartbeat for captain " + captainId
                        + "; stall detection reads a stale time: " + captainEx.Message);
                }

                if (!persistMissionHeartbeat || String.IsNullOrEmpty(missionId)) return;

                try
                {
                    await _Database.Missions.UpdateHeartbeatAsync(missionId).ConfigureAwait(false);
                }
                catch (Exception missionEx)
                {
                    // Forget the throttle stamp so the next output line retries the write.
                    _MissionHeartbeatWrites.TryRemove(missionId, out _);
                    _Logging.Warn(_Header + "could not record the output heartbeat for mission " + missionId + "; the next output retries: " + missionEx.Message);
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

            List<ProgressParser.ProgressSignal> signals = ProgressParser.ParseAll(line);
            if (signals.Count == 0) return;

            string? captainId = null;
            string? missionId = null;
            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain.TryGetValue(processId, out captainId);
                _ProcessToMission.TryGetValue(processId, out missionId);
            }

            if (String.IsNullOrEmpty(captainId)) return;

            // A parsed progress or [ARMADA:ACTIVITY] tool signal is the provider making progress: it
            // chose to narrate a step or call a tool. Record it as provider progress so a captain
            // that is actively working through a long tool run (a foreground test suite, a large
            // source read) while quiet on token-usage narration is not misclassified as a
            // provider_silent_stall and nudged mid-work. Runtime token-usage updates
            // (OnProviderProgressReceived) go silent during a tool call, so they alone cannot
            // show that a captain is still working.
            _ProviderProgress?.Record(captainId, DateTime.UtcNow);

            // One record can carry several markers (a message or papercut followed by the result).
            // Each is routed on its own so none hides another, in the order the captain wrote them.
            foreach (ProgressParser.ProgressSignal signal in signals)
            {
                RouteAgentSignal(captainId, missionId, signal);
            }
        }

        /// <summary>
        /// Route one parsed agent signal: record a terminal marker, store a papercut, or apply a
        /// progress status and persist the progress signal.
        /// </summary>
        /// <param name="captainId">Emitting captain identifier.</param>
        /// <param name="missionId">Mission identifier, when the process is mapped to one.</param>
        /// <param name="signal">Parsed signal.</param>
        private void RouteAgentSignal(string captainId, string? missionId, ProgressParser.ProgressSignal signal)
        {
            // The first terminal marker ends the stage even if the process keeps running; later
            // markers (a re-review) never replace it.
            if (_TerminalMarkers != null && !String.IsNullOrEmpty(missionId) && TerminalMarkerTracker.IsTerminalMarker(signal)
                && _TerminalMarkers.TryRecordFirst(missionId, signal, DateTime.UtcNow))
            {
                _Logging.Info(_Header + "mission " + missionId + " emitted its first terminal marker [" + signal.Type + "] " + signal.Value);
            }

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
                        if (mission != null)
                        {
                            // Agent output reports progress only. A post-work or terminal status is
                            // reached through the completion and landing paths, which run their checks.
                            if (MissionStateMachine.IsAgentReportableTransition(mission.Status, signal.MissionStatus.Value))
                            {
                                mission.Status = signal.MissionStatus.Value;
                                mission.LastUpdateUtc = DateTime.UtcNow;
                                await _Database.Missions.UpdateAsync(mission).ConfigureAwait(false);
                                _Logging.Info(_Header + "mission " + mission.Id + " transitioned to " + signal.MissionStatus.Value + " via agent signal");
                            }
                            else
                            {
                                _Logging.Info(_Header + "mission " + mission.Id + " ignored agent status " + signal.MissionStatus.Value
                                    + " from " + mission.Status + ": agent output may report only InProgress, Testing, or Review");
                            }
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

                    ArmadaEvent papercutEvent = PapercutService.ToEvent(parsed);
                    if (mission != null)
                    {
                        EventOwnerScope.ApplyFromMission(papercutEvent, mission);
                    }
                    else
                    {
                        EventOwnerScopeResult scope = await EventOwnerScope.ApplyAsync(_Database, papercutEvent).ConfigureAwait(false);
                        if (scope.Outcome == Armada.Core.Enums.EventOwnerScopeOutcomeEnum.LookupFailed)
                            _Logging.Warn(_Header + "papercut from captain " + capturedCaptainId + " written without owner scope: " + scope.Detail);
                    }

                    await _Database.Events.CreateAsync(papercutEvent).ConfigureAwait(false);

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

                    await TokenUsageCapture.CaptureAsync(
                        _Database, _Logging, "mission",
                        model: usage.Model,
                        runtime: usage.Runtime,
                        tenantId: mission.TenantId,
                        userId: mission.UserId,
                        vesselId: mission.VesselId,
                        captainId: captain.Id,
                        sourceId: mission.Id,
                        inputTokens: usage.InputTokens,
                        outputTokens: usage.OutputTokens,
                        cachedTokens: usage.CacheReadTokens,
                        inputText: null,
                        outputText: null).ConfigureAwait(false);

                    ArmadaEvent evt = new ArmadaEvent(
                        "mission.token_usage",
                        "Authoritative token usage reported by " + usage.Runtime + " for " + usage.Model);
                    EventOwnerScope.ApplyFromMission(evt, mission);
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
            return Armada.Runtimes.ActivityRecords.IsActivityRecord(line);
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

            if (_IntentionalStops.TryTake(processId, captainId, missionId, IntentionalStopKindEnum.Completion))
            {
                _Logging.Info(_Header + "process " + processId + " was stopped after its terminal marker; completing mission " + missionId + " from the recorded output");
                exitCode = 0;
            }
            _TerminalMarkers?.Clear(missionId);

            lock (_ProcessToCaptain)
            {
                _ProcessToCaptain.Remove(processId);
                _ProcessToMission.Remove(processId);
            }
            _MissionHeartbeatWrites.TryRemove(missionId, out _);

            // Mark this PID as in flight BEFORE the async work begins. The health check consults
            // the marker so it never treats a process that exited cleanly, but whose completion
            // handler is still running, as a crash. The marker is released by the handler itself.
            _InFlightProcessExits[processId] = 0;

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
            _InFlightProcessExits[processId] = 0;
            try
            {
                await _Admiral.HandleProcessExitAsync(processId, exitCode, captainId, missionId).ConfigureAwait(false);
            }
            finally
            {
                // Record completion before releasing the in-flight marker, so there is no instant
                // at which the health check can see neither.
                _HandledProcessExits[processId] = DateTime.UtcNow;
                _InFlightProcessExits.TryRemove(processId, out _);
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
            if (captain.Runtime == AgentRuntimeEnum.ApiEndpoint)
            {
                if (!ApiAgentRuntime.CancelTracked(captain.ProcessId.Value))
                {
                    lock (_ProcessToCaptain)
                    {
                        _ProcessToCaptain.Remove(captain.ProcessId.Value);
                        _ProcessToMission.Remove(captain.ProcessId.Value);
                    }
                }
                return;
            }

            Armada.Runtimes.Interfaces.IAgentRuntime runtime = _RuntimeFactory.Create(captain.Runtime);
            await runtime.StopAsync(captain.ProcessId.Value).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Creates a runtime for a captain after resolving and rechecking its endpoint admission.
        /// </summary>
        /// <param name="captain">Captain to launch.</param>
        /// <returns>An authorized runtime instance.</returns>
        private async Task<Armada.Runtimes.Interfaces.IAgentRuntime> CreateRuntimeAsync(Captain captain)
        {
            if (captain.Runtime != AgentRuntimeEnum.ApiEndpoint)
            {
                await ResolveNativeEndpointCredentialsAsync(captain, CancellationToken.None).ConfigureAwait(false);
                return _RuntimeFactory.Create(captain.Runtime);
            }
            if (String.IsNullOrWhiteSpace(captain.TenantId) || String.IsNullOrWhiteSpace(captain.ModelEndpointId))
                throw new InvalidOperationException("An API-endpoint captain must reference an authorized tenant-owned model endpoint.");
            ModelEndpoint? endpoint = await _Database.ModelEndpoints.ReadAsync(
                captain.TenantId!,
                captain.ModelEndpointId!,
                CancellationToken.None).ConfigureAwait(false);
            string? validationError = ValidateApiEndpointAdmission(captain, endpoint, _Settings.ApiCaptainCloudProviders);
            if (!String.IsNullOrEmpty(validationError))
                throw new InvalidOperationException(validationError);

            Armada.Runtimes.Interfaces.IAgentRuntime missionRuntime = _RuntimeFactory.Create(captain.Runtime, endpoint);

            // A dispatched mission gets the command tool; this is the one place it is switched on. The chat
            // path builds the same runtime type and deliberately leaves it off.
            if (missionRuntime is ApiAgentRuntime apiRuntime)
                apiRuntime.CommandToolEnabled = true;

            return missionRuntime;
        }

        /// <summary>
        /// When a native-runtime captain references an inference model endpoint, resolve the endpoint's
        /// base URL, key, and model onto the launch snapshot so the runtime's provider resolver drives the
        /// managed endpoint instead of inline captain credentials. A captain with no endpoint id is left
        /// unchanged, so inline-credential captains keep working.
        /// </summary>
        /// <param name="captain">Captain to launch; mutated in place when it references an endpoint.</param>
        /// <param name="token">Cancellation token.</param>
        private async Task ResolveNativeEndpointCredentialsAsync(Captain captain, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(captain.ModelEndpointId)) return;
            if (String.IsNullOrWhiteSpace(captain.TenantId))
                throw new InvalidOperationException("A captain that references a model endpoint must belong to a tenant.");
            ModelEndpoint? endpoint = await _Database.ModelEndpoints.ReadAsync(
                captain.TenantId!,
                captain.ModelEndpointId!,
                token).ConfigureAwait(false);
            string? error = ValidateNativeEndpointAdmission(captain, endpoint);
            if (!String.IsNullOrEmpty(error))
                throw new InvalidOperationException(error);
            ApplyNativeEndpointCredentials(captain, endpoint!);
        }

        /// <summary>
        /// Refuse to seat a captain in a persona its runtime cannot serve. A persona that must run commands
        /// can be filled by a runtime that cannot, and the result is a confident, unfounded answer rather than
        /// a visible failure: a Judge that cannot read the diff or run the gate still votes. A refusal at launch
        /// is loud; a captain quietly working without the tools it needs is not.
        /// </summary>
        /// <remarks>
        /// A capability check, not a policy. Every runtime a mission can launch today runs commands, the
        /// API-endpoint runtime included through its run_command tool, so this refuses nothing in practice. It
        /// stays as the backstop that eligibility's answer is checked against, for a captain pinned by hand or
        /// by a captain override, and for any runtime added later without a command tool.
        /// </remarks>
        /// <param name="captain">Captain about to be launched.</param>
        /// <param name="persona">Persona of the mission being launched.</param>
        /// <returns>Null when the launch may proceed, otherwise the reason it may not.</returns>
        public static string? ValidateRuntimeSupportsPersona(Captain captain, string? persona)
        {
            if (captain == null) return "The captain is required.";

            // The same predicate the assignment-time eligibility check asks, so the two cannot drift.
            // This refusal is the backstop for a captain pinned by hand or by a captain override, which
            // reaches launch without passing eligibility.
            if (AgentRuntimeCapability.CanServePersona(captain.Runtime, persona)) return null;

            return "A " + captain.Runtime + " captain cannot serve the " + PersonaCatalog.NormalizeName(persona)
                + " persona: that persona must run commands in its dock, and this runtime provides no command tool. "
                + "Assign a captain whose runtime can run commands, or narrow this captain's allowed personas.";
        }

        /// <summary>
        /// Admission for a native-runtime captain that references an inference model endpoint. The endpoint
        /// must be an enabled inference endpoint the captain can see, and its model must match the captain's
        /// when both are set. Native runtimes already reach cloud providers through inline credentials, so
        /// this path does not impose the API-captain cloud-provider allowlist.
        /// </summary>
        /// <param name="captain">Captain to admit.</param>
        /// <param name="endpoint">Endpoint snapshot read for the captain tenant and endpoint identifier.</param>
        /// <returns>Null when admitted, otherwise a safe admission error.</returns>
        public static string? ValidateNativeEndpointAdmission(Captain captain, ModelEndpoint? endpoint)
        {
            if (captain == null)
                return "The captain is required.";
            if (String.IsNullOrWhiteSpace(captain.ModelEndpointId) || String.IsNullOrWhiteSpace(captain.TenantId))
                return "A captain that references a model endpoint must reference an authorized tenant-owned model endpoint.";
            if (endpoint == null
                || !String.Equals(endpoint.Id, captain.ModelEndpointId, StringComparison.Ordinal)
                || !String.Equals(endpoint.TenantId, captain.TenantId, StringComparison.Ordinal))
                return "The referenced model endpoint is not available to this captain.";
            if (endpoint.Scope == ScopeEnum.UserSpecific
                && !String.Equals(endpoint.UserId, captain.UserId, StringComparison.Ordinal))
                return "The referenced model endpoint is not available to this captain.";
            if (endpoint.Kind != ModelEndpointKindEnum.Inference)
                return "A captain requires an inference model endpoint.";
            if (!endpoint.Enabled)
                return "The referenced model endpoint is disabled.";
            if (!String.IsNullOrWhiteSpace(captain.Model)
                && !String.Equals(captain.Model, endpoint.Model, StringComparison.Ordinal))
                return "The captain model must match the configured model endpoint model.";
            return null;
        }

        /// <summary>
        /// Carry a validated inference endpoint's base URL, key, and model onto the launch snapshot. The
        /// endpoint wins over any inline captain credentials; a blank captain model takes the endpoint's.
        /// </summary>
        /// <param name="captain">Captain being launched.</param>
        /// <param name="endpoint">Validated inference endpoint the captain references.</param>
        public static void ApplyNativeEndpointCredentials(Captain captain, ModelEndpoint endpoint)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            captain.ApiBaseUrl = endpoint.BaseUrl;
            captain.ApiKey = endpoint.ApiKey;
            if (String.IsNullOrWhiteSpace(captain.Model))
                captain.Model = endpoint.Model;
        }

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
                EventOwnerScope.ApplyFromMission(promptEvent, mission);
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

            if (String.IsNullOrWhiteSpace(options.Endpoint))
                return "Mux validation requires a named endpoint.";

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

            BoundedProcessRequest request = new BoundedProcessRequest(
                GitProcessStartInfo.Create(workingDirectory, new[] { "init", "--quiet" }),
                GitProcessTimeouts.Resolve())
            {
                OutputLimitBytes = 64 * 1024
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }
            if (result.TimedOut)
                throw new InvalidOperationException("Failed to initialize temporary validation repository: git init timed out.");
            if (result.ExitCode == 0)
                return;

            string stderr = result.StandardError;
            string stdout = result.StandardOutput;
            string details = !String.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            if (String.IsNullOrWhiteSpace(details))
                details = "git init exited with code " + result.ExitCode + ".";

            throw new InvalidOperationException("Failed to initialize temporary validation repository: " + details);
        }

        #endregion
    }
}
