namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Orchestrates admiral-side orchestrator wakes via RemoteFire (HTTP POST to Claude Code Routines /fire)
    /// or AgentWake (spawn a local Claude/Codex process) modes.
    /// Per-vessel 60s coalescing, rolling hourly throttle cap (default 20, configurable), retry-once-on-failure,
    /// and 3-strike consecutive-failure tracking apply.
    /// Critical events bypass coalescing and throttle.
    /// AgentWake additionally enforces a global single-flight lease to prevent bursts from spawning
    /// many concurrent agent processes.
    /// </summary>
    public sealed class RemoteTriggerService : IRemoteTriggerService
    {
        #region Private-Members

        private const int CoalesceWindowSeconds = 60;
        private const int DefaultThrottleCapPerHour = 20;
        private const int ConsecutiveFailureCap = 3;
        private const int ThrottleNotificationDebounceMinutes = 10;

        private static readonly TimeSpan _DefaultRetryDelay = TimeSpan.FromSeconds(2);

        private readonly RemoteTriggerSettings _Settings;
        private readonly IRemoteTriggerHttpClient _Http;
        private readonly IAgentWakeProcessHost? _AgentWakeHost;
        private readonly DatabaseDriver? _Database;
        private readonly LoggingModule _Logging;
        private readonly TimeSpan _RetryDelay;
        private readonly int _ThrottleCap;
        private const string _Header = "[RemoteTriggerService] ";

        private readonly Dictionary<string, DateTime> _PerVesselLastFire = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Queue<DateTime> _RecentWakes = new Queue<DateTime>();
        private int _ConsecutiveFailures = 0;
        private DateTime? _LastThrottleNotification = null;
        private bool _AgentWakeRunning = false;
        private AgentWakeSessionRegistration? _AgentWakeSession = null;
        private readonly object _Lock = new object();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Constructs the service. If <paramref name="settings"/> is null the feature is disabled
        /// and all Fire* methods are no-ops.
        /// </summary>
        public RemoteTriggerService(RemoteTriggerSettings? settings, IRemoteTriggerHttpClient http, LoggingModule logging)
            : this(settings, http, null, null, logging, _DefaultRetryDelay)
        {
        }

        /// <summary>Overload for test isolation: accepts a custom <paramref name="retryDelay"/> so unit tests run without sleeping.</summary>
        public RemoteTriggerService(RemoteTriggerSettings? settings, IRemoteTriggerHttpClient http, LoggingModule logging, TimeSpan retryDelay)
            : this(settings, http, null, null, logging, retryDelay)
        {
        }

        /// <summary>Production overload: includes an <see cref="IAgentWakeProcessHost"/> for AgentWake mode support.</summary>
        public RemoteTriggerService(RemoteTriggerSettings? settings, IRemoteTriggerHttpClient http, IAgentWakeProcessHost? agentWakeHost, LoggingModule logging)
            : this(settings, http, agentWakeHost, null, logging, _DefaultRetryDelay)
        {
        }

        /// <summary>Production overload: AgentWake process host plus database driver for MCP notification delivery mode.</summary>
        public RemoteTriggerService(RemoteTriggerSettings? settings, IRemoteTriggerHttpClient http, IAgentWakeProcessHost? agentWakeHost, DatabaseDriver? database, LoggingModule logging)
            : this(settings, http, agentWakeHost, database, logging, _DefaultRetryDelay)
        {
        }

        /// <summary>Test overload preserving the original 5-arg signature with retry delay.</summary>
        public RemoteTriggerService(RemoteTriggerSettings? settings, IRemoteTriggerHttpClient http, IAgentWakeProcessHost? agentWakeHost, LoggingModule logging, TimeSpan retryDelay)
            : this(settings, http, agentWakeHost, null, logging, retryDelay)
        {
        }

        /// <summary>Full overload: includes AgentWake process host, optional database driver for MCP notification delivery, and custom retry delay for test isolation.</summary>
        public RemoteTriggerService(RemoteTriggerSettings? settings, IRemoteTriggerHttpClient http, IAgentWakeProcessHost? agentWakeHost, DatabaseDriver? database, LoggingModule logging, TimeSpan retryDelay)
        {
            _Settings = settings ?? new RemoteTriggerSettings { Enabled = false };
            _Http = http ?? throw new ArgumentNullException(nameof(http));
            _AgentWakeHost = agentWakeHost;
            _Database = database;
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _RetryDelay = retryDelay;
            int throttleCap = _Settings.ThrottleCapPerHour;
            if (throttleCap <= 0)
                throttleCap = DefaultThrottleCapPerHour;
            _ThrottleCap = throttleCap;
        }

        #endregion

        #region Public-Members

        /// <summary>Current consecutive failure count. Resets to zero on any successful fire.</summary>
        public int ConsecutiveFailures
        {
            get { lock (_Lock) { return _ConsecutiveFailures; } }
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc/>
        public async Task FireDrainerAsync(string vesselId, string text, CancellationToken token = default)
        {
            if (_Settings.IsAgentWakeConfigured())
            {
                AgentWakeDeliveryMode deliveryMode = _Settings.AgentWake?.DeliveryMode ?? AgentWakeDeliveryMode.SpawnProcess;
                bool didMcp = false;
                bool didSpawn = false;

                if (deliveryMode == AgentWakeDeliveryMode.McpNotification || deliveryMode == AgentWakeDeliveryMode.Both)
                {
                    await WriteWakeSignalAsync(vesselId, text, critical: false, token).ConfigureAwait(false);
                    didMcp = true;
                }

                if (deliveryMode == AgentWakeDeliveryMode.SpawnProcess || deliveryMode == AgentWakeDeliveryMode.Both)
                {
                    if (_AgentWakeHost == null)
                    {
                        _Logging.Warn(_Header + "AgentWake SpawnProcess mode configured but no IAgentWakeProcessHost wired; skipping spawn");
                    }
                    else
                    {
                        await FireAgentWakeDrainerAsync(vesselId, text, token).ConfigureAwait(false);
                        didSpawn = true;
                    }
                }

                if (!didMcp && !didSpawn)
                {
                    _Logging.Warn(_Header + "AgentWake mode configured (delivery=" + deliveryMode + ") but no delivery path fired");
                }
                return;
            }

            if (!_Settings.IsDrainerConfigured()) return;
            if (string.IsNullOrEmpty(vesselId)) return;

            DateTime now = DateTime.UtcNow;

            lock (_Lock)
            {
                if (_PerVesselLastFire.TryGetValue(vesselId, out DateTime last) &&
                    (now - last).TotalSeconds < CoalesceWindowSeconds)
                {
                    return;
                }

                while (_RecentWakes.Count > 0 && (now - _RecentWakes.Peek()).TotalMinutes > 60)
                    _RecentWakes.Dequeue();

                if (_RecentWakes.Count >= _ThrottleCap)
                {
                    _Logging.Warn(_Header + "throttle hit (" + _RecentWakes.Count + " wakes/hour); suppressing wake for vessel " + vesselId);
                    MaybeFireThrottleNotification(now, vesselId);
                    return;
                }

                _PerVesselLastFire[vesselId] = now;
                _RecentWakes.Enqueue(now);
            }

            FireRequest req = new FireRequest
            {
                FireUrl = _Settings.DrainerFireUrl!,
                BearerToken = _Settings.DrainerBearerToken!,
                BetaHeader = _Settings.BetaHeader,
                AnthropicVersion = _Settings.AnthropicVersion,
                Text = text,
            };
            await SendWithRetryAsync(req, token).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task FireCriticalAsync(string text, CancellationToken token = default)
        {
            if (_Settings.IsAgentWakeConfigured())
            {
                AgentWakeDeliveryMode deliveryMode = _Settings.AgentWake?.DeliveryMode ?? AgentWakeDeliveryMode.SpawnProcess;
                bool didMcp = false;
                bool didSpawn = false;

                if (deliveryMode == AgentWakeDeliveryMode.McpNotification || deliveryMode == AgentWakeDeliveryMode.Both)
                {
                    await WriteWakeSignalAsync(vesselId: null, text, critical: true, token).ConfigureAwait(false);
                    didMcp = true;
                }

                if (deliveryMode == AgentWakeDeliveryMode.SpawnProcess || deliveryMode == AgentWakeDeliveryMode.Both)
                {
                    if (_AgentWakeHost == null)
                    {
                        _Logging.Warn(_Header + "AgentWake SpawnProcess mode configured but no IAgentWakeProcessHost wired; skipping critical spawn");
                    }
                    else
                    {
                        await FireAgentWakeCriticalAsync(text, token).ConfigureAwait(false);
                        didSpawn = true;
                    }
                }

                if (!didMcp && !didSpawn)
                {
                    _Logging.Warn(_Header + "AgentWake mode configured (delivery=" + deliveryMode + ") but no critical delivery path fired");
                }
                return;
            }

            if (!_Settings.Enabled) return;
            if (_Settings.Mode == RemoteTriggerMode.Disabled) return;

            if (!_Settings.IsCriticalConfigured() && !_Settings.IsDrainerConfigured()) return;

            FireRequest req;
            if (_Settings.IsCriticalConfigured())
            {
                req = new FireRequest
                {
                    FireUrl = _Settings.CriticalFireUrl!,
                    BearerToken = _Settings.CriticalBearerToken!,
                    BetaHeader = _Settings.BetaHeader,
                    AnthropicVersion = _Settings.AnthropicVersion,
                    Text = text,
                };
            }
            else
            {
                req = new FireRequest
                {
                    FireUrl = _Settings.DrainerFireUrl!,
                    BearerToken = _Settings.DrainerBearerToken!,
                    BetaHeader = _Settings.BetaHeader,
                    AnthropicVersion = _Settings.AnthropicVersion,
                    Text = "[CRITICAL] " + text,
                };
            }

            await SendWithRetryAsync(req, token).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public AgentWakeSessionRegistration RegisterAgentWakeSession(AgentWakeSessionRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (registration.Runtime == AgentWakeRuntime.Auto)
                throw new ArgumentException("AgentWake session registration requires a concrete runtime.", nameof(registration));

            AgentWakeSessionRegistration normalized = new AgentWakeSessionRegistration
            {
                Runtime = registration.Runtime,
                SessionId = NormalizeOptional(registration.SessionId),
                Command = NormalizeOptional(registration.Command),
                WorkingDirectory = NormalizeOptional(registration.WorkingDirectory),
                ClientName = NormalizeOptional(registration.ClientName),
                LastSeenUtc = DateTime.UtcNow,
            };

            lock (_Lock)
            {
                _AgentWakeSession = normalized;
            }

            return CloneAgentWakeSession(normalized);
        }

        /// <inheritdoc/>
        public AgentWakeSessionRegistration? GetAgentWakeSession()
        {
            lock (_Lock)
            {
                return _AgentWakeSession == null ? null : CloneAgentWakeSession(_AgentWakeSession);
            }
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Writes a <see cref="SignalTypeEnum.Wake"/> signal row carrying the wake text. Interactive
        /// orchestrators drain these via <c>armada_enumerate entityType=signals signalType=Wake
        /// unreadOnly=true</c> and acknowledge with <c>armada_mark_signal_read</c>. Used when
        /// <see cref="AgentWakeDeliveryMode.McpNotification"/> or
        /// <see cref="AgentWakeDeliveryMode.Both"/> is configured.
        /// </summary>
        /// <param name="vesselId">Source vessel id, or null/empty for cross-vessel critical wakes.</param>
        /// <param name="text">Wake-text payload as produced by <c>MissionOutcomeWakeHandler.BuildWakeText</c> or the critical caller.</param>
        /// <param name="critical">When true, the signal payload is prefixed with <c>[CRITICAL]</c> so a poller can route it differently.</param>
        /// <param name="token">Cancellation token.</param>
        private async Task WriteWakeSignalAsync(string? vesselId, string text, bool critical, CancellationToken token)
        {
            if (_Database == null)
            {
                _Logging.Warn(_Header + "AgentWake McpNotification mode configured but no DatabaseDriver wired; skipping signal write");
                return;
            }

            try
            {
                string body = text ?? string.Empty;
                if (!string.IsNullOrEmpty(vesselId))
                {
                    // Signal model has no VesselId field today; encode it into the payload prefix so
                    // operators can grep without an extra schema migration. Format: "[vsl=<vid>] <text>".
                    body = "[vsl=" + vesselId + "] " + body;
                }
                string payload = critical ? "[CRITICAL] " + body : body;
                Signal signal = new Signal(SignalTypeEnum.Wake, payload);
                signal.TenantId = Constants.DefaultTenantId;
                await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "WriteWakeSignalAsync failed: " + ex.Message);
            }
        }

        private async Task FireAgentWakeDrainerAsync(string vesselId, string text, CancellationToken token)
        {
            if (string.IsNullOrEmpty(vesselId)) return;

            DateTime now = DateTime.UtcNow;

            lock (_Lock)
            {
                if (_PerVesselLastFire.TryGetValue(vesselId, out DateTime last) &&
                    (now - last).TotalSeconds < CoalesceWindowSeconds)
                {
                    return;
                }

                while (_RecentWakes.Count > 0 && (now - _RecentWakes.Peek()).TotalMinutes > 60)
                    _RecentWakes.Dequeue();

                if (_RecentWakes.Count >= _ThrottleCap)
                {
                    _Logging.Warn(_Header + "throttle hit (" + _RecentWakes.Count + " wakes/hour); suppressing AgentWake for vessel " + vesselId);
                    MaybeFireThrottleNotification(now, vesselId);
                    return;
                }

                if (_AgentWakeRunning)
                {
                    _Logging.Info(_Header + "AgentWake already running; suppressing drainer wake for vessel " + vesselId);
                    return;
                }

                _PerVesselLastFire[vesselId] = now;
                _RecentWakes.Enqueue(now);
                _AgentWakeRunning = true;
            }

            List<AgentWakeProcessRequest> requests = BuildAgentWakeRequests(text);
            await StartAgentWakeWithRetryAsync(requests, token).ConfigureAwait(false);
        }

        private async Task FireAgentWakeCriticalAsync(string text, CancellationToken token)
        {
            lock (_Lock)
            {
                if (_AgentWakeRunning)
                {
                    _Logging.Info(_Header + "AgentWake already running; suppressing critical wake");
                    return;
                }
                _AgentWakeRunning = true;
            }

            List<AgentWakeProcessRequest> requests = BuildAgentWakeRequests("[CRITICAL] " + text);
            await StartAgentWakeWithRetryAsync(requests, token).ConfigureAwait(false);
        }

        private List<AgentWakeProcessRequest> BuildAgentWakeRequests(string text)
        {
            AgentWakeSettings awSettings = _Settings.AgentWake ?? new AgentWakeSettings();
            string payload = text + "\n\n[AgentWake] This is a one-shot wake. Handle current Armada work and exit. Do not start a polling loop or recurring schedule.";
            List<AgentWakeProcessRequest> requests = new List<AgentWakeProcessRequest>();

            if (awSettings.Runtime != AgentWakeRuntime.Auto)
            {
                requests.Add(BuildAgentWakeRequest(awSettings.Runtime, awSettings, awSettings.SessionId, awSettings.Command, awSettings.WorkingDirectory, payload));
                return requests;
            }

            AgentWakeSessionRegistration? session = GetAgentWakeSession();
            HashSet<AgentWakeRuntime> added = new HashSet<AgentWakeRuntime>();

            if (session != null)
            {
                requests.Add(BuildAgentWakeRequest(
                    session.Runtime,
                    awSettings,
                    string.IsNullOrEmpty(awSettings.SessionId) ? session.SessionId : awSettings.SessionId,
                    string.IsNullOrEmpty(awSettings.Command) ? session.Command : awSettings.Command,
                    string.IsNullOrEmpty(awSettings.WorkingDirectory) ? session.WorkingDirectory : awSettings.WorkingDirectory,
                    payload));
                added.Add(session.Runtime);
            }

            foreach (AgentWakeRuntime runtime in awSettings.GetRuntimePreference())
            {
                if (added.Contains(runtime)) continue;
                requests.Add(BuildAgentWakeRequest(runtime, awSettings, awSettings.SessionId, awSettings.Command, awSettings.WorkingDirectory, payload));
                added.Add(runtime);
            }

            return requests;
        }

        private static AgentWakeProcessRequest BuildAgentWakeRequest(
            AgentWakeRuntime runtime,
            AgentWakeSettings awSettings,
            string? sessionId,
            string? commandOverride,
            string? workingDirectory,
            string payload)
        {
            string command = awSettings.GetEffectiveCommand(runtime, commandOverride);
            List<string> args = new List<string>();

            if (runtime == AgentWakeRuntime.Codex)
            {
                args.Add("exec");
                args.Add("resume");
                if (!string.IsNullOrEmpty(sessionId))
                    args.Add(sessionId!);
                else
                    args.Add("--last");
                args.Add("-");
            }
            else
            {
                args.Add("--print");
                if (!string.IsNullOrEmpty(sessionId))
                {
                    args.Add("--resume");
                    args.Add(sessionId!);
                }
                else
                {
                    args.Add("--continue");
                }
                args.Add("--setting-sources");
                args.Add("project,local");
                args.Add("--strict-mcp-config");
            }

            return new AgentWakeProcessRequest
            {
                Command = command,
                ArgumentList = args,
                StdinPayload = payload,
                WorkingDirectory = workingDirectory,
                TimeoutSeconds = awSettings.TimeoutSeconds,
                EnvironmentVariables = awSettings.EnvironmentVariables,
            };
        }

        private async Task StartAgentWakeWithRetryAsync(List<AgentWakeProcessRequest> requests, CancellationToken token)
        {
            bool started = TryStartAnyAgentWakeRequest(requests);

            if (!started)
            {
                _Logging.Warn(_Header + "AgentWake spawn failed on first attempt; retrying");
                if (_RetryDelay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(_RetryDelay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        ReleaseAgentWakeLease();
                        throw;
                    }
                }
                started = TryStartAnyAgentWakeRequest(requests);
            }

            if (started)
            {
                lock (_Lock) { _ConsecutiveFailures = 0; }
            }
            else
            {
                int failureCount;
                lock (_Lock)
                {
                    _ConsecutiveFailures++;
                    failureCount = _ConsecutiveFailures;
                    _AgentWakeRunning = false;
                }
                _Logging.Error(_Header + "AgentWake spawn failed after retry; consecutiveFailures=" + failureCount);
                if (failureCount >= ConsecutiveFailureCap)
                    _Logging.Error(_Header + "consecutive failure cap reached (" + ConsecutiveFailureCap + ") for AgentWake");
            }
        }

        private bool TryStartAnyAgentWakeRequest(List<AgentWakeProcessRequest> requests)
        {
            foreach (AgentWakeProcessRequest request in requests)
            {
                if (_AgentWakeHost!.TryStart(request, ReleaseAgentWakeLease))
                    return true;

                _Logging.Warn(_Header + "AgentWake spawn candidate failed: " + request.Command + " " + string.Join(" ", request.ArgumentList));
            }

            return false;
        }

        private void ReleaseAgentWakeLease()
        {
            lock (_Lock) { _AgentWakeRunning = false; }
        }

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static AgentWakeSessionRegistration CloneAgentWakeSession(AgentWakeSessionRegistration source)
        {
            return new AgentWakeSessionRegistration
            {
                Runtime = source.Runtime,
                SessionId = source.SessionId,
                Command = source.Command,
                WorkingDirectory = source.WorkingDirectory,
                ClientName = source.ClientName,
                LastSeenUtc = source.LastSeenUtc,
            };
        }

        private async Task SendWithRetryAsync(FireRequest req, CancellationToken token)
        {
            FireResult result = await _Http.FireAsync(req, token).ConfigureAwait(false);

            if (result.Outcome == FireOutcome.RetriableFailure)
            {
                _Logging.Warn(_Header + "retriable failure on first attempt; retrying :: " + (result.ErrorMessage ?? "(no message)"));
                if (_RetryDelay > TimeSpan.Zero)
                    await Task.Delay(_RetryDelay, token).ConfigureAwait(false);
                result = await _Http.FireAsync(req, token).ConfigureAwait(false);
            }

            if (result.Outcome == FireOutcome.Success)
            {
                if (!string.IsNullOrEmpty(result.SessionUrl))
                    _Logging.Info(_Header + "wake fired; session_url=" + result.SessionUrl);

                lock (_Lock) { _ConsecutiveFailures = 0; }
                return;
            }

            int failureCount;
            lock (_Lock)
            {
                if (result.Outcome == FireOutcome.RetriableFailure)
                    _ConsecutiveFailures++;
                failureCount = _ConsecutiveFailures;
            }

            _Logging.Warn(_Header + "fire failed (" + result.Outcome + "); consecutiveFailures=" + failureCount + " :: " + (result.ErrorMessage ?? "(no message)"));

            if (failureCount >= ConsecutiveFailureCap)
            {
                _Logging.Error(_Header + "consecutive failure cap reached (" + ConsecutiveFailureCap + "); PushNotification fallback deferred to V2 -- see spec section 8");
            }
        }

        private void MaybeFireThrottleNotification(DateTime now, string vesselId)
        {
            if (_LastThrottleNotification.HasValue &&
                (now - _LastThrottleNotification.Value).TotalMinutes < ThrottleNotificationDebounceMinutes)
            {
                return;
            }
            _LastThrottleNotification = now;
            _Logging.Warn(_Header + "throttle notification: " + _ThrottleCap + " wakes in last hour exceeded for vessel " + vesselId + "; manual drain recommended");
        }

        #endregion
    }
}
