namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Sends one probe to a captain's configured model and reports success, latency and a short
    /// error. A runtime captain is probed through the existing captain model validation, which
    /// already sends one fixed prompt and starts no mission. An API-endpoint captain is admitted
    /// by that same validation, then probed through the existing model-endpoint validation, which
    /// sends one bounded provider request. A global administrator may run a probe, and the same
    /// captain cannot be probed again until the minimum interval has elapsed.
    /// </summary>
    public sealed class CaptainModelProbeService
    {
        /// <summary>
        /// Default gap between probes of one captain.
        /// </summary>
        public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromSeconds(30);

        private const int ErrorLimit = 200;

        private readonly DatabaseDriver _Database;
        private readonly Func<Captain, CancellationToken, Task<CaptainModelValidationFailure?>> _ValidateCaptain;
        private readonly Func<AuthContext, string, CancellationToken, Task<ModelEndpointProbeResult>>? _ValidateEndpoint;
        private readonly TimeSpan _MinimumInterval;
        private readonly Func<DateTime> _Clock;
        private readonly object _Gate = new object();
        private readonly Dictionary<string, DateTime> _NextAllowedUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>
        /// Create the probe service.
        /// </summary>
        /// <param name="database">Database that stores captains.</param>
        /// <param name="validateCaptain">Existing captain model validation. It sends the fixed prompt for a runtime captain and admits an API-endpoint captain.</param>
        /// <param name="validateEndpoint">Existing model-endpoint validation. Used only after an API-endpoint captain is admitted. Null skips that request.</param>
        /// <param name="minimumInterval">Minimum gap between probes of one captain. Zero allows another probe at the same clock reading.</param>
        /// <param name="clock">UTC clock. Null uses <see cref="DateTime.UtcNow"/>.</param>
        public CaptainModelProbeService(
            DatabaseDriver database,
            Func<Captain, CancellationToken, Task<CaptainModelValidationFailure?>> validateCaptain,
            Func<AuthContext, string, CancellationToken, Task<ModelEndpointProbeResult>>? validateEndpoint = null,
            TimeSpan? minimumInterval = null,
            Func<DateTime>? clock = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _ValidateCaptain = validateCaptain ?? throw new ArgumentNullException(nameof(validateCaptain));
            _ValidateEndpoint = validateEndpoint;
            _MinimumInterval = minimumInterval ?? DefaultMinimumInterval;
            if (_MinimumInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
            _Clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Probe one captain. Creates no mission.
        /// </summary>
        /// <param name="auth">Caller. A global administrator is required.</param>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Success, latency and a short error reason.</returns>
        /// <exception cref="UnauthorizedAccessException">The caller is not a global administrator.</exception>
        /// <exception cref="KeyNotFoundException">The captain does not exist.</exception>
        /// <exception cref="CaptainModelProbeRateLimitException">This captain was probed too recently.</exception>
        public async Task<CaptainModelProbeResult> ProbeAsync(AuthContext auth, string captainId, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (!auth.IsAuthenticated) throw new UnauthorizedAccessException("Authentication is required.");
            if (!auth.IsAdmin) throw new UnauthorizedAccessException("Global administrator access is required to probe a captain model.");
            if (String.IsNullOrWhiteSpace(captainId)) throw new ArgumentException("A captain id is required.", nameof(captainId));

            string id = captainId.Trim();
            Captain? captain = await _Database.Captains.ReadAsync(id, token).ConfigureAwait(false);
            if (captain == null) throw new KeyNotFoundException("Captain not found: " + id);

            DateTime started = _Clock().ToUniversalTime();
            Reserve(id, started);

            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                CaptainModelValidationFailure? failure = await _ValidateCaptain(captain, token).ConfigureAwait(false);
                if (failure != null)
                    return Failed(id, watch, failure.Message);

                if (captain.Runtime == AgentRuntimeEnum.ApiEndpoint)
                {
                    if (_ValidateEndpoint == null)
                        return Failed(id, watch, "API-endpoint captain probe requires the model-endpoint validation path.");
                    if (String.IsNullOrWhiteSpace(captain.ModelEndpointId))
                        return Failed(id, watch, "An API-endpoint captain must reference an inference model endpoint.");

                    ModelEndpointProbeResult endpoint = await _ValidateEndpoint(auth, captain.ModelEndpointId, token).ConfigureAwait(false);
                    if (!endpoint.Success)
                        return Failed(id, watch, endpoint.Error);
                }

                watch.Stop();
                return new CaptainModelProbeResult
                {
                    CaptainId = id,
                    Success = true,
                    LatencyMs = watch.ElapsedMilliseconds
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CaptainModelProbeRateLimitException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed(id, watch, ex.Message);
            }
        }

        private void Reserve(string captainId, DateTime utcNow)
        {
            lock (_Gate)
            {
                DateTime next;
                if (_NextAllowedUtc.TryGetValue(captainId, out next) && utcNow < next)
                    throw new CaptainModelProbeRateLimitException(next);
                _NextAllowedUtc[captainId] = utcNow + _MinimumInterval;
            }
        }

        private static CaptainModelProbeResult Failed(string captainId, Stopwatch watch, string? message)
        {
            watch.Stop();
            return new CaptainModelProbeResult
            {
                CaptainId = captainId,
                Success = false,
                LatencyMs = watch.ElapsedMilliseconds,
                Error = ShortReason(message)
            };
        }

        /// <summary>
        /// One line, at most 200 characters. Provider text can be long; the operator needs the reason, not the body.
        /// </summary>
        private static string ShortReason(string? message)
        {
            if (String.IsNullOrWhiteSpace(message)) return "The model probe failed.";
            string flat = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (flat.Contains("  ", StringComparison.Ordinal))
                flat = flat.Replace("  ", " ", StringComparison.Ordinal);
            if (flat.Length <= ErrorLimit) return flat;
            return flat.Substring(0, ErrorLimit);
        }
    }
}
