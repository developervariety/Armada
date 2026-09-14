namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Probes the admiral health endpoint. Health counts only when the response reports healthy and
    /// its server start time is not earlier than the launched process, so a server left over from
    /// before the launch cannot satisfy the check.
    /// </summary>
    public sealed class SelfDeployHttpHealthProbe : ISelfDeployHealthProbe
    {
        /// <summary>
        /// Maximum accepted response body in bytes.
        /// </summary>
        public const int MaximumResponseBytes = 64 * 1024;

        /// <summary>
        /// Clock tolerance between the operating-system start time and the server's own start time.
        /// </summary>
        public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private readonly HttpClient _Client;
        private readonly TimeSpan _AttemptTimeout;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="client">HTTP client.</param>
        /// <param name="attemptTimeout">Maximum duration of one attempt.</param>
        public SelfDeployHttpHealthProbe(HttpClient client, TimeSpan attemptTimeout)
        {
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            if (attemptTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
            _AttemptTimeout = attemptTimeout;
        }

        /// <summary>
        /// Health URL for the admiral listening on the loopback interface.
        /// </summary>
        /// <param name="admiralPort">Admiral REST port.</param>
        /// <returns>Health endpoint URL.</returns>
        public static string LoopbackHealthUrl(int admiralPort)
        {
            return "http://127.0.0.1:" + admiralPort + "/api/v1/status/health";
        }

        /// <inheritdoc />
        public async Task<SelfDeployHealthResult> CheckAsync(
            string healthUrl,
            SelfDeployProcessIdentity process,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(healthUrl)) return Unhealthy("health_url_missing");
            if (process == null) throw new ArgumentNullException(nameof(process));
            using (CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                attempt.CancelAfter(_AttemptTimeout);
                try
                {
                    using (HttpResponseMessage response = await _Client.GetAsync(healthUrl, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return Unhealthy("health_http_" + (int)response.StatusCode);
                        if (response.Content.Headers.ContentLength > MaximumResponseBytes) return Unhealthy("health_response_too_large");
                        byte[] body = await ReadBoundedAsync(response, attempt.Token).ConfigureAwait(false);
                        if (body.Length > MaximumResponseBytes) return Unhealthy("health_response_too_large");
                        HealthResponse? health = JsonSerializer.Deserialize<HealthResponse>(body, JsonOptions);
                        if (health == null) return Unhealthy("health_response_invalid");
                        if (!String.Equals(health.Status, "healthy", StringComparison.OrdinalIgnoreCase))
                            return Unhealthy("health_status_not_healthy");
                        if (health.StartUtc == null) return Unhealthy("health_start_missing");
                        DateTime serverStartUtc = health.StartUtc.Value.Kind == DateTimeKind.Unspecified
                            ? DateTime.SpecifyKind(health.StartUtc.Value, DateTimeKind.Utc)
                            : health.StartUtc.Value.ToUniversalTime();
                        if (serverStartUtc < process.StartedUtc - StartTolerance)
                            return Unhealthy("health_start_predates_launch");
                        return new SelfDeployHealthResult { Healthy = true };
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return Unhealthy("health_attempt_timeout");
                }
                catch (HttpRequestException)
                {
                    return Unhealthy("health_unreachable");
                }
                catch (JsonException)
                {
                    return Unhealthy("health_response_invalid");
                }
                catch (IOException)
                {
                    return Unhealthy("health_response_io_failed");
                }
            }
        }

        private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
        {
            using (Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                while (true)
                {
                    int read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
                    if (read == 0) break;
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaximumResponseBytes) break;
                }
                return buffer.ToArray();
            }
        }

        private static SelfDeployHealthResult Unhealthy(string reason)
        {
            return new SelfDeployHealthResult { Healthy = false, FailureReason = reason };
        }
    }
}
