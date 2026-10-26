namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>
    /// Reads the provenance a stage outcome cannot take from the database: the agent runtime's version (the CLI's
    /// <c>--version</c> on the host that ran the attempt, or the Ollama server version for an API-endpoint captain) and
    /// the digest of an Ollama model. Values are cached per captain and Harbor (versions) and per endpoint, Harbor and
    /// model (digests). A probe never throws and never holds exit handling for longer than <see cref="WaitTimeout"/>:
    /// a probe still running then finishes in the background and fills the cache for the next exit.
    /// </summary>
    public class StageProvenanceProbe
    {
        #region Public-Members

        /// <summary>
        /// Longest value kept (a runtime version line or a digest).
        /// </summary>
        public const int MaxValueLength = 200;

        /// <summary>
        /// Hard limit, in milliseconds, for one probe (the CLI command or the HTTP request), running in the background.
        /// </summary>
        public const int ProbeTimeoutMs = 10000;

        /// <summary>
        /// How long a probed value is reused. Defaults to 30 minutes, so an upgrade is picked up without a restart.
        /// </summary>
        public TimeSpan CacheDuration
        {
            get => _CacheDuration;
            set => _CacheDuration = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }

        /// <summary>
        /// How long a probe that returned nothing (failed, timed out, or not reported) is remembered before it is tried
        /// again. Defaults to 5 minutes.
        /// </summary>
        public TimeSpan FailureCacheDuration
        {
            get => _FailureCacheDuration;
            set => _FailureCacheDuration = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }

        /// <summary>
        /// Longest time a caller waits for a probe that is not cached yet. Defaults to 3 seconds.
        /// </summary>
        public TimeSpan WaitTimeout
        {
            get => _WaitTimeout;
            set => _WaitTimeout = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }

        /// <summary>
        /// Replaces the runtime-version probe (tests, or hosts that know the version another way). Receives the captain,
        /// the Harbor that ran the attempt (null for the Admiral's host) and the captain's model endpoint (or null).
        /// Null (the default) uses <see cref="ProbeRuntimeVersionAsync"/>.
        /// </summary>
        public Func<Captain, string?, ModelEndpoint?, CancellationToken, Task<string?>>? RuntimeVersionProbe { get; set; } = null;

        /// <summary>
        /// Replaces the model-digest probe. Receives the endpoint, the model and the Harbor (or null). Null (the default)
        /// uses <see cref="ProbeModelDigestAsync"/>.
        /// </summary>
        public Func<ModelEndpoint, string, string?, CancellationToken, Task<string?>>? ModelDigestProbe { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Header = "[StageProvenanceProbe] ";
        private readonly LoggingModule _Logging;
        private readonly Func<string?, IHostCommandExecutor?> _CommandsFor;
        private readonly object _Lock = new object();
        private readonly Dictionary<string, Task<string?>> _Probes = new Dictionary<string, Task<string?>>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _StartedUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private TimeSpan _CacheDuration = TimeSpan.FromMinutes(30);
        private TimeSpan _FailureCacheDuration = TimeSpan.FromMinutes(5);
        private TimeSpan _WaitTimeout = TimeSpan.FromSeconds(3);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="commandsFor">Runs a command on a host: given a Harbor id (or null for the Admiral's host), the
        /// executor, or null when that host cannot be reached. Null runs commands on the Admiral's host only.</param>
        /// <exception cref="ArgumentNullException">Thrown when logging is null.</exception>
        public StageProvenanceProbe(LoggingModule logging, Func<string?, IHostCommandExecutor?>? commandsFor = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _CommandsFor = commandsFor ?? (harborId => String.IsNullOrEmpty(harborId) ? new LocalHostCommandExecutor() : null);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The runtime version for an attempt, from the cache or a probe. Never throws.
        /// </summary>
        /// <param name="captain">Captain that ran the attempt.</param>
        /// <param name="harborId">Harbor that ran it, or null for the Admiral's host.</param>
        /// <param name="endpoint">The captain's model endpoint, or null.</param>
        /// <param name="token">Cancellation token (ends the wait, not the background probe).</param>
        /// <returns>The version, or null when it is not known within <see cref="WaitTimeout"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when captain is null.</exception>
        public Task<string?> GetRuntimeVersionAsync(Captain captain, string? harborId, ModelEndpoint? endpoint, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            string key = "runtime|" + captain.Id + "|" + (harborId ?? String.Empty) + "|" + captain.Runtime + "|" + (endpoint?.Id ?? String.Empty);
            Func<Captain, string?, ModelEndpoint?, CancellationToken, Task<string?>> probe = RuntimeVersionProbe ?? ProbeRuntimeVersionAsync;
            return GetCachedAsync(key, ct => probe(captain, harborId, endpoint, ct), token);
        }

        /// <summary>
        /// The model digest for an attempt on an Ollama endpoint, from the cache or a probe. Never throws.
        /// </summary>
        /// <param name="endpoint">The captain's model endpoint.</param>
        /// <param name="model">Model the attempt ran.</param>
        /// <param name="harborId">Harbor that ran it, or null for the Admiral's host.</param>
        /// <param name="token">Cancellation token (ends the wait, not the background probe).</param>
        /// <returns>The digest, or null for another provider, no model, or when it is not known within
        /// <see cref="WaitTimeout"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when endpoint is null.</exception>
        public Task<string?> GetModelDigestAsync(ModelEndpoint endpoint, string? model, string? harborId, CancellationToken token = default)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (endpoint.Provider != ModelProviderEnum.Ollama || String.IsNullOrWhiteSpace(model)) return Task.FromResult<string?>(null);
            string key = "digest|" + endpoint.Id + "|" + endpoint.BaseUrl + "|" + (harborId ?? String.Empty) + "|" + model;
            Func<ModelEndpoint, string, string?, CancellationToken, Task<string?>> probe = ModelDigestProbe ?? ProbeModelDigestAsync;
            return GetCachedAsync(key, ct => probe(endpoint, model!, harborId, ct), token);
        }

        /// <summary>
        /// Forget every cached value.
        /// </summary>
        public void ClearCache()
        {
            lock (_Lock)
            {
                _Probes.Clear();
                _StartedUtc.Clear();
            }
        }

        /// <summary>
        /// The default runtime-version probe, uncached: <c>&lt;cli&gt; --version</c> on the host that ran the attempt
        /// (first line of its output), or for a runtime without a CLI on an Ollama endpoint, "Ollama " plus the server
        /// version from GET /api/version. Null otherwise.
        /// </summary>
        /// <param name="captain">Captain.</param>
        /// <param name="harborId">Harbor, or null for the Admiral's host.</param>
        /// <param name="endpoint">Model endpoint, or null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The version, or null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when captain is null.</exception>
        public async Task<string?> ProbeRuntimeVersionAsync(Captain captain, string? harborId, ModelEndpoint? endpoint, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            string? command = HarborRuntimeDetector.CommandFor(captain.Runtime);
            if (command != null)
            {
                IHostCommandExecutor? commands = _CommandsFor(harborId);
                if (commands == null) return null;
                HostCommandRequest request = new HostCommandRequest
                {
                    Executable = command,
                    Arguments = new List<string> { "--version" },
                    TimeoutMs = ProbeTimeoutMs,
                    Label = command + " --version"
                };
                HostCommandResult result = await commands.RunAsync(request, token).ConfigureAwait(false);
                if (!result.Success) return null;
                return FirstLine(result.StandardOutput) ?? FirstLine(result.StandardError);
            }

            if (endpoint != null && endpoint.Provider == ModelProviderEnum.Ollama && Reachable(endpoint, harborId))
            {
                OllamaVersionResponse? version = await GetJsonAsync<OllamaVersionResponse>(endpoint, "/api/version", token).ConfigureAwait(false);
                string? value = FirstLine(version?.Version);
                return value == null ? null : "Ollama " + value;
            }

            return null;
        }

        /// <summary>
        /// The default model-digest probe, uncached: the digest of the model in Ollama's GET /api/tags (a name without a
        /// tag matches ":latest"). Null for another provider, an unknown model, or an endpoint the Admiral cannot reach
        /// on the Harbor's behalf (a loopback address used by a Harbor attempt).
        /// </summary>
        /// <param name="endpoint">Model endpoint.</param>
        /// <param name="model">Model name.</param>
        /// <param name="harborId">Harbor, or null for the Admiral's host.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The digest, or null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when endpoint or model is null.</exception>
        public async Task<string?> ProbeModelDigestAsync(ModelEndpoint endpoint, string model, string? harborId, CancellationToken token = default)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (String.IsNullOrWhiteSpace(model)) throw new ArgumentNullException(nameof(model));
            if (endpoint.Provider != ModelProviderEnum.Ollama || !Reachable(endpoint, harborId)) return null;

            OllamaTagsResponse? tags = await GetJsonAsync<OllamaTagsResponse>(endpoint, "/api/tags", token).ConfigureAwait(false);
            if (tags?.Models == null) return null;
            string wanted = NormalizeModelName(model);
            foreach (OllamaTagModel candidate in tags.Models)
            {
                if (candidate == null) continue;
                if (String.Equals(NormalizeModelName(candidate.Name), wanted, StringComparison.Ordinal)
                    || String.Equals(NormalizeModelName(candidate.Model), wanted, StringComparison.Ordinal))
                    return FirstLine(candidate.Digest);
            }

            return null;
        }

        #endregion

        #region Private-Methods

        private async Task<string?> GetCachedAsync(string key, Func<CancellationToken, Task<string?>> probe, CancellationToken token)
        {
            Task<string?> task;
            bool started = false;
            DateTime now = DateTime.UtcNow;
            lock (_Lock)
            {
                if (_Probes.TryGetValue(key, out Task<string?>? existing) && IsFresh(existing, _StartedUtc[key], now))
                {
                    task = existing;
                }
                else
                {
                    task = RunProbeAsync(key, probe);
                    _Probes[key] = task;
                    _StartedUtc[key] = now;
                    started = true;
                }
            }

            if (task.IsCompleted) return task.Result;

            // Only the caller that started the probe waits; while it is still running, later exits record null rather
            // than waiting again.
            if (!started || _WaitTimeout <= TimeSpan.Zero) return null;
            try
            {
                Task finished = await Task.WhenAny(task, Task.Delay(_WaitTimeout, token)).ConfigureAwait(false);
                if (finished == task) return task.Result;
            }
            catch (OperationCanceledException)
            {
            }

            _Logging.Debug(_Header + "probe " + key + " did not answer within " + (int)_WaitTimeout.TotalMilliseconds + " ms; recording null");
            return null;
        }

        private bool IsFresh(Task<string?> task, DateTime startedUtc, DateTime now)
        {
            if (!task.IsCompleted) return true;
            TimeSpan lifetime = task.Result == null ? _FailureCacheDuration : _CacheDuration;
            return now - startedUtc < lifetime;
        }

        private Task<string?> RunProbeAsync(string key, Func<CancellationToken, Task<string?>> probe)
        {
            // Task.Run keeps a probe that blocks synchronously off the caller's thread.
            return Task.Run(async () =>
            {
                using (CancellationTokenSource cts = new CancellationTokenSource(ProbeTimeoutMs))
                {
                    try
                    {
                        string? value = await probe(cts.Token).ConfigureAwait(false);
                        return FirstLine(value);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Debug(_Header + "probe " + key + " failed: " + ex.Message);
                        return null;
                    }
                }
            });
        }

        private static async Task<T?> GetJsonAsync<T>(ModelEndpoint endpoint, string path, CancellationToken token) where T : class
        {
            string? root = OllamaRoot(endpoint.BaseUrl);
            if (root == null) return null;

            HttpClient? configured = ModelEndpointAuth.CreateHttpClient(endpoint);
            using (HttpClient http = configured ?? new HttpClient())
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, root + path))
            {
                http.Timeout = TimeSpan.FromMilliseconds(ProbeTimeoutMs);
                string secret = ModelEndpointAuth.ProviderSecret(endpoint);
                if (!String.IsNullOrEmpty(secret)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                using (HttpResponseMessage response = await http.SendAsync(request, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    if (String.IsNullOrWhiteSpace(body)) return null;
                    return JsonSerializer.Deserialize<T>(body);
                }
            }
        }

        private static string? OllamaRoot(string? baseUrl)
        {
            if (String.IsNullOrWhiteSpace(baseUrl)) return null;
            string root = baseUrl.Trim().TrimEnd('/');
            if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root.Substring(0, root.Length - 3);
            else if (root.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) root = root.Substring(0, root.Length - 4);
            return Uri.TryCreate(root, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? root : null;
        }

        private static bool Reachable(ModelEndpoint endpoint, string? harborId)
        {
            // A Harbor calls a loopback address on its own computer; the Admiral would read a different server.
            if (String.IsNullOrEmpty(harborId)) return true;
            string? root = OllamaRoot(endpoint.BaseUrl);
            if (root == null || !Uri.TryCreate(root, UriKind.Absolute, out Uri? uri)) return false;
            return !uri.IsLoopback;
        }

        private static string NormalizeModelName(string? name)
        {
            if (String.IsNullOrWhiteSpace(name)) return String.Empty;
            string trimmed = name.Trim().ToLowerInvariant();
            return trimmed.Contains(':') ? trimmed : trimmed + ":latest";
        }

        private static string? FirstLine(string? text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                return trimmed.Length <= MaxValueLength ? trimmed : trimmed.Substring(0, MaxValueLength);
            }

            return null;
        }

        #endregion
    }
}
