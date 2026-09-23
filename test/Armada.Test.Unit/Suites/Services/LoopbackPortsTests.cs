namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using SyslogLogging;
    using global::Test.Shared.Infrastructure;

    /// <summary>
    /// Test servers bind ports that were found free and then released. A port another socket takes in between is
    /// detected by the server's own bind failure and the start is repeated on new ports.
    /// </summary>
    public class LoopbackPortsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Loopback Ports";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("An Admiral whose REST port is taken before it binds starts again on new ports", async () =>
            {
                await AssertTakenPortRecoversAsync(takeRestPort: true).ConfigureAwait(false);
            });

            await RunTest("An Admiral whose MCP port is taken before it binds starts again on new ports", async () =>
            {
                await AssertTakenPortRecoversAsync(takeRestPort: false).ConfigureAwait(false);
            });

            await RunTest("A start failure other than an address in use is not repeated", async () =>
            {
                int attempts = 0;
                int releases = 0;
                InvalidOperationException? thrown = null;
                try
                {
                    await LoopbackPorts.StartAsync(
                        () =>
                        {
                            attempts++;
                            throw new InvalidOperationException("not a bind failure");
                        },
                        () => releases++).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    thrown = ex;
                }

                AssertNotNull(thrown, "the failure propagates");
                AssertEqual(1, attempts, "a failure that is not a lost port is not retried");
                AssertEqual(0, releases, "nothing is released for a failure that propagates");
            });

            await RunTest("A port that stays taken fails after the attempt limit", async () =>
            {
                using (TcpListener holder = new TcpListener(IPAddress.Loopback, 0))
                {
                    holder.Start();
                    int taken = ((IPEndPoint)holder.LocalEndpoint).Port;
                    int attempts = 0;
                    Exception? thrown = null;
                    try
                    {
                        await LoopbackPorts.StartAsync(
                            () =>
                            {
                                attempts++;
                                using (TcpListener second = new TcpListener(IPAddress.Loopback, taken))
                                {
                                    second.Start();
                                }
                                return Task.CompletedTask;
                            },
                            () => { },
                            maxAttempts: 3).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        thrown = ex;
                    }

                    AssertEqual(3, attempts, "every attempt is made before the failure is reported");
                    AssertTrue(LoopbackPorts.IsAddressInUse(thrown), "the reported failure is the bind failure: " + thrown?.Message);
                }
            });
        }

        private async Task AssertTakenPortRecoversAsync(bool takeRestPort)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "armada_loopback_ports_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            ArmadaSettings settings = NewSettings(tempDir);
            ArmadaServer? server = null;
            int attempts = 0;
            try
            {
                using (TcpListener holder = new TcpListener(IPAddress.Loopback, 0))
                {
                    holder.Start();
                    int taken = ((IPEndPoint)holder.LocalEndpoint).Port;

                    await LoopbackPorts.StartAsync(
                        async () =>
                        {
                            attempts++;
                            int restPort = LoopbackPorts.FindFree();
                            int mcpPort = LoopbackPorts.FindFree();
                            // The first attempt is handed a port another socket already holds, as happens
                            // when the port is taken between finding it and the server binding it.
                            if (attempts == 1)
                            {
                                if (takeRestPort) restPort = taken;
                                else mcpPort = taken;
                            }
                            settings.AdmiralPort = restPort;
                            settings.McpPort = mcpPort;
                            LoggingModule logging = new LoggingModule();
                            logging.Settings.EnableConsole = false;
                            server = new ArmadaServer(logging, settings, quiet: true);
                            await server.StartAsync().ConfigureAwait(false);
                        },
                        () =>
                        {
                            server?.Stop();
                            server = null;
                        }).ConfigureAwait(false);

                    AssertEqual(2, attempts, "the start that lost its port is repeated once");
                    AssertFalse(settings.AdmiralPort == taken || settings.McpPort == taken, "the repeated start uses new ports");

                    using (HttpClient client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(10);
                        client.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);
                        HttpResponseMessage response = await client.GetAsync("http://127.0.0.1:" + settings.AdmiralPort + "/api/v1/status/health").ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.OK, response.StatusCode, "the server answers on its new REST port");
                    }
                }
            }
            finally
            {
                server?.Stop();
                try { Directory.Delete(tempDir, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static ArmadaSettings NewSettings(string tempDir)
        {
            DatabaseSettings dbSettings = new DatabaseSettings
            {
                Type = DatabaseTypeEnum.Sqlite,
                Filename = Path.Combine(tempDir, "armada.db")
            };
            ArmadaSettings settings = new ArmadaSettings
            {
                DataDirectory = tempDir,
                DatabasePath = dbSettings.Filename,
                Database = dbSettings,
                LogDirectory = Path.Combine(tempDir, "logs"),
                DocksDirectory = Path.Combine(tempDir, "docks"),
                ReposDirectory = Path.Combine(tempDir, "repos"),
                ApiKey = "test-key-" + Guid.NewGuid().ToString("N"),
                HeartbeatIntervalSeconds = 300
            };
            settings.Rest.Hostname = "127.0.0.1";
            settings.AutonomousObjectiveScheduler.Enabled = false;
            settings.SettingsFilePath = Path.Combine(tempDir, "settings.json");
            settings.InitializeDirectories();
            return settings;
        }
    }
}
