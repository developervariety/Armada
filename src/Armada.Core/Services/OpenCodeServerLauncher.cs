namespace Armada.Core.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Starts and stops a local OpenCode daemon for inference when configured.
    /// </summary>
    public sealed class OpenCodeServerLauncher : IDisposable
    {
        #region Public-Types

        /// <summary>
        /// Process start abstraction used to allow hand-rolled tests without real process launch.
        /// </summary>
        public interface IProcessRunner
        {
            /// <summary>
            /// Start a process using the supplied process start info.
            /// </summary>
            ILaunchedProcess? Start(ProcessStartInfo startInfo);
        }

        /// <summary>
        /// Active process abstraction returned by <see cref="IProcessRunner"/>.
        /// </summary>
        public interface ILaunchedProcess : IDisposable
        {
            /// <summary>
            /// Process id.
            /// </summary>
            int ProcessId { get; }

            /// <summary>
            /// Whether the process has exited.
            /// </summary>
            bool HasExited { get; }

            /// <summary>
            /// Begin asynchronous drain of standard output.
            /// </summary>
            Task<string> DrainStandardOutputAsync();

            /// <summary>
            /// Begin asynchronous drain of standard error.
            /// </summary>
            Task<string> DrainStandardErrorAsync();

            /// <summary>
            /// Kill the process.
            /// </summary>
            void Kill(bool entireProcessTree);

            /// <summary>
            /// Wait for process exit.
            /// </summary>
            bool WaitForExit(int milliseconds);
        }

        #endregion

        #region Private-Members

        private const string _Header = "[OpenCodeServerLauncher] ";
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly HttpClient _Http;
        private readonly IProcessRunner _ProcessRunner;
        private readonly Func<TimeSpan, CancellationToken, Task> _DelayAsync;

        private ILaunchedProcess? _SpawnedProcess;
        private bool _AttachedToExisting;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create an OpenCode server launcher.
        /// </summary>
        public OpenCodeServerLauncher(ArmadaSettings settings, LoggingModule logging, HttpClient http)
            : this(settings, logging, http, new DefaultProcessRunner(), DefaultDelayAsync)
        {
        }

        /// <summary>
        /// Testing constructor with injectable process runner and delay strategy.
        /// </summary>
        public OpenCodeServerLauncher(
            ArmadaSettings settings,
            LoggingModule logging,
            HttpClient http,
            IProcessRunner processRunner,
            Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Http = http ?? throw new ArgumentNullException(nameof(http));
            _ProcessRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
            _DelayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Start and health-check the OpenCode daemon when configuration requires it.
        /// </summary>
        public async Task StartAsync(CancellationToken token = default)
        {
            if (!ShouldRun()) return;

            bool healthyAlready = await IsHealthyAsync(token).ConfigureAwait(false);
            if (healthyAlready)
            {
                _AttachedToExisting = true;
                _Logging.Info(_Header + "attached to existing daemon");
                return;
            }

            ProcessStartInfo startInfo = BuildProcessStartInfo();
            _SpawnedProcess = _ProcessRunner.Start(startInfo);
            if (_SpawnedProcess == null)
                throw new InvalidOperationException("OpenCode daemon launch failed: process start returned null.");

            _ = DrainOutputAsync(_SpawnedProcess.DrainStandardOutputAsync(), "stdout");
            _ = DrainOutputAsync(_SpawnedProcess.DrainStandardErrorAsync(), "stderr");

            DateTime deadline = DateTime.UtcNow.AddSeconds(_Settings.CodeIndex.OpenCodeServer.StartupTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (await IsHealthyAsync(token).ConfigureAwait(false))
                {
                    _Logging.Info(_Header + "daemon healthy on pid " + _SpawnedProcess.ProcessId);
                    return;
                }

                await _DelayAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }

            DisposeSpawnedProcess();
            throw new InvalidOperationException("OpenCode daemon did not become healthy before startup timeout.");
        }

        /// <summary>
        /// Stop only the daemon process started by this launcher.
        /// </summary>
        public void Dispose()
        {
            if (_AttachedToExisting) return;
            DisposeSpawnedProcess();
        }

        #endregion

        #region Private-Methods

        private bool ShouldRun()
        {
            string client = _Settings.CodeIndex.InferenceClient;
            if (!string.Equals(client, "OpenCodeServer", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!_Settings.CodeIndex.OpenCodeServer.AutoLaunch)
                return false;
            return true;
        }

        private ProcessStartInfo BuildProcessStartInfo()
        {
            string command = BuildServeCommand();
            string arguments = BuildServeArguments();
            string executablePath = _Settings.CodeIndex.OpenCodeServer.ExecutablePath ?? string.Empty;

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                startInfo.FileName = executablePath.Trim();
                startInfo.Arguments = arguments;
            }
            else if (ShouldUseWindowsCmdShim())
            {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/c " + command;
            }
            else
            {
                startInfo.FileName = "opencode";
                startInfo.Arguments = arguments;
            }

            string password = _Settings.CodeIndex.OpenCodeServer.Password ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(password))
                startInfo.Environment["OPENCODE_SERVER_PASSWORD"] = password;

            return startInfo;
        }

        private bool ShouldUseWindowsCmdShim()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData)) return false;
            string cmdPath = Path.Combine(appData, "npm", "opencode.cmd");
            return File.Exists(cmdPath);
        }

        private string BuildServeCommand()
        {
            return "opencode " + BuildServeArguments();
        }

        private string BuildServeArguments()
        {
            int port = _Settings.CodeIndex.OpenCodeServer.Port;
            string host = _Settings.CodeIndex.OpenCodeServer.Hostname ?? "127.0.0.1";
            if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";
            return "serve --port " + port + " --hostname " + host;
        }

        private async Task<bool> IsHealthyAsync(CancellationToken token)
        {
            try
            {
                string endpoint = BuildHealthEndpoint();
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                ApplyBasicAuthorization(request);
                using HttpResponseMessage response = await SendWithTimeoutAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return false;

                string responseBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                OpenCodeHealthResponse? parsed = JsonSerializer.Deserialize<OpenCodeHealthResponse>(responseBody, _JsonOptions);
                return parsed?.Healthy == true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private string BuildHealthEndpoint()
        {
            string baseUrl = _Settings.CodeIndex.OpenCodeServer.BaseUrl ?? "http://127.0.0.1:4096";
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "http://127.0.0.1:4096";
            return baseUrl.Trim().TrimEnd('/') + "/global/health";
        }

        private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage request, CancellationToken token)
        {
            using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_Settings.CodeIndex.OpenCodeServer.RequestTimeoutSeconds));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            return await _Http.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
        }

        private void ApplyBasicAuthorization(HttpRequestMessage request)
        {
            string password = _Settings.CodeIndex.OpenCodeServer.Password ?? string.Empty;
            if (string.IsNullOrWhiteSpace(password)) return;

            string username = _Settings.CodeIndex.OpenCodeServer.Username;
            if (string.IsNullOrWhiteSpace(username)) username = "opencode";

            string raw = username + ":" + password;
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64);
        }

        private async Task DrainOutputAsync(Task<string> drainTask, string streamName)
        {
            try
            {
                string output = await drainTask.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(output))
                    _Logging.Debug(_Header + streamName + ": " + ExtractTail(output));
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed draining " + streamName + ": " + ex.Message);
            }
        }

        private static string ExtractTail(string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length <= 512) return trimmed;
            return "..." + trimmed.Substring(trimmed.Length - 512);
        }

        private void DisposeSpawnedProcess()
        {
            if (_SpawnedProcess == null) return;
            try
            {
                if (!_SpawnedProcess.HasExited)
                    _SpawnedProcess.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            try
            {
                _SpawnedProcess.WaitForExit(5000);
            }
            catch
            {
            }

            try
            {
                _SpawnedProcess.Dispose();
            }
            catch
            {
            }

            _SpawnedProcess = null;
        }

        private static Task DefaultDelayAsync(TimeSpan delay, CancellationToken token)
        {
            return Task.Delay(delay, token);
        }

        #endregion

        #region Private-Types

        private sealed class OpenCodeHealthResponse
        {
            [JsonPropertyName("healthy")]
            public bool Healthy { get; set; }
        }

        private sealed class DefaultProcessRunner : IProcessRunner
        {
            public ILaunchedProcess? Start(ProcessStartInfo startInfo)
            {
                Process? process = Process.Start(startInfo);
                if (process == null) return null;
                return new DefaultLaunchedProcess(process);
            }
        }

        private sealed class DefaultLaunchedProcess : ILaunchedProcess
        {
            private readonly Process _Process;

            public int ProcessId => _Process.Id;
            public bool HasExited => _Process.HasExited;

            public DefaultLaunchedProcess(Process process)
            {
                _Process = process ?? throw new ArgumentNullException(nameof(process));
            }

            public Task<string> DrainStandardOutputAsync()
            {
                return _Process.StandardOutput.ReadToEndAsync();
            }

            public Task<string> DrainStandardErrorAsync()
            {
                return _Process.StandardError.ReadToEndAsync();
            }

            public void Kill(bool entireProcessTree)
            {
                _Process.Kill(entireProcessTree);
            }

            public bool WaitForExit(int milliseconds)
            {
                return _Process.WaitForExit(milliseconds);
            }

            public void Dispose()
            {
                _Process.Dispose();
            }
        }

        #endregion
    }
}
