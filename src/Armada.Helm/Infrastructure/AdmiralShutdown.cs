namespace Armada.Helm.Infrastructure
{
    using System;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Settings;

    /// <summary>
    /// The one way Helm stops an Admiral and proves it has exited. Every command that stops the server, or that
    /// must not touch Admiral data while the server runs, calls this. The stop request carries Helm's bearer
    /// credential, a refused request is reported with its status, and the server counts as down only when a
    /// connection to its health route fails. A health probe that times out, or any HTTP answer at all, counts as
    /// still running.
    /// </summary>
    public sealed class AdmiralShutdown
    {
        #region Public-Members

        /// <summary>
        /// Route that asks the Admiral to stop.
        /// </summary>
        public const string StopPath = "/api/v1/server/stop";

        /// <summary>
        /// Route probed to learn whether the Admiral still answers.
        /// </summary>
        public const string HealthPath = "/api/v1/status/health";

        #endregion

        #region Private-Members

        private readonly HttpClient _Client;
        private readonly string _BaseUrl;
        private readonly string _BearerToken;
        private readonly int _PollAttempts;
        private readonly TimeSpan _PollInterval;
        private readonly TimeSpan _RequestTimeout;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="client">HTTP client used for the stop request and health probes.</param>
        /// <param name="baseUrl">Admiral base URL, without a trailing slash.</param>
        /// <param name="bearerToken">Bearer credential sent with the stop request.</param>
        /// <param name="pollAttempts">Number of health probes after the stop request before giving up.</param>
        /// <param name="pollInterval">Delay before each health probe.</param>
        /// <param name="requestTimeout">Timeout for each individual request.</param>
        public AdmiralShutdown(
            HttpClient client,
            string baseUrl,
            string bearerToken,
            int pollAttempts = 15,
            TimeSpan? pollInterval = null,
            TimeSpan? requestTimeout = null)
        {
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            if (String.IsNullOrEmpty(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));
            if (pollAttempts < 1) throw new ArgumentOutOfRangeException(nameof(pollAttempts));
            _BaseUrl = baseUrl.TrimEnd('/');
            _BearerToken = bearerToken ?? "";
            _PollAttempts = pollAttempts;
            _PollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
            _RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// Build the shutdown for the Admiral the given settings describe, using the address and bearer credential
        /// every other Helm request uses.
        /// </summary>
        /// <param name="client">HTTP client.</param>
        /// <param name="settings">Armada settings.</param>
        /// <returns>The shutdown.</returns>
        public static AdmiralShutdown ForSettings(HttpClient client, ArmadaSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new AdmiralShutdown(client, BaseUrlFor(settings), BearerTokenFor(settings));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Admiral base URL for the given settings. Watson binds the default "localhost" host to IPv4 loopback, so
        /// 127.0.0.1 avoids a slow IPv6 localhost fallback.
        /// </summary>
        /// <param name="settings">Armada settings.</param>
        /// <returns>Base URL.</returns>
        public static string BaseUrlFor(ArmadaSettings settings)
        {
            return "http://127.0.0.1:" + settings.AdmiralPort;
        }

        /// <summary>
        /// Bearer credential Helm sends. <see cref="ArmadaSettings.ApiKey" /> stores it; when unset, the seeded
        /// default credential is used.
        /// </summary>
        /// <param name="settings">Armada settings.</param>
        /// <returns>Bearer token.</returns>
        public static string BearerTokenFor(ArmadaSettings settings)
        {
            return String.IsNullOrEmpty(settings.ApiKey) ? "default" : settings.ApiKey;
        }

        /// <summary>
        /// Whether anything answers at the Admiral health route. Only a failed connection counts as not answering.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the Admiral answers or the probe could not prove otherwise.</returns>
        public async Task<bool> IsAnsweringAsync(CancellationToken token = default)
        {
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(_RequestTimeout);
                try
                {
                    using (HttpResponseMessage response = await _Client.GetAsync(_BaseUrl + HealthPath, timeout.Token).ConfigureAwait(false))
                    {
                        return true;
                    }
                }
                catch (HttpRequestException)
                {
                    return false;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // A listener that accepts the connection but does not answer in time is still alive.
                    return true;
                }
            }
        }

        /// <summary>
        /// Ask the Admiral to stop and wait until it no longer answers.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stop result.</returns>
        public async Task<AdmiralStopResult> StopAsync(CancellationToken token = default)
        {
            if (!await IsAnsweringAsync(token).ConfigureAwait(false))
            {
                return new AdmiralStopResult
                {
                    Outcome = AdmiralStopOutcomeEnum.NotRunning,
                    Message = "Admiral server is not running at " + _BaseUrl + "."
                };
            }

            int? statusCode = null;
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _BaseUrl + StopPath))
            {
                timeout.CancelAfter(_RequestTimeout);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _BearerToken);
                try
                {
                    using (HttpResponseMessage response = await _Client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                    {
                        statusCode = (int)response.StatusCode;
                        if (!response.IsSuccessStatusCode)
                        {
                            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                            return new AdmiralStopResult
                            {
                                Outcome = AdmiralStopOutcomeEnum.Refused,
                                StatusCode = statusCode,
                                Message = "Admiral server refused the stop request with HTTP " + statusCode + DescribeRefusal(statusCode.Value, body)
                            };
                        }
                    }
                }
                catch (HttpRequestException)
                {
                    // The connection dropped mid-request; the health probes below decide whether the server exited.
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // The request timed out; the health probes below decide whether the server exited.
                }
            }

            for (int attempt = 0; attempt < _PollAttempts; attempt++)
            {
                await Task.Delay(_PollInterval, token).ConfigureAwait(false);
                if (!await IsAnsweringAsync(token).ConfigureAwait(false))
                {
                    return new AdmiralStopResult
                    {
                        Outcome = AdmiralStopOutcomeEnum.Stopped,
                        StatusCode = statusCode,
                        Message = "Admiral server stopped."
                    };
                }
            }

            return new AdmiralStopResult
            {
                Outcome = AdmiralStopOutcomeEnum.StillRunning,
                StatusCode = statusCode,
                Message = "Admiral server still answers at " + _BaseUrl + " after "
                    + (_PollInterval.TotalSeconds * _PollAttempts).ToString("0.##") + " seconds."
            };
        }

        #endregion

        #region Private-Methods

        private static string DescribeRefusal(int statusCode, string body)
        {
            string hint = statusCode == 401 || statusCode == 403
                ? ". Shutdown requires authorization; set the ApiKey in the Helm settings file to a credential allowed to stop the server."
                : ".";
            string trimmed = (body ?? "").Trim();
            if (trimmed.Length > 300) trimmed = trimmed.Substring(0, 300);
            return trimmed.Length > 0 ? hint + " Response: " + trimmed : hint;
        }

        #endregion
    }
}
