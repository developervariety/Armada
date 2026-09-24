namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Server.Routes;
    using Armada.Server.WebSocket;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// One database served through all three operator surfaces the way the server wires them: the REST routes on a
    /// loopback webserver with an administrator key, the WebSocket command handler, and the registered MCP tools. The
    /// harness records every event written and every mission or voyage change broadcast, and every captain recall,
    /// so a parity test can drive one input through each surface and compare what each one changed.
    /// </summary>
    public sealed class SurfaceParityHarness : IDisposable
    {
        #region Public-Members

        /// <summary>Test database.</summary>
        public TestDatabase Database { get; }

        /// <summary>Database driver.</summary>
        public DatabaseDriver Driver => Database.Driver;

        /// <summary>Settings with private log, dock and repository directories.</summary>
        public ArmadaSettings Settings { get; }

        /// <summary>Git service double.</summary>
        public StubGitService Git { get; }

        /// <summary>Dock service over the test database.</summary>
        public IDockService Docks { get; }

        /// <summary>Merge queue service over the test database.</summary>
        public IMergeQueueService MergeQueue { get; }

        /// <summary>Admiral double that records captain recalls.</summary>
        public ParityAdmiral Admiral { get; }

        /// <summary>The shared mission and voyage operations every surface is given.</summary>
        public MissionOperations Operations { get; }

        /// <summary>Events the surfaces emitted, in order.</summary>
        public ConcurrentQueue<ArmadaEvent> Events { get; } = new ConcurrentQueue<ArmadaEvent>();

        /// <summary>Change broadcasts the surfaces sent, as <c>kind:id:status</c>, in order.</summary>
        public ConcurrentQueue<string> Broadcasts { get; } = new ConcurrentQueue<string>();

        /// <summary>WebSocket command handler.</summary>
        public WebSocketCommandHandler WebSocket { get; }

        /// <summary>Registered MCP tool handlers by name.</summary>
        public Dictionary<string, Func<JsonElement?, Task<object>>> Mcp { get; } = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);

        /// <summary>REST client sending the administrator key.</summary>
        public HttpClient Rest { get; }

        /// <summary>The global administrator every surface acts as.</summary>
        public AuthContext Caller => McpTestCaller.Operator;

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _RestJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        private static readonly JsonSerializerOptions _WebSocketJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly Webserver _Server;
        private readonly CancellationTokenSource _Cancellation = new CancellationTokenSource();
        private readonly string _Root;

        #endregion

        #region Constructors-and-Factories

        private SurfaceParityHarness(TestDatabase database)
        {
            Database = database;
            _Root = Path.Combine(Path.GetTempPath(), "armada_parity_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_Root);

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            string apiKey = "parity-key-" + Guid.NewGuid().ToString("N");
            Settings = new ArmadaSettings();
            Settings.ApiKey = apiKey;
            Settings.LogDirectory = Path.Combine(_Root, "logs");
            Settings.DocksDirectory = Path.Combine(_Root, "docks");
            Settings.ReposDirectory = Path.Combine(_Root, "repos");
            // Each parity case restarts or dispatches one mission per surface; the fleet capacity gate is not under test.
            Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 100;
            Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 100;
            Directory.CreateDirectory(Path.Combine(Settings.LogDirectory, "missions"));
            Directory.CreateDirectory(Path.Combine(Settings.LogDirectory, "captains"));
            Directory.CreateDirectory(Path.Combine(Settings.LogDirectory, "diffs"));

            Git = new StubGitService();
            Docks = new DockService(logging, Driver, Settings, Git);
            MergeQueue = new MergeQueueService(logging, Driver, Settings, Git, new MergeFailureClassifier());
            Admiral = ParityAdmiral.Create(Driver);

            Func<string, string, string?, string?, string?, string?, string?, string?, Task> emitEvent = EmitEventAsync;
            OperationNotifier notifier = new OperationNotifier(
                emitEvent,
                mission => Broadcasts.Enqueue("mission:" + mission.Id + ":" + mission.Status),
                voyage => Broadcasts.Enqueue("voyage:" + voyage.Id + ":" + voyage.Status));
            Operations = new MissionOperations(Driver, Settings, Docks, Admiral.Service.RecallCaptainAsync, notifier, logging);

            ICaptainService captains = new CaptainService(logging, Driver, Settings, Git, Docks);
            MissionService missionService = new MissionService(logging, Driver, Settings, Docks, captains,
                resourcePressureAdmission: TestResourcePressure.Unconstrained(Settings));
            MissionStatusTransitionService transitions = new MissionStatusTransitionService(
                Driver, Admiral.Service, missionService, Git,
                (_, _) => Task.FromResult(false),
                (_, _) => Task.CompletedTask,
                emitEvent, logging);
            LandingService landing = new LandingService(logging, Driver, Settings, Git);

            WebSocket = new WebSocketCommandHandler(
                Admiral.Service, Driver, MergeQueue, Settings, Git, null, _WebSocketJsonOptions,
                mission => Broadcasts.Enqueue("mission:" + mission.Id + ":" + mission.Status),
                voyage => Broadcasts.Enqueue("voyage:" + voyage.Id + ":" + voyage.Status),
                transitions);
            WebSocket.Operations = Operations;

            AgentRuntimeFactory runtimeFactory = new AgentRuntimeFactory(logging);
            AgentLifecycleHandler lifecycle = new AgentLifecycleHandler(
                logging, Driver, Settings, runtimeFactory, Admiral.Service, new MessageTemplateService(logging),
                null, null, emitEvent);

            McpToolRegistrar.RegisterAll(
                (name, _, _, handler) => { Mcp[name] = McpTestCaller.Wrap(handler); },
                Driver,
                Admiral.Service,
                Settings,
                Git,
                MergeQueue,
                Docks,
                landing,
                null,
                null,
                lifecycle,
                null,
                logging,
                missionService: missionService,
                statusTransitions: transitions,
                missionOperations: Operations);

            WorkflowProfileService workflowProfiles = new WorkflowProfileService(Driver, logging);
            VesselReadinessService readiness = new VesselReadinessService(Driver, workflowProfiles, logging);
            CheckRunService checkRuns = new CheckRunService(Driver, workflowProfiles, readiness, logging);
            DeploymentEnvironmentService environments = new DeploymentEnvironmentService(Driver, workflowProfiles, logging);
            DeploymentService deployments = new DeploymentService(Driver, workflowProfiles, environments, checkRuns, logging);
            ObjectiveService objectives = new ObjectiveService(Driver);
            GitHubIntegrationService gitHub = new GitHubIntegrationService(Driver, objectives, checkRuns, deployments, Settings, logging);
            LandingPreviewService landingPreview = new LandingPreviewService(Driver, logging, Settings);
            AuthenticationService authentication = new AuthenticationService(Driver, new SessionTokenService(), Settings, logging);

            int port = ReservePort();
            WebserverSettings webserverSettings = new WebserverSettings();
            webserverSettings.Hostname = "127.0.0.1";
            webserverSettings.Port = port;
            _Server = new Webserver(webserverSettings, async (HttpContextBase ctx) =>
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.Send().ConfigureAwait(false);
            });

            Func<HttpContextBase, Task<AuthContext>> authenticate = ctx => authentication.AuthenticateAsync(
                ctx.Request.Headers.Get("Authorization"),
                ctx.Request.Headers.Get("X-Token"),
                ctx.Request.Headers.Get("X-Api-Key"));
            AuthorizationService authz = new AuthorizationService();

            new MissionRoutes(Driver, Admiral.Service, missionService, Settings, Git, landing, landingPreview, gitHub,
                emitEvent, null, logging, _RestJsonOptions, transitions, Operations).Register(_Server, authenticate, authz);
            new VoyageRoutes(Driver, Admiral.Service, emitEvent, null, logging, objectives, null, Settings, _RestJsonOptions,
                operations: Operations).Register(_Server, authenticate, authz);
            new MergeQueueRoutes(Driver, MergeQueue, emitEvent, _RestJsonOptions).Register(_Server, authenticate, authz);
            new CaptainRoutes(Driver, Admiral.Service, Settings, runtimeFactory, lifecycle, new CaptainToolService(logging, Driver, Settings),
                emitEvent, _RestJsonOptions, null, null, logging).Register(_Server, authenticate, authz);
            new VesselRoutes(Driver, readiness, landingPreview, emitEvent, _RestJsonOptions, Docks)
                .Register(_Server, authenticate, authz);

            _Server.Start(_Cancellation.Token);
            Rest = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port + "/"), Timeout = TimeSpan.FromSeconds(20) };
            Rest.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        /// <summary>Start a harness over a fresh test database.</summary>
        /// <returns>The running harness.</returns>
        public static async Task<SurfaceParityHarness> StartAsync()
        {
            TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            return new SurfaceParityHarness(database);
        }

        #endregion

        #region Public-Methods

        /// <summary>Send a REST request and return its status code and body.</summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="path">Route path.</param>
        /// <param name="body">Optional JSON body object.</param>
        /// <returns>Status and body.</returns>
        public async Task<SurfaceReply> RestAsync(System.Net.Http.HttpMethod method, string path, object? body = null)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(method, path))
            {
                if (body != null)
                    request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await Rest.SendAsync(request).ConfigureAwait(false))
                {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new SurfaceReply("REST", (int)response.StatusCode, text);
                }
            }
        }

        /// <summary>Run a WebSocket command as the global administrator.</summary>
        /// <param name="action">Command name.</param>
        /// <param name="id">Command id field.</param>
        /// <param name="data">Optional data object.</param>
        /// <param name="lines">Optional page size.</param>
        /// <param name="offset">Optional page offset.</param>
        /// <returns>The serialized result.</returns>
        public async Task<SurfaceReply> WebSocketAsync(string action, string? id, object? data = null, int? lines = null, int? offset = null)
        {
            string rawBody = JsonSerializer.Serialize(new { route = "command", action = action, id = id, data = data, lines = lines, offset = offset });
            WebSocketCommand command = new WebSocketCommand { Action = action, Id = id, Lines = lines, Offset = offset };
            object result = await WebSocket.HandleCommandAsync(action, command, rawBody, Caller).ConfigureAwait(false);
            string text = JsonSerializer.Serialize(result, _WebSocketJsonOptions);
            return new SurfaceReply("WebSocket", text.Contains("\"command.error\"", StringComparison.Ordinal) ? 409 : 200, text);
        }

        /// <summary>Call an MCP tool as the global administrator.</summary>
        /// <param name="tool">Tool name.</param>
        /// <param name="args">Arguments object.</param>
        /// <returns>The serialized result.</returns>
        public async Task<SurfaceReply> McpAsync(string tool, object args)
        {
            if (!Mcp.TryGetValue(tool, out Func<JsonElement?, Task<object>>? handler))
                throw new InvalidOperationException("MCP tool not registered: " + tool);
            object result = await handler(JsonSerializer.SerializeToElement(args)).ConfigureAwait(false);
            string text = JsonSerializer.Serialize(result, _WebSocketJsonOptions);
            bool error = text.Contains("\"error\":", StringComparison.OrdinalIgnoreCase) && !text.Contains("\"error\":null", StringComparison.OrdinalIgnoreCase);
            return new SurfaceReply("MCP", error ? 409 : 200, text);
        }

        /// <summary>The event types recorded so far, in order.</summary>
        /// <returns>Event types.</returns>
        public List<string> EventTypes()
        {
            return Events.Select(evt => evt.EventType + ":" + (evt.EntityId ?? "")).ToList();
        }

        /// <summary>Forget recorded events, broadcasts and recalls before the next surface runs.</summary>
        public void ResetRecordings()
        {
            while (Events.TryDequeue(out _)) { }
            while (Broadcasts.TryDequeue(out _)) { }
            Admiral.Recalled.Clear();
        }

        /// <summary>Write a file under the log directory.</summary>
        /// <param name="relativePath">Path below the log directory.</param>
        /// <param name="content">File content.</param>
        /// <returns>Full path.</returns>
        public string WriteLogFile(string relativePath, string content)
        {
            string path = Path.Combine(Settings.LogDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>A directory under this harness's private root.</summary>
        /// <param name="name">Directory name.</param>
        /// <returns>Full path, created.</returns>
        public string MakeDirectory(string name)
        {
            string path = Path.Combine(_Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>Dispose the harness.</summary>
        public void Dispose()
        {
            Rest.Dispose();
            _Cancellation.Cancel();
            try { _Server.Stop(); }
            catch (Exception exception) { Console.WriteLine("Parity harness server stop failed: " + exception.Message); }
            _Server.Dispose();
            _Cancellation.Dispose();
            Database.Dispose();
            try { Directory.Delete(_Root, true); }
            catch (Exception exception) { Console.WriteLine("Parity harness cleanup failed: " + exception.Message); }
        }

        #endregion

        #region Private-Methods

        private async Task EmitEventAsync(string eventType, string message, string? entityType, string? entityId,
            string? captainId, string? missionId, string? vesselId, string? voyageId)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message);
            evt.EntityType = entityType;
            evt.EntityId = entityId;
            evt.CaptainId = captainId;
            evt.MissionId = missionId;
            evt.VesselId = vesselId;
            evt.VoyageId = voyageId;
            Events.Enqueue(evt);
            await Driver.Events.CreateAsync(evt).ConfigureAwait(false);
        }

        private static int ReservePort()
        {
            using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }

        #endregion
    }

    /// <summary>What one surface answered.</summary>
    public sealed class SurfaceReply
    {
        /// <summary>Surface name.</summary>
        public string Surface { get; }

        /// <summary>HTTP status for REST; 200 or 409 for a WebSocket result or error and an MCP result or error.</summary>
        public int Status { get; }

        /// <summary>Serialized reply.</summary>
        public string Body { get; }

        /// <summary>True when the surface refused.</summary>
        public bool Refused => Status >= 400;

        /// <summary>Instantiate.</summary>
        public SurfaceReply(string surface, int status, string body)
        {
            Surface = surface;
            Status = status;
            Body = body;
        }

        /// <inheritdoc />
        public override string ToString() => Surface + " " + Status + ": " + (Body.Length > 600 ? Body.Substring(0, 600) + "..." : Body);
    }

    /// <summary>
    /// An admiral double whose recall behaves like the real one on the database: the captain's running mission is
    /// failed, and the captain is released. Every recall is recorded.
    /// </summary>
    public class ParityAdmiral : DispatchProxy
    {
        private DatabaseDriver? _Database;

        /// <summary>Captain ids recalled, in order.</summary>
        public ConcurrentQueue<string> Recalled { get; } = new ConcurrentQueue<string>();

        /// <summary>The double as an admiral service.</summary>
        public IAdmiralService Service => (IAdmiralService)(object)this;

        /// <summary>Create a double over a database.</summary>
        /// <param name="database">Database driver.</param>
        /// <returns>The double.</returns>
        public static ParityAdmiral Create(DatabaseDriver database)
        {
            ParityAdmiral admiral = (ParityAdmiral)(object)DispatchProxy.Create<IAdmiralService, ParityAdmiral>();
            admiral._Database = database;
            return admiral;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;
            if (targetMethod.Name == nameof(IAdmiralService.RecallCaptainAsync) && args != null && args.Length >= 1)
                return RecallAsync((string)args[0]!);

            Type returnType = targetMethod.ReturnType;
            if (returnType == typeof(void)) return null;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                Type resultType = returnType.GenericTypeArguments[0];
                object? result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, new[] { result });
            }
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }

        private async Task RecallAsync(string captainId)
        {
            Recalled.Enqueue(captainId);
            Captain? captain = await _Database!.Captains.ReadAsync(captainId).ConfigureAwait(false);
            if (captain == null) throw new InvalidOperationException("Captain not found: " + captainId);
            if (!String.IsNullOrEmpty(captain.CurrentMissionId))
            {
                Mission? current = await _Database.Missions.ReadAsync(captain.CurrentMissionId).ConfigureAwait(false);
                if (current != null && (current.Status == MissionStatusEnum.InProgress || current.Status == MissionStatusEnum.Assigned))
                {
                    current.Status = MissionStatusEnum.Failed;
                    current.ProcessId = null;
                    current.CompletedUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(current).ConfigureAwait(false);
                }
            }
            captain.State = CaptainStateEnum.Idle;
            captain.CurrentMissionId = null;
            captain.CurrentDockId = null;
            captain.ProcessId = null;
            await _Database.Captains.UpdateAsync(captain).ConfigureAwait(false);
        }
    }
}
