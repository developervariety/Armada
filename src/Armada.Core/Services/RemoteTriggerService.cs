namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Orchestrates admiral-side orchestrator wakes via HTTP POST to Claude Code Routines /fire (RemoteFire mode).
    /// Per-vessel 60s coalescing, rolling 20/hour throttle, retry-once-on-retriable-failure,
    /// and 3-strike consecutive-failure tracking apply.
    /// Critical events bypass coalescing and throttle.
    /// </summary>
    public sealed class RemoteTriggerService : IRemoteTriggerService
    {
        private const int CoalesceWindowSeconds = 60;
        private const int ThrottleCapPerHour = 20;
        private const int ConsecutiveFailureCap = 3;
        private const int ThrottleNotificationDebounceMinutes = 10;

        private static readonly TimeSpan _DefaultRetryDelay = TimeSpan.FromSeconds(2);

        private readonly RemoteTriggerSettings _Settings;
        private readonly IRemoteTriggerHttpClient _Http;
        private readonly LoggingModule _Logging;
        private readonly TimeSpan _RetryDelay;
        private const string _Header = "[RemoteTriggerService] ";

        private readonly Dictionary<string, DateTime> _PerVesselLastFire = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Queue<DateTime> _RecentWakes = new Queue<DateTime>();
        private int _ConsecutiveFailures = 0;
        private DateTime? _LastThrottleNotification = null;
        private readonly object _Lock = new object();

        /// <summary>
        /// Constructs the service. If <paramref name="settings"/> is null the feature is disabled
        /// and all Fire* methods are no-ops.
        /// </summary>
        public RemoteTriggerService(RemoteTriggerSettings? settings, IRemoteTriggerHttpClient http, LoggingModule logging)
            : this(settings, http, logging, _DefaultRetryDelay)
        {
        }

        /// <summary>Overload for test isolation: accepts a custom <paramref name="retryDelay"/> so unit tests run without sleeping.</summary>
        public RemoteTriggerService(RemoteTriggerSettings? settings, IRemoteTriggerHttpClient http, LoggingModule logging, TimeSpan retryDelay)
        {
            _Settings = settings ?? new RemoteTriggerSettings { Enabled = false };
            _Http = http ?? throw new ArgumentNullException(nameof(http));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _RetryDelay = retryDelay;
        }

        /// <summary>Current consecutive failure count. Resets to zero on any successful fire.</summary>
        public int ConsecutiveFailures
        {
            get { lock (_Lock) { return _ConsecutiveFailures; } }
        }

        /// <inheritdoc/>
        public async Task FireDrainerAsync(string vesselId, string text, CancellationToken token = default)
        {
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

                if (_RecentWakes.Count >= ThrottleCapPerHour)
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
            _Logging.Warn(_Header + "throttle notification: " + ThrottleCapPerHour + " wakes in last hour exceeded for vessel " + vesselId + "; manual drain recommended");
        }
    }
}
