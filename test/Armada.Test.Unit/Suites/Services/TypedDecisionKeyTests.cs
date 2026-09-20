namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Behavioural coverage of the typed-decision key: effective mode without a key, the live client swap when the
    /// key file is written or removed, environment precedence, file permissions, and the key never leaving the store.
    /// </summary>
    public sealed class TypedDecisionKeyTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Typed Decision Key";

        private const string _EnvName = "ARMADA_TYPESAFE_KEY";
        private const string _FileKey = "example-file-key-value";
        private const string _EnvKey = "example-env-key-value";

        private static string TempDataDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_typed_key_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static TypedDecisionSettings Wire(TypedDecisionKeyStore keys)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings();
            settings.KeyAvailable = () => keys.HasKey(settings, out string? _);
            return settings;
        }

        private sealed class RejectingHandler : HttpMessageHandler
        {
            internal string? Authorization;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Authorization = request.Headers.Authorization?.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
            }
        }

        private sealed class ModelHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"model\":\"test-model-version\",\"answers\":{\"ready\":{\"type\":\"noul\",\"noul\":0.9,\"confidence\":0.9}}}")
                });
            }
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A running server registers the evaluation tool with a switchable client", async () =>
            {
                string data = TempDataDirectory();
                int FreePort()
                {
                    System.Net.Sockets.TcpListener listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    listener.Stop();
                    return port;
                }
                ArmadaSettings settings = new ArmadaSettings
                {
                    DataDirectory = data,
                    DatabasePath = Path.Combine(data, "armada.db"),
                    Database = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = Path.Combine(data, "armada.db") },
                    LogDirectory = Path.Combine(data, "logs"),
                    DocksDirectory = Path.Combine(data, "docks"),
                    ReposDirectory = Path.Combine(data, "repos"),
                    AdmiralPort = FreePort(), McpPort = FreePort(),
                    ApiKey = "test-key-" + Guid.NewGuid().ToString("N"),
                    HeartbeatIntervalSeconds = 300
                };
                settings.Rest.Hostname = "127.0.0.1";
                settings.AutonomousObjectiveScheduler.Enabled = false;
                settings.TypedDecisions.Mode = TypedDecisionModeEnum.Off;
                settings.SettingsFilePath = Path.Combine(data, "settings.json");
                settings.InitializeDirectories();
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                Armada.Server.ArmadaServer server = new Armada.Server.ArmadaServer(logging, settings, quiet: true);
                try
                {
                    await server.StartAsync();
                    using HttpClient http = new HttpClient();
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + settings.McpPort + "/mcp");
                    request.Headers.Add("Accept", "application/json, text/event-stream");
                    request.Headers.Add("X-Api-Key", settings.ApiKey);
                    request.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}", System.Text.Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await http.SendAsync(request);
                    string body = await response.Content.ReadAsStringAsync();
                    AssertTrue(response.IsSuccessStatusCode, "tool listing must succeed");
                    AssertContains("armada_typed_decision_eval", body, "manual evaluation stays available with the switchable client, even without provider calls");
                }
                finally
                {
                    server.Stop();
                    try { Directory.Delete(data, true); } catch (IOException) { }
                }
            });

            await RunTest("Model observations survive removing and restoring the provider key", async () =>
            {
                string data = TempDataDirectory();
                try
                {
                    TypedDecisionKeyStore keys = new TypedDecisionKeyStore(data, _ => null);
                    TypedDecisionSettings settings = Wire(keys);
                    using HttpClient http = new HttpClient(new ModelHandler());
                    SwitchableTypedDecisionClient client = new SwitchableTypedDecisionClient(settings, keys, new LoggingModule(), http);
                    List<string> observed = new List<string>();
                    client.ModelObserved = observed.Add;
                    TypedDecisionRequest request = new TypedDecisionRequest
                    {
                        DecisionPoint = "captain_tool", State = "example state",
                        Questions = new Dictionary<string, TypedQuestion> { ["ready"] = new NoulQuestion("Is it ready?", "ready", "not ready") }
                    };
                    AssertFalse((await client.DecideAsync(request, CancellationToken.None)).Available);
                    AssertEqual(0, observed.Count);
                    await keys.WriteKeyAsync(_FileKey);
                    AssertTrue((await client.DecideAsync(request, CancellationToken.None)).Available);
                    AssertEqual("test-model-version", observed.Single());
                    keys.DeleteKeyFile();
                    AssertFalse((await client.DecideAsync(request, CancellationToken.None)).Available);
                    await keys.WriteKeyAsync(_FileKey);
                    AssertTrue((await client.DecideAsync(request, CancellationToken.None)).Available);
                    AssertEqual(2, observed.Count);
                }
                finally { Directory.Delete(data, true); }
            });

            await RunTest("Without a key the effective mode is Off and the null client is used even with stored Gate", () =>
            {
                string data = TempDataDirectory();
                try
                {
                    TypedDecisionKeyStore keys = new TypedDecisionKeyStore(data, _ => null);
                    TypedDecisionSettings settings = Wire(keys);
                    AssertEqual(TypedDecisionModeEnum.Gate, settings.Mode, "stored mode");
                    AssertEqual(TypedDecisionModeEnum.Off, settings.EffectiveMode);
                    AssertEqual(TypedDecisionModeEnum.Off, settings.For("failure_cause").Mode, "a Gate decision is Off without a key");
                    using (HttpClient http = new HttpClient())
                    {
                        SwitchableTypedDecisionClient client = new SwitchableTypedDecisionClient(settings, keys, new LoggingModule(), http);
                        AssertTrue(client.Current is NullTypedDecisionClient, "null client without a key");
                    }
                    TypedDecisionStatus status = TypedDecisionStatusBuilder.Build(settings, keys);
                    AssertEqual(TypedDecisionModeEnum.Off, status.EffectiveMode);
                    AssertEqual(TypedDecisionKeyStore.ReasonNoKey, status.EffectiveReason);
                    AssertFalse(status.KeyPresent);
                    AssertNull(status.KeySource);
                }
                finally { Directory.Delete(data, true); }
            });

            await RunTest("Writing the key file wires the provider client and restores the stored mode without a restart", async () =>
            {
                string data = TempDataDirectory();
                try
                {
                    TypedDecisionKeyStore keys = new TypedDecisionKeyStore(data, _ => null);
                    TypedDecisionSettings settings = Wire(keys);
                    using (HttpClient http = new HttpClient())
                    {
                        SwitchableTypedDecisionClient client = new SwitchableTypedDecisionClient(settings, keys, new LoggingModule(), http);
                        AssertTrue(client.Current is NullTypedDecisionClient);
                        await keys.WriteKeyAsync(_FileKey);
                        AssertTrue(client.Current is TypeSafeDecisionClient, "the same client instance now delegates to the provider");
                        AssertEqual(TypedDecisionModeEnum.Gate, settings.For("failure_cause").Mode);
                        TypedDecisionStatus status = TypedDecisionStatusBuilder.Build(settings, keys);
                        AssertEqual(TypedDecisionModeEnum.Gate, status.EffectiveMode);
                        AssertNull(status.EffectiveReason);
                        AssertEqual(TypedDecisionKeyStore.SourceFile, status.KeySource);
                        AssertTrue(keys.DeleteKeyFile(), "the file is removed");
                        AssertTrue(client.Current is NullTypedDecisionClient, "removing the key switches back");
                        AssertEqual(TypedDecisionModeEnum.Off, settings.For("failure_cause").Mode);
                    }
                }
                finally { Directory.Delete(data, true); }
            });

            await RunTest("The environment variable wins over the key file", async () =>
            {
                string data = TempDataDirectory();
                try
                {
                    Dictionary<string, string?> environment = new Dictionary<string, string?> { [_EnvName] = _EnvKey };
                    TypedDecisionKeyStore keys = new TypedDecisionKeyStore(data, name => environment.TryGetValue(name, out string? value) ? value : null);
                    TypedDecisionSettings settings = Wire(keys);
                    await keys.WriteKeyAsync(_FileKey);
                    AssertEqual(_EnvKey, keys.ResolveKey(settings, out string? source));
                    AssertEqual(TypedDecisionKeyStore.SourceEnvironment, source);
                    keys.DeleteKeyFile();
                    AssertTrue(keys.EnvironmentSuppliesKey(settings), "the variable still supplies a key after the file is removed");
                    AssertEqual(TypedDecisionModeEnum.Gate, settings.EffectiveMode);
                    environment[_EnvName] = null;
                    AssertEqual(TypedDecisionModeEnum.Off, settings.EffectiveMode);
                }
                finally { Directory.Delete(data, true); }
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("The key file is owner-only (folder 0700, file 0600)", "Unix file modes do not apply on Windows.");
            }
            else
            {
                await RunTest("The key file is owner-only (folder 0700, file 0600)", async () =>
                {
                    string data = TempDataDirectory();
                    try
                    {
                        TypedDecisionKeyStore keys = new TypedDecisionKeyStore(data, _ => null);
                        await keys.WriteKeyAsync(_FileKey);
                        AssertEqual(Path.Combine(Path.GetFullPath(data), "secrets", TypedDecisionKeyStore.KeyFileName), keys.KeyFilePath);
                        AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keys.KeyFilePath));
                        AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(keys.KeyFilePath)!));
                        await keys.WriteKeyAsync(_FileKey + "-rotated");
                        AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keys.KeyFilePath), "a replaced key keeps 0600");
                        AssertEqual(1, Directory.GetFiles(Path.GetDirectoryName(keys.KeyFilePath)!).Length, "no temporary file is left behind");
                    }
                    finally { Directory.Delete(data, true); }
                });
            }

            await RunTest("An invalid key is refused and the key never appears in settings, status, or messages", async () =>
            {
                string data = TempDataDirectory();
                try
                {
                    TypedDecisionKeyStore keys = new TypedDecisionKeyStore(data, _ => null);
                    TypedDecisionSettings settings = Wire(keys);
                    await AssertThrowsAsync<ArgumentException>(() => keys.WriteKeyAsync("   "), "blank key");
                    try
                    {
                        await keys.WriteKeyAsync("bad\nkey-" + _FileKey);
                        throw new Exception("Expected a refused key");
                    }
                    catch (ArgumentException ex)
                    {
                        AssertFalse(ex.Message.Contains(_FileKey), "the refusal does not quote the key");
                    }
                    await keys.WriteKeyAsync(_FileKey);
                    ArmadaSettings armada = new ArmadaSettings();
                    armada.TypedDecisions = settings;
                    string serialized = System.Text.Json.JsonSerializer.Serialize(armada);
                    AssertFalse(serialized.Contains(_FileKey), "settings serialization never carries the key");
                    AssertFalse(System.Text.Json.JsonSerializer.Serialize(TypedDecisionStatusBuilder.Build(settings, keys)).Contains(_FileKey), "status never carries the key");
                }
                finally { Directory.Delete(data, true); }
            });

            await RunTest("A provider call sends the file key as the Bearer header and never writes it to the log", async () =>
            {
                string data = TempDataDirectory();
                try
                {
                    TypedDecisionKeyStore keys = new TypedDecisionKeyStore(data, _ => null);
                    TypedDecisionSettings settings = Wire(keys);
                    await keys.WriteKeyAsync(_FileKey);
                    string logPath = Path.Combine(data, "logs", "admiral.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    logging.Settings.FileLogging = FileLoggingMode.SingleLogFile;
                    logging.Settings.LogFilename = logPath;
                    RejectingHandler handler = new RejectingHandler();
                    using (HttpClient http = new HttpClient(handler))
                    {
                        SwitchableTypedDecisionClient client = new SwitchableTypedDecisionClient(settings, keys, logging, http);
                        TypedDecisionResult result = await client.DecideAsync(new TypedDecisionRequest
                        {
                            DecisionPoint = "failure_cause",
                            State = "example state",
                            Questions = new Dictionary<string, TypedQuestion>()
                        }, CancellationToken.None);
                        AssertFalse(result.Available, "a rejected call is unavailable");
                    }
                    AssertEqual("Bearer " + _FileKey, handler.Authorization, "the provider receives the file key");
                    logging.Dispose();
                    string logged = String.Join("\n", Directory.GetFiles(Path.GetDirectoryName(logPath)!).Select(File.ReadAllText));
                    AssertContains("http_401", logged, "the rejected call is logged");
                    AssertFalse(logged.Contains(_FileKey), "the key never reaches the log");
                }
                finally { Directory.Delete(data, true); }
            });

            await RunTest("A hot reload updates the settings instance decision points already hold", () =>
            {
                ArmadaSettings live = new ArmadaSettings();
                TypedDecisionSettings held = live.TypedDecisions;
                ArmadaSettings incoming = new ArmadaSettings();
                incoming.TypedDecisions.Mode = TypedDecisionModeEnum.Shadow;
                incoming.TypedDecisions.Decisions["capacity_escalation"].Mode = TypedDecisionModeEnum.Off;
                live.ApplyHotReloadableFrom(incoming);
                AssertEqual(TypedDecisionModeEnum.Shadow, held.Mode, "an adapter's settings reference sees the reloaded global mode");
                AssertEqual(TypedDecisionModeEnum.Off, held.For("capacity_escalation").Mode);
            });

            await RunTest("Typed-decision routes require settings write permission and are never captured in request history", () =>
            {
                AuthorizationService authz = new AuthorizationService();
                AssertTrue(TypedDecisionRoutes.IsPermitted(new AuthContext { IsAuthenticated = true, IsAdmin = true }, authz), "global administrator");
                AssertFalse(TypedDecisionRoutes.IsPermitted(new AuthContext { IsAuthenticated = true, IsTenantAdmin = true, TenantId = "ten_example" }, authz), "tenant administrator");
                AssertFalse(TypedDecisionRoutes.IsPermitted(new AuthContext { IsAuthenticated = true, TenantId = "ten_example", UserId = "usr_example" }, authz), "ordinary user");
                AssertFalse(TypedDecisionRoutes.IsPermitted(new AuthContext(), authz), "unauthenticated caller");
                ArmadaSettings settings = new ArmadaSettings { RequestHistoryEnabled = true };
                RequestHistoryCaptureService capture = new RequestHistoryCaptureService(settings);
                AssertFalse(capture.ShouldCapture("/api/v1/typed-decisions/key"));
                AssertFalse(capture.ShouldCapture("/api/v1/typed-decisions"));
                AssertTrue(capture.ShouldCapture("/api/v1/settings"), "other routes are still captured");
            });
        }
    }
}
