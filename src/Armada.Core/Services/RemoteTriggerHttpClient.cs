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
    using SyslogLogging;

    /// <summary>
    /// Posts to the Claude Code Routines /fire endpoint with the required headers (bearer auth,
    /// anthropic-beta, anthropic-version) and a JSON body containing the `text` context string.
    /// Categorizes outcomes: 2xx -> Success (extracts session URL); 5xx + network + auth (401/403) ->
    /// RetriableFailure; other 4xx -> NonRetriableFailure. Default timeout is 10 seconds.
    /// </summary>
    public sealed class RemoteTriggerHttpClient : IRemoteTriggerHttpClient, IDisposable
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private static readonly TimeSpan _DefaultTimeout = TimeSpan.FromSeconds(10);
        private const string _Header = "[RemoteTriggerHttpClient] ";

        private readonly HttpClient _Http;
        private readonly LoggingModule _Logging;

        /// <summary>Production constructor: creates its own HttpClient with a 10s timeout.</summary>
        public RemoteTriggerHttpClient(LoggingModule logging)
            : this(logging, new HttpClient { Timeout = _DefaultTimeout })
        {
        }

        /// <summary>Test constructor: accepts a pre-configured HttpClient for injecting hand-rolled HttpMessageHandler doubles.</summary>
        public RemoteTriggerHttpClient(LoggingModule logging, HttpClient http)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Http = http ?? throw new ArgumentNullException(nameof(http));
            if (_Http.Timeout == default || _Http.Timeout == TimeSpan.Zero)
                _Http.Timeout = _DefaultTimeout;
        }

        /// <inheritdoc/>
        public async Task<FireResult> FireAsync(FireRequest request, CancellationToken token = default)
        {
            FireResult result = new FireResult();
            try
            {
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, request.FireUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.BearerToken);
                req.Headers.Add("anthropic-beta", request.BetaHeader);
                req.Headers.Add("anthropic-version", request.AnthropicVersion);

                string bodyJson = JsonSerializer.Serialize(new { text = request.Text }, _JsonOptions);
                req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                using HttpResponseMessage resp = await _Http.SendAsync(req, token).ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                string body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                result.StatusCode = code;
                result.ResponseBody = body;

                if (code >= 200 && code < 300)
                {
                    result.Outcome = FireOutcome.Success;
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("claude_code_session_url", out JsonElement urlEl)
                            && urlEl.ValueKind == JsonValueKind.String)
                        {
                            result.SessionUrl = urlEl.GetString();
                        }
                    }
                    catch
                    {
                        // parse failure is non-fatal; session URL is informational only
                    }
                    return result;
                }

                if (code >= 500)
                {
                    result.Outcome = FireOutcome.RetriableFailure;
                    result.ErrorMessage = "5xx from /fire: " + code + " :: " + Truncate(body, 500);
                    return result;
                }

                if (code == 401 || code == 403)
                {
                    result.Outcome = FireOutcome.RetriableFailure;
                    result.ErrorMessage = "auth failure (" + code + "); check bearer token + beta header :: " + Truncate(body, 500);
                    return result;
                }

                result.Outcome = FireOutcome.NonRetriableFailure;
                result.ErrorMessage = "4xx from /fire: " + code + " :: " + Truncate(body, 500);
                return result;
            }
            catch (TaskCanceledException tcex) when (!token.IsCancellationRequested)
            {
                result.Outcome = FireOutcome.RetriableFailure;
                result.ErrorMessage = "request timed out: " + tcex.Message;
                _Logging.Warn(_Header + "request timed out posting to /fire");
                return result;
            }
            catch (HttpRequestException hrex)
            {
                result.Outcome = FireOutcome.RetriableFailure;
                result.ErrorMessage = "network error: " + hrex.Message;
                _Logging.Warn(_Header + "network error posting to /fire");
                return result;
            }
            catch (Exception ex)
            {
                result.Outcome = FireOutcome.NonRetriableFailure;
                result.ErrorMessage = "unexpected error: " + ex.Message;
                _Logging.Error(_Header + "unexpected error posting to /fire");
                return result;
            }
        }

        /// <inheritdoc/>
        public void Dispose() => _Http.Dispose();

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s!.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
