namespace Armada.Test.Shared.Infrastructure
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Server;
    using SyslogLogging;

    /// <summary>
    /// Lazily boots a single in-process <see cref="ArmadaServer"/> backed by a temp SQLite
    /// database and exposes shared HTTP clients for end-to-end suites. The server starts once
    /// on first access (thread-safe) and is reused across every e2e suite, matching the legacy
    /// automated harness. Readiness is confirmed by polling the health endpoint rather than a
    /// fixed sleep, so slow machines do not flake. The server and temp directory are torn down
    /// on process exit.
    /// </summary>
    public sealed class E2EServerFixture
    {
        #region Public-Members

        /// <summary>
        /// HTTP client authenticated with the test API key.
        /// </summary>
        public HttpClient AuthClient { get; private set; } = null!;

        /// <summary>
        /// HTTP client with no authentication headers.
        /// </summary>
        public HttpClient UnauthClient { get; private set; } = null!;

        /// <summary>
        /// HTTP client targeting the MCP port.
        /// </summary>
        public HttpClient McpClient { get; private set; } = null!;

        /// <summary>
        /// Base URL of the REST API (http://127.0.0.1:{restPort}).
        /// </summary>
        public string BaseUrl { get; private set; } = "";

        /// <summary>
        /// Test API key accepted by the server.
        /// </summary>
        public string ApiKey { get; private set; } = "";

        /// <summary>
        /// REST API port.
        /// </summary>
        public int RestPort { get; private set; }

        /// <summary>
        /// MCP port.
        /// </summary>
        public int McpPort { get; private set; }

        /// <summary>
        /// Temp directory holding the server's database, logs, docks, and repos.
        /// </summary>
        public string TempDir { get; private set; } = "";

        #endregion

        #region Private-Members

        private static readonly SemaphoreSlim _Gate = new SemaphoreSlim(1, 1);
        private static E2EServerFixture? _Instance;
        private ArmadaServer _Server = null!;

        #endregion

        #region Constructors-and-Factories

        private E2EServerFixture()
        {
        }

        /// <summary>
        /// Get the shared fixture, booting the server on first call.
        /// </summary>
        /// <returns>The initialized fixture.</returns>
        public static async Task<E2EServerFixture> GetAsync()
        {
            if (_Instance != null) return _Instance;

            await _Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_Instance != null) return _Instance;

                E2EServerFixture fixture = new E2EServerFixture();
                await fixture.StartAsync().ConfigureAwait(false);
                _Instance = fixture;
                return _Instance;
            }
            finally
            {
                _Gate.Release();
            }
        }

        #endregion

        #region Private-Methods

        private async Task StartAsync()
        {
            TempDir = Path.Combine(Path.GetTempPath(), "armada_e2e_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);

            string sqlitePath = Path.Combine(TempDir, "armada.db");
            DatabaseSettings dbSettings = new DatabaseSettings();
            dbSettings.Type = DatabaseTypeEnum.Sqlite;
            dbSettings.Filename = sqlitePath;

            RestPort = GetAvailablePort();
            McpPort = GetAvailablePort();
            ApiKey = "test-key-" + Guid.NewGuid().ToString("N");

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            ArmadaSettings settings = new ArmadaSettings();
            settings.DataDirectory = TempDir;
            settings.DatabasePath = dbSettings.Filename;
            settings.Database = dbSettings;
            settings.LogDirectory = Path.Combine(TempDir, "logs");
            settings.DocksDirectory = Path.Combine(TempDir, "docks");
            settings.ReposDirectory = Path.Combine(TempDir, "repos");
            settings.AdmiralPort = RestPort;
            settings.McpPort = McpPort;
            settings.ApiKey = ApiKey;
            settings.HeartbeatIntervalSeconds = 300;
            settings.InitializeDirectories();

            _Server = new ArmadaServer(logging, settings, quiet: true);
            await _Server.StartAsync().ConfigureAwait(false);

            BaseUrl = "http://127.0.0.1:" + RestPort;

            AuthClient = new HttpClient();
            AuthClient.BaseAddress = new Uri(BaseUrl);
            AuthClient.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

            UnauthClient = new HttpClient();
            UnauthClient.BaseAddress = new Uri(BaseUrl);

            McpClient = new HttpClient();
            McpClient.BaseAddress = new Uri("http://localhost:" + McpPort);

            await WaitForReadyAsync().ConfigureAwait(false);

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        }

        private async Task WaitForReadyAsync()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    HttpResponseMessage response = await AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK) return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            throw new TimeoutException("E2E server did not become ready within 30 seconds." +
                (last != null ? " Last error: " + last.Message : ""));
        }

        private void Shutdown()
        {
            try { AuthClient?.Dispose(); } catch { }
            try { UnauthClient?.Dispose(); } catch { }
            try { McpClient?.Dispose(); } catch { }
            try { _Server?.Stop(); } catch { }
            try
            {
                if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
            }
            catch
            {
                // Best-effort cleanup; a lingering handle must not crash process exit.
            }
        }

        private static int GetAvailablePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        #endregion
    }
}
