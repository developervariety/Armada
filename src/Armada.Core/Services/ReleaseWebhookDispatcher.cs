namespace Armada.Core.Services
{
    using System;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Posts a release-shipped JSON payload to the CD webhook endpoint configured in
    /// CdWebhookSettings. Categorizes outcomes: 2xx -> Success; 5xx, network, timeout, and auth
    /// (401/403) -> RetriableFailure; other 4xx -> NonRetriableFailure. Retriable failures are
    /// retried up to CdWebhookSettings.MaxRetries times with a fixed backoff. Transport failures
    /// are returned in the result and never thrown.
    /// </summary>
    public sealed class ReleaseWebhookDispatcher : IReleaseWebhookDispatcher, IDisposable
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private static readonly TimeSpan _DefaultTimeout = TimeSpan.FromSeconds(10);
        private const int _DefaultTimeoutSeconds = 10;
        private const int _MaxResponseBodyChars = 500;
        private readonly string _Header = "[ReleaseWebhookDispatcher] ";

        private readonly string _Url;
        private readonly string? _BearerToken;
        private readonly int _MaxRetries;
        private readonly TimeSpan _RetryBackoff;
        private readonly HttpClient _Http;
        private readonly LoggingModule _Logging;

        /// <summary>Production constructor: creates its own HttpClient from the supplied settings.</summary>
        public ReleaseWebhookDispatcher(CdWebhookSettings settings, LoggingModule logging)
            : this(settings, logging, new HttpClient())
        {
        }

        /// <summary>Test constructor: accepts a pre-configured HttpClient for injecting hand-rolled HttpMessageHandler doubles.</summary>
        public ReleaseWebhookDispatcher(CdWebhookSettings settings, LoggingModule logging, HttpClient http)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Http = http ?? throw new ArgumentNullException(nameof(http));
            if (String.IsNullOrWhiteSpace(settings.Url))
                throw new ArgumentException("CdWebhookSettings.Url is required.", nameof(settings));

            _Url = settings.Url!;
            _BearerToken = settings.BearerToken;
            _MaxRetries = Math.Clamp(settings.MaxRetries, 0, 5);

            int backoffSeconds = settings.RetryBackoffSeconds > 0 ? settings.RetryBackoffSeconds : 2;
            _RetryBackoff = TimeSpan.FromSeconds(backoffSeconds);

            int timeoutSeconds = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : _DefaultTimeoutSeconds;
            TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);
            if (_Http.Timeout == default || _Http.Timeout == TimeSpan.Zero)
                _Http.Timeout = timeout;
        }

        /// <inheritdoc/>
        public async Task<WebhookDispatchResult> DispatchAsync(ReleaseWebhookPayload payload, CancellationToken token = default)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            int totalAttempts = _MaxRetries + 1;
            WebhookDispatchResult result = new WebhookDispatchResult();
            for (int attempt = 1; attempt <= totalAttempts; attempt++)
            {
                result = await SendOnceAsync(payload, token).ConfigureAwait(false);
                result.Attempts = attempt;

                if (result.Outcome != WebhookDispatchOutcome.RetriableFailure || attempt == totalAttempts)
                    return result;

                _Logging.Warn(_Header + "retriable failure (attempt " + attempt + "/" + totalAttempts + "); retrying in " + _RetryBackoff.TotalSeconds + "s");
                await Task.Delay(_RetryBackoff, token).ConfigureAwait(false);
            }

            return result;
        }

        private async Task<WebhookDispatchResult> SendOnceAsync(ReleaseWebhookPayload payload, CancellationToken token)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            WebhookDispatchResult result = new WebhookDispatchResult();
            try
            {                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _Url);
                if (!String.IsNullOrEmpty(_BearerToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _BearerToken);

                string bodyJson = JsonSerializer.Serialize(payload, _JsonOptions);
                req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                using HttpResponseMessage resp = await _Http.SendAsync(req, token).ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                string body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                result.StatusCode = code;
                result.ResponseBody = body;

                if (code >= 200 && code < 300)
                {
                    result.Outcome = WebhookDispatchOutcome.Success;
                    return result;
                }

                if (code >= 500)
                {
                    result.Outcome = WebhookDispatchOutcome.RetriableFailure;
                    result.ErrorMessage = "5xx from CD webhook: " + code + " :: " + Truncate(body);
                    return result;
                }

                if (code == 401 || code == 403)
                {
                    result.Outcome = WebhookDispatchOutcome.RetriableFailure;
                    result.ErrorMessage = "auth failure (" + code + "); check bearer token :: " + Truncate(body);
                    return result;
                }

                result.Outcome = WebhookDispatchOutcome.NonRetriableFailure;
                result.ErrorMessage = "4xx from CD webhook: " + code + " :: " + Truncate(body);
                return result;
            }
            catch (TaskCanceledException tcex) when (!token.IsCancellationRequested)
            {
                result.Outcome = WebhookDispatchOutcome.RetriableFailure;
                result.ErrorMessage = "request timed out: " + tcex.Message;
                _Logging.Warn(_Header + "request timed out posting to CD webhook");
                return result;
            }
            catch (HttpRequestException hrex)
            {
                result.Outcome = WebhookDispatchOutcome.RetriableFailure;
                result.ErrorMessage = "network error: " + hrex.Message;
                _Logging.Warn(_Header + "network error posting to CD webhook");
                return result;
            }
            catch (Exception ex)
            {
                result.Outcome = WebhookDispatchOutcome.NonRetriableFailure;
                result.ErrorMessage = "unexpected error: " + ex.Message;
                _Logging.Error(_Header + "unexpected error posting to CD webhook");
                return result;
            }
        }

        /// <inheritdoc/>
        public void Dispose() => _Http.Dispose();

        private string Truncate(string? s)
        {
            if (String.IsNullOrEmpty(s)) return "";
            return s!.Length <= _MaxResponseBodyChars ? s : s.Substring(0, _MaxResponseBodyChars) + "...";
        }
    }
}
