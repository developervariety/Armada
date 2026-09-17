namespace Armada.Server
{
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using SyslogLogging;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Core;
    using Armada.Core.Context;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.TypedDecisions;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Server.Routes;
    using Armada.Server.WebSocket;

    /// <summary>
    /// Admiral server orchestrating REST API, MCP server, and agent coordination.
    /// Routes, MCP tools, WebSocket commands, agent lifecycle, and mission landing
    /// are each handled by dedicated classes — this class wires them together.
    /// </summary>
    public class ArmadaServer
    {
        #region Public-Members

        /// <summary>
        /// Callback invoked when the server is stopping, allowing the host to unblock.
        /// </summary>
        public Action? OnStopping { get; set; }

        /// <summary>
        /// Self-deploy service, available after <see cref="StartAsync"/> wires it.
        /// </summary>
        public SelfDeployService? SelfDeploy { get; private set; }

        /// <summary>
        /// Delay between health loop ticks. Null, the default, uses <see cref="ArmadaSettings.HeartbeatIntervalSeconds"/>,
        /// whose settings floor is five seconds; an in-process host can set a shorter positive interval. Read at
        /// each tick.
        /// </summary>
        public TimeSpan? HealthLoopInterval
        {
            get => _HealthLoopInterval;
            set
            {
                if (value.HasValue && value.Value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(HealthLoopInterval), "Must be positive.");
                _HealthLoopInterval = value;
            }
        }

        #endregion

        #region Private-Members

        private string _Header = "[ArmadaServer] ";
        private LoggingModule _Logging;
        private ArmadaSettings _Settings;
        private bool _Quiet;

        private DatabaseDriver _Database = null!;
        private IGitService _Git = null!;
        private IDockService _Docks = null!;
        private IAdmiralService _Admiral = null!;
        private IBuildDriftService _BuildDriftService = null!;
        private ICaptainQuarantineService _CaptainQuarantine = null!;
        private AgentRuntimeFactory _RuntimeFactory = null!;
        private AgentRuntimeFactory? _SuppliedRuntimeFactory;
        private SettingsFileWatcher? _SettingsWatcher;

        private Webserver _App = null!;

        /// <summary>
        /// Routes registered on the REST listener after start. Contract tests read this table so
        /// published examples are checked against what the Admiral actually serves.
        /// </summary>
        internal WatsonWebserver.Core.Routing.WebserverRoutes RestRoutes => _App.Routes;
        private ArmadaMcpHttpServer _McpServer = null!;
        private Armada.Core.Services.HarborJobService? _HarborJobService = null;
        private ArmadaWebSocketHub _WebSocketHub = null!;
        private MissionStatusTransitionService _StatusTransitions = null!;

        private IMergeQueueService _MergeQueue = null!;
        private Armada.Core.Services.JobService _JobService = null!;
        private IMergeRecoveryHandler _MergeRecoveryHandler = null!;
        private IAutoLandEvaluator _AutoLandEvaluator = null!;
        private IConventionChecker _ConventionChecker = null!;
        private ICriticalTriggerEvaluator _CriticalTriggerEvaluator = null!;
        private LandingService _LandingService = null!;
        private IMessageTemplateService _TemplateService = null!;
        private IPromptTemplateService _PromptTemplateService = null!;
        private ICodeIndexService _CodeIndex = null!;
        private PersonaSeedService _PersonaSeedService = null!;
        private LogRotationService _LogRotation = null!;
        private DataExpiryService _DataExpiry = null!;
        private DiskLifecycleService _DiskLifecycle = null!;
        private ArmadaTelemetryHost? _TelemetryHost = null;
        private BranchCleanupSweepService _BranchCleanupSweep = null!;
        private TerminalVoyageMissionReconciler _TerminalVoyageMissions = null!;
        private OpenCodeServerLauncher _OpenCodeServerLauncher = null!;
        private RemoteTunnelManager _RemoteTunnel = null!;
        private AccountLoginService? _AccountLogins;
        private RemoteDashboardRelayService _RemoteDashboardRelay = null!;
        private PlanningSessionCoordinator _PlanningSessions = null!;
        private ObjectiveRefinementCoordinator _ObjectiveRefinementSessions = null!;
        private CoordinationService _CoordinationService = null!;
        private Armada.Core.Services.DispatchHold _DispatchHold = null!;
        private IWorkspaceService _Workspace = null!;
        private RequestHistoryCaptureService _RequestHistoryCapture = null!;
        private WorkflowProfileService _WorkflowProfileService = null!;
        private VesselReadinessService _VesselReadinessService = null!;
        private DeploymentEnvironmentService _EnvironmentService = null!;
        private CheckRunService _CheckRunService = null!;
        private ObjectiveService _ObjectiveService = null!;
        private ObjectiveDispatchPreviewService _ObjectiveDispatchPreviewService = null!;
        private ReleaseService _ReleaseService = null!;
        private ReleaseWebhookDispatcher? _ReleaseWebhookDispatcher = null;
        private DeploymentService _DeploymentService = null!;
        private IncidentService _IncidentService = null!;
        private RunbookService _RunbookService = null!;
        private AutomaticCheckRunOrchestrator _AutomaticCheckRuns = null!;
        private AutonomousRecoveryOrchestrator _AutonomousRecovery = null!;
        // Typed-decision foundation (TypeSafe Jev): the switchable client every adapter holds, the key
        // store it resolves the provider key from, and the event recorder.
        private ITypedDecisionClient _TypedDecisionClient = new NullTypedDecisionClient();
        private TypedDecisionKeyStore _TypedDecisionKeys = null!;
        private TypedDecisionRecorder _TypedDecisionRecorder = null!;
        private TypedDecisionSampleStore _TypedDecisionSamples = null!;
        private TypedDecisionEvalService? _TypedDecisionEval = null;
        private HttpClient _TypedDecisionHttpClient = null!;
        // The context retrieval service over the built context index (manifest chunks plus bodies).
        // Built in-process at MCP registration from AI-Memory and the docs tree; feeds the
        // read-only captain context fetch tool. Additive; touches no loader and no brief.
        private Armada.Core.Context.ContextRetrievalService? _ContextRetrieval;
        private PapercutMergeAdapter _PapercutMergeAdapter = null!;
        // D13 owner_digest scheduled runner. Constructed only with the live typed-decision client and
        // dormant until the owner_digest decision is enabled; the health loop drives it once per day.
        private OwnerDigestRunner? _OwnerDigestRunner = null;
        private PapercutMemorySweepRunner? _PapercutMemorySweepRunner = null;
        // D11 inbox_triage and D12 followup_routing adapters. Null until the live typed-decision client
        // exists; the inbox/coordination and audit MCP tools receive them through the registrar.
        private InboxTriageAdapter? _InboxTriageAdapter;
        private FollowUpRoutingAdapter? _FollowUpRoutingAdapter;
        private Armada.Core.Services.Interfaces.IFollowUpRouter? _FollowUpRouter;
        private TypedChangeQualityAdapter? _ChangeQualityAdapter;
        private LongRunningJobService _LongRunningJobs = new LongRunningJobService();
        private ProviderProgressTracker _ProviderProgress = new ProviderProgressTracker();
        private TerminalMarkerTracker _TerminalMarkers = new TerminalMarkerTracker();
        private AutonomousObjectiveScheduler _ObjectiveScheduler = null!;
        private IncidentLifecycleOrchestrator _IncidentLifecycle = null!;
        private GitHubIntegrationService _GitHubIntegrationService = null!;
        private LandingPreviewService _LandingPreviewService = null!;
        private HistoricalTimelineService _HistoricalTimelineService = null!;
        private ModelEndpointService _ModelEndpointService = null!;

        private ISessionTokenService _SessionTokenService = null!;
        private IAuthenticationService _AuthenticationService = null!;
        private IAuthorizationService _AuthorizationService = null!;
        private IMissionService _MissionService = null!;
        private CaptainToolService _CaptainTools = null!;

        private AgentLifecycleHandler _AgentLifecycle = null!;
        private MissionLandingHandler _MissionLanding = null!;
        private IRemoteTriggerService _RemoteTriggerService = null!;
        private IAgentWakeProcessHost _AgentWakeProcessHost = null!;

        private CancellationTokenSource _TokenSource = new CancellationTokenSource();
        private Task _HealthCheckTask = null!;
        private Task _ModelEndpointHealthTask = null!;
        private int _HealthCheckCycles = 0;
        private TimeSpan? _HealthLoopInterval = null;
        private DateTime _StartUtc = DateTime.UtcNow;
        private readonly ConditionalWeakTable<HttpContextBase, AuthContext> _RequestAuthContexts = new ConditionalWeakTable<HttpContextBase, AuthContext>();

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="quiet">Suppress startup console output.</param>
        public ArmadaServer(LoggingModule logging, ArmadaSettings settings, bool quiet = false)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Quiet = quiet;
        }

        /// <summary>
        /// Instantiate with the runtime factory every captain launch, stop, liveness probe, chat, planning and
        /// validation run uses. The Admiral builds its own factory from settings when none is supplied.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="runtimeFactory">Runtime factory to use instead of the settings-built one.</param>
        /// <param name="quiet">Suppress startup console output.</param>
        public ArmadaServer(LoggingModule logging, ArmadaSettings settings, AgentRuntimeFactory runtimeFactory, bool quiet = false)
            : this(logging, settings, quiet)
        {
            _SuppliedRuntimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Start the Admiral server.
        /// </summary>
        public async Task StartAsync()
        {
            // Initialize database
            _Database = DatabaseDriverFactory.Create(_Settings.Database, _Logging);
            await _Database.InitializeAsync().ConfigureAwait(false);
            _Logging.Info(_Header + "database initialized");

            // Initialize services
            // PR-fallback wiring: per-call factory selects the right platform CLI (gh / glab).
            // Path defaults match the design's Configuration table — env var override
            // (ARMADA_GH_PATH / ARMADA_GLAB_PATH), else discover via PATH.
            string ghCliPath = Environment.GetEnvironmentVariable("ARMADA_GH_PATH") ?? "gh";
            string glabCliPath = Environment.GetEnvironmentVariable("ARMADA_GLAB_PATH") ?? "glab";
            Func<PullRequestPlatform, string, IPullRequestService> prServiceFactory = (platform, workingDir) =>
            {
                return platform switch
                {
                    PullRequestPlatform.GitHub => new GhPullRequestService(ghCliPath, workingDir),
                    PullRequestPlatform.GitLab => new GlabPullRequestService(glabCliPath, workingDir),
                    _ => throw new NotSupportedException("Unsupported PR platform: " + platform)
                };
            };
            _Git = new GitService(_Logging, prServiceFactory);
            IDockService dockService = new DockService(_Logging, _Database, _Settings, _Git);
            _Docks = dockService;
            ICaptainService captainService = new CaptainService(_Logging, _Database, _Settings, _Git, dockService);
            // Prompt template service must be created before MissionService so it can resolve templates
            _PromptTemplateService = new PromptTemplateService(_Database, _Logging, _Settings.AdditionalPromptTemplates);
            HttpClient codeIndexHttpClient = new HttpClient();
            foreach (string warning in BuildCodeIndexConfigurationWarnings(_Settings.CodeIndex))
            {
                _Logging.Warn(_Header + warning);
            }

            IEmbeddingClient embeddingClient = await EmbeddingClientFactory.CreateAsync(_Settings, _Database, _Logging, codeIndexHttpClient).ConfigureAwait(false);
            _OpenCodeServerLauncher = new OpenCodeServerLauncher(_Settings, _Logging, codeIndexHttpClient);
            IInferenceClient inferenceClient = string.Equals(_Settings.CodeIndex.InferenceClient, "OpenCodeServer", StringComparison.OrdinalIgnoreCase)
                ? new OpenCodeServerInferenceClient(_Settings, _Logging, codeIndexHttpClient)
                : new DeepSeekInferenceClient(_Settings.CodeIndex, _Logging, codeIndexHttpClient);
            _CodeIndex = new CodeIndexService(_Logging, _Database, _Settings, _Git, embeddingClient, inferenceClient);

            // Typed-decision foundation. Every decision point holds one switchable client: it calls the
            // provider while a key resolves (the environment variable, else the key file in the data
            // directory) and the null client otherwise, so a key added or removed through the API takes
            // effect without a restart. Without a key the effective global mode is Off, so no decision
            // calls a client or records an event. The recorder writes only its own events.
            _TypedDecisionHttpClient = new HttpClient();
            // Retention of redacted decision state on this host, as the training and evaluation set for
            // a local classifier (owner ruling 2026-09-17). Off until typedDecisions.retention.enabled
            // AND the decision's own retainState, and read live so a change needs no restart. The store
            // never leaves the host, and the event payload still carries only the state's hash.
            _TypedDecisionSamples = new TypedDecisionSampleStore(_Settings.DataDirectory, _Logging);
            _TypedDecisionRecorder = new TypedDecisionRecorder(
                _Database, _Logging, _TypedDecisionSamples, () => _Settings.TypedDecisions);
            if (_Settings.TypedDecisions.Retention.Enabled)
            {
                int pruned = _TypedDecisionSamples.Prune(_Settings.TypedDecisions.Retention.RetentionDays);
                if (pruned > 0) _Logging.Info(_Header + "typed-decision samples: pruned " + pruned + " file(s) outside the retention window");
            }
            _TypedDecisionKeys = new TypedDecisionKeyStore(_Settings.DataDirectory);
            _Settings.TypedDecisions.KeyAvailable = () => _TypedDecisionKeys.HasKey(_Settings.TypedDecisions, out string? _);
            _TypedDecisionClient = new SwitchableTypedDecisionClient(_Settings.TypedDecisions, _TypedDecisionKeys, _Logging, _TypedDecisionHttpClient);
            TypedDecisionStatus typedDecisionStatus = TypedDecisionStatusBuilder.Build(_Settings.TypedDecisions, _TypedDecisionKeys);
            _Logging.Info(_Header + "typed decisions: effective mode " + typedDecisionStatus.EffectiveMode
                + (typedDecisionStatus.KeyPresent
                    ? " (stored mode " + typedDecisionStatus.StoredMode + ", key from " + typedDecisionStatus.KeySource + ")"
                    : " (" + TypedDecisionKeyStore.ReasonNoKey + "; stored mode " + typedDecisionStatus.StoredMode + ")"));

            // The synthetic evaluation set runs against the live client only: on operator request, and
            // in the background whenever the provider reports a model version not yet evaluated.
            if (_TypedDecisionClient is TypeSafeDecisionClient typeSafeClient)
            {
                _TypedDecisionEval = new TypedDecisionEvalService(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Database, _Logging);
                typeSafeClient.ModelObserved = _TypedDecisionEval.ObserveModel;
            }

            // D6 papercut_merge adapter. Reads the client above, so it is a no-op (the plain grouping)
            // whenever the null client is in use or the decision is off.
            _PapercutMergeAdapter = new PapercutMergeAdapter(
                _Settings.TypedDecisions, _TypedDecisionClient, _TypedDecisionRecorder, _Database, _Logging);

            if (_Settings.CodeIndex.Enabled)
            {
                ScheduleStartupBaselineCacheWarmup(_CodeIndex);
                await _OpenCodeServerLauncher.StartAsync(_TokenSource.Token).ConfigureAwait(false);
            }
            else
            {
                _Logging.Info(_Header + "code-index startup work skipped: code indexing disabled");
            }

            CaptainQuarantineService captainQuarantineService = new CaptainQuarantineService(_Database, _Settings, _Logging, new ProviderResetQuotaProbe());
            // Held as a field so the bench/unbench MCP tools mutate the same instance the
            // dispatcher consults, keeping an operator bench effective without a restart.
            _CaptainQuarantine = captainQuarantineService;
            // Shared resource-pressure admission instance so the dispatcher's pre-launch gate
            // and the process-exit OOM classification see the same capacity/cooldown state.
            ResourcePressureAdmission resourcePressureAdmission = new ResourcePressureAdmission(
                _Settings.ResourcePressureAdmission, new HostResourcePressureProbe(), _Logging);
            // The context retrieval service is built later, in RegisterMcpTools, and held in
            // _ContextRetrieval. A lazy provider lets MissionService reach it at brief-generation
            // time (well after startup) without reordering startup; it stays null until built,
            // and the brief-slimming path (default off) falls back to the full memory section.
            MissionService missionService = new MissionService(_Logging, _Database, _Settings, dockService, captainService, _PromptTemplateService, _Git, captainQuarantineService, resourcePressureAdmission, () => _ContextRetrieval);
            _MissionService = missionService;
            IVoyageService voyageService = new VoyageService(_Logging, _Database);
            IEscalationService escalationService = new EscalationService(_Logging, _Database, _Settings);
            _BuildDriftService = new BuildDriftService(_Git, _Database, BuildInfo.RunningCommit, _Logging);
            _DispatchHold = new Armada.Core.Services.DispatchHold();
            AdmiralService admiralService = new AdmiralService(_Logging, _Database, _Settings, captainService, missionService, voyageService, dockService, escalationService, _BuildDriftService, captainQuarantineService, resourcePressureAdmission, null, _DispatchHold);
            _Admiral = admiralService;
            IMergeFailureClassifier mergeFailureClassifier = new MergeFailureClassifier();
            _MergeQueue = new MergeQueueService(_Logging, _Database, _Settings, _Git, mergeFailureClassifier, prServiceFactory, _CodeIndex);
            _JobService = new Armada.Core.Services.JobService(_Database, _Logging);

            // Auto-recovery wiring: classifier -> router -> handler. The handler reads
            // the persisted classification on a Failed entry and routes to redispatch,
            // rebase-captain (via the dock setup), or surface (which re-pokes the
            // PR-fallback path for recovery_exhausted).
            IRecoveryRouter recoveryRouter = new RecoveryRouter(_Settings.MaxRecoveryAttempts);
            IRebaseCaptainDockSetup rebaseDockSetup = new RebaseCaptainDockSetup(_Git, _Database, _Logging);
            IPlaybookService recoveryPlaybookService = new PlaybookService(_Database, _Logging);
            IMergeRecoveryHandler mergeRecoveryHandler = new MergeRecoveryHandler(
                _Logging, _Database, _Settings, recoveryRouter, rebaseDockSetup, _MergeQueue, recoveryPlaybookService);
            _MergeRecoveryHandler = mergeRecoveryHandler;
            ((MergeQueueService)_MergeQueue).SetRecoveryHandler(_MergeRecoveryHandler);
            SelfDeployService selfDeployService = new SelfDeployService(
                _Logging,
                _Database,
                _Settings,
                _Git,
                new SelfDeployBuildRunner(_Logging),
                SelfDeployCutoverComponents.CreateDefault(_Settings.DataDirectory, _Settings.SelfDeploy),
                () =>
                {
                    Stop();
                    Environment.Exit(0);
                });
            ((MergeQueueService)_MergeQueue).SetSelfDeployService(selfDeployService);
            SelfDeploy = selfDeployService;
            _AutoLandEvaluator = new AutoLandEvaluator();
            _ConventionChecker = new ConventionChecker();
            _CriticalTriggerEvaluator = new CriticalTriggerEvaluator();
            _LandingService = new LandingService(_Logging, _Database, _Settings, _Git);
            _TemplateService = new MessageTemplateService(_Logging, _PromptTemplateService);
            _RuntimeFactory = _SuppliedRuntimeFactory
                ?? new AgentRuntimeFactory(_Logging, _Settings.CodeIndex.OpenCodeServer, _Settings.ModelProviders);
            _Workspace = new WorkspaceService();
            _RequestHistoryCapture = new RequestHistoryCaptureService(_Settings);
            _WorkflowProfileService = new WorkflowProfileService(_Database, _Logging);
            _VesselReadinessService = new VesselReadinessService(_Database, _WorkflowProfileService, _Logging);
            _EnvironmentService = new DeploymentEnvironmentService(_Database, _WorkflowProfileService, _Logging);
            _CheckRunService = new CheckRunService(_Database, _WorkflowProfileService, _VesselReadinessService, _Logging, () => _Settings.BannedDiffPatterns);
            _ObjectiveService = new ObjectiveService(_Database);
            _ObjectiveDispatchPreviewService = new ObjectiveDispatchPreviewService(
                _Database,
                _WorkflowProfileService,
                _VesselReadinessService,
                _Git,
                _Settings);
            if (_Settings.CdWebhook != null && _Settings.CdWebhook.IsConfigured())
                _ReleaseWebhookDispatcher = new ReleaseWebhookDispatcher(_Settings.CdWebhook, _Logging);
            _ReleaseService = new ReleaseService(_Database, _WorkflowProfileService, _Logging, _ReleaseWebhookDispatcher);
            _DeploymentService = new DeploymentService(_Database, _WorkflowProfileService, _EnvironmentService, _CheckRunService, _Logging);
            _IncidentService = new IncidentService(_Database);
            _RunbookService = new RunbookService(_Database, _Logging);
            _AutomaticCheckRuns = new AutomaticCheckRunOrchestrator(_Database, _CheckRunService, _ReleaseService, _IncidentService, _Logging);
            _AutonomousRecovery = new AutonomousRecoveryOrchestrator(
                _Database, _Admiral, _IncidentService, _RunbookService, _Settings, _Logging,
                _MergeQueue, _Git, _AutoLandEvaluator, _ConventionChecker, _CriticalTriggerEvaluator, _ProviderProgress, _CheckRunService,
                null, _DispatchHold, _TerminalMarkers);

            // Gated typed-decision adapters (failure_cause, refusal, runtime_failure, review_substance,
            // change_substance, capacity_escalation). Each consumer holds an adapter over the shared
            // switchable client, never the raw client. They are always wired: without a key the effective
            // mode is Off, so every seam stays on its deterministic rule with no call and no event, and a
            // key added later takes effect without a restart. Set after construction so existing
            // construction sites and tests are unchanged.
            {
                TypedRefusalAdapter typedRefusalAdapter = new TypedRefusalAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                // A gated blocked_on_premise reading files a BriefContradiction papercut.
                typedRefusalAdapter.PapercutDatabase = _Database;
                missionService.RefusalAdapter = typedRefusalAdapter;
                admiralService.RefusalAdapter = typedRefusalAdapter;
                missionService.ReviewSubstanceAdapter = new TypedReviewSubstanceAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                admiralService.RuntimeFailureAdapter = new TypedRuntimeFailureAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                _AutonomousRecovery.FailureCauseAdapter = new TypedFailureCauseAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                // change_substance (ineffective-rescue) ships Off; capacity_escalation (Smart Routing model
                // groups) ships in Gate and is consulted only for a persona with a Lighter or Stronger list.
                missionService.ChangeSubstanceAdapter = new TypedChangeSubstanceAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                missionService.CapacityEscalationAdapter = new TypedCapacityEscalationAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
            }

            _ObjectiveScheduler = new AutonomousObjectiveScheduler(_Database, _ObjectiveService, _Admiral, _MergeQueue, _Settings, _Logging, _CodeIndex, _DispatchHold, _ObjectiveDispatchPreviewService);
            _IncidentLifecycle = new IncidentLifecycleOrchestrator(_Database, _IncidentService, _Settings, _Logging);
            _GitHubIntegrationService = new GitHubIntegrationService(_Database, _ObjectiveService, _CheckRunService, _DeploymentService, _Settings, _Logging);
            _LandingPreviewService = new LandingPreviewService(_Database, _Logging, _Settings);
            _HistoricalTimelineService = new HistoricalTimelineService(_Database);
            _ModelEndpointService = new ModelEndpointService(
                _Database,
                _Logging,
                isInUse: async (endpointId, token) =>
                {
                    List<Captain> captains = await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);
                    return captains.Any(captain => String.Equals(captain.ModelEndpointId, endpointId, StringComparison.Ordinal));
                });
            _RemoteTunnel = new RemoteTunnelManager(_Logging, _Settings);
            _RemoteDashboardRelay = new RemoteDashboardRelayService(_Logging, _Settings, _RemoteTunnel.PublishEventAsync);
            admiralService.OnGetRemoteTunnelStatus = _RemoteTunnel.GetStatus;
            admiralService.OnGetSchedulerStatus = () => McpObjectiveSchedulerTools.BuildStatus(_ObjectiveScheduler);
            admiralService.OnGetCodeIndexStaleness = () => _CodeIndex.GetStalenessSummaryAsync();
            // Seed built-in prompt templates, personas, and pipelines
            await _PromptTemplateService.SeedDefaultsAsync().ConfigureAwait(false);
            _Logging.Info(_Header + "prompt template seeding completed");

            _PersonaSeedService = new PersonaSeedService(_Database, _Logging, _Settings.AdditionalPersonas, _Settings.AdditionalPipelines);
            await _PersonaSeedService.SeedAsync().ConfigureAwait(false);
            _Logging.Info(_Header + "persona and pipeline seeding completed");

            await _EnvironmentService.SeedDefaultsAsync().ConfigureAwait(false);
            _Logging.Info(_Header + "deployment environment seeding completed");

            ArchitectPersonaSyncService architectSync = new ArchitectPersonaSyncService(_Database, _Logging);
            bool architectSynced = await architectSync.SyncAsync().ConfigureAwait(false);
            if (architectSynced) _Logging.Info(_Header + "Architect persona prompt synced from embedded resource");

            // Move the retired model tier settings onto captain and persona records once, after personas exist.
            TierRecordMigrationService tierMigration = new TierRecordMigrationService(_Database, _Logging);
            await tierMigration.RunAsync(_Settings, ArmadaSettings.DefaultSettingsPath).ConfigureAwait(false);
            await TierRoutingRecords.RefreshAsync(_Settings.ModelTier, _Database).ConfigureAwait(false);

            // Initialize authentication services
            _SessionTokenService = new SessionTokenService(_Settings.SessionTokenEncryptionKey);
            if (string.IsNullOrEmpty(_Settings.SessionTokenEncryptionKey))
            {
                _Settings.SessionTokenEncryptionKey = ((SessionTokenService)_SessionTokenService).GetKeyBase64();
                _Logging.Info(_Header + "auto-generated session token encryption key");
            }
            _AuthenticationService = new AuthenticationService(_Database, _SessionTokenService, _Settings, _Logging);
            _AuthorizationService = new AuthorizationService();

            // Seed synthetic admin identity if API key is configured
            if (!string.IsNullOrEmpty(_Settings.ApiKey))
            {
                await SeedSyntheticAdminAsync().ConfigureAwait(false);
            }

            // Initialize log rotation and data expiry
            _LogRotation = new LogRotationService(_Logging, _Settings.MaxLogFileSizeBytes, _Settings.MaxLogFileCount);
            _DataExpiry = new DataExpiryService(_Logging, _Database, _Settings.DataRetentionDays, _Settings.ProductionFactRetentionDays);
            _DiskLifecycle = new DiskLifecycleService(_Database, _Settings, _Logging);

            // Telemetry export. ArmadaMetrics already emits the meters; without this host nothing
            // observes or exports them. Start is a no-op unless telemetry.enabled is true, so a
            // fresh install still ships no telemetry surface.
            _TelemetryHost = new ArmadaTelemetryHost(_Logging);
            _TelemetryHost.Start(_Settings.Telemetry);
            _BranchCleanupSweep = new BranchCleanupSweepService(_Logging, _Database, _Settings, _Git);
            _TerminalVoyageMissions = new TerminalVoyageMissionReconciler(_Logging, _Database, _Git);

            // Initialize remote trigger service (no-op when remoteTrigger section is absent or disabled)
            RemoteTriggerHttpClient rtHttp = new RemoteTriggerHttpClient(_Logging);
            _AgentWakeProcessHost = new AgentWakeProcessHost(_Logging);
            _Settings.RemoteTrigger ??= new RemoteTriggerSettings();
            _RemoteTriggerService = new RemoteTriggerService(_Settings.RemoteTrigger, rtHttp, _AgentWakeProcessHost, _Database, _Logging);

            // Initialize handler classes (WebSocketHub is created later, so pass null initially)
            _MissionLanding = new MissionLandingHandler(
                _Logging, _Database, _Settings, _Git, _MergeQueue, _LandingService, _AutoLandEvaluator, _ConventionChecker, _CriticalTriggerEvaluator, _TemplateService, _PromptTemplateService, _Docks, _RemoteTriggerService, null, _CodeIndex);

            _AgentLifecycle = new AgentLifecycleHandler(
                _Logging, _Database, _Settings, _RuntimeFactory, _Admiral, _TemplateService, _PromptTemplateService, null, EmitEventAsync,
                sessionTokens: _SessionTokenService);
            _AgentLifecycle.SetProviderProgress(_ProviderProgress);
            _AgentLifecycle.SetTerminalMarkers(_TerminalMarkers);

            // Wire up agent lifecycle events
            _Admiral.OnLaunchAgent = _AgentLifecycle.HandleLaunchAgentAsync;
            _Admiral.OnStopAgent = _AgentLifecycle.HandleStopAgentAsync;
            _Admiral.OnCaptureDiff = _MissionLanding.HandleCaptureDiffAsync;
            _Admiral.OnIsProcessExitHandled = _AgentLifecycle.IsProcessExitHandled;
            missionService.OnGetMissionOutput = _AgentLifecycle.GetAndClearMissionOutput;
            if (_Settings.DefinitionOfDone.Enabled)
            {
                // The git seam enables consumer verification: a passing gate also builds the
                // vessels that declare this one as a sibling, so a public-API break is caught
                // while the producer's change is still unlanded.
                // D15 flake_score: score a red unit-test result and, when the model recommends it, run an
                // isolated class-filtered re-run whose real result is the truth. Ships Off; while the
                // decision is Off (or no key resolves) the adapter returns the rule and the red stands.
                TypedFlakeScoreAdapter? flakeScoreAdapter = new TypedFlakeScoreAdapter(_TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                missionService.DefinitionOfDone = new DefinitionOfDoneGate(
                    _Settings.DefinitionOfDone,
                    _Database,
                    _Logging,
                    new DockerCliContainerRuntimeProbe(),
                    _Git,
                    flakeScoreAdapter);
            }
            MissionOutcomeWakeHandler outcomeWake = new MissionOutcomeWakeHandler(_RemoteTriggerService, _Logging);
            missionService.OnMissionOutcome = async (Mission mission, bool willInvokeLanding) =>
            {
                await outcomeWake.HandleAsync(mission, willInvokeLanding).ConfigureAwait(false);
                await _AutonomousRecovery.HandleMissionOutcomeAsync(mission, willInvokeLanding).ConfigureAwait(false);
                _IncidentLifecycle.TriggerBackgroundSweep();
                if (mission.Status == MissionStatusEnum.Failed
                    || mission.Status == MissionStatusEnum.LandingFailed
                    || mission.Status == MissionStatusEnum.Cancelled)
                    _ObjectiveScheduler.RequestRefill();
            };
            _Admiral.OnMissionComplete = _MissionLanding.HandleMissionCompleteAsync;
            _Admiral.OnVoyageComplete = async voyage =>
            {
                try
                {
                    await _MissionLanding.HandleVoyageCompleteAsync(voyage).ConfigureAwait(false);
                }
                finally
                {
                    _ObjectiveScheduler.RequestRefill();
                }
            };
            _Admiral.OnReconcilePullRequest = _MissionLanding.HandleReconcilePullRequestAsync;
            _Admiral.OnReconcileMergeEntries = async () =>
            {
                int prCount = await _MergeQueue.ReconcilePullRequestEntriesAsync().ConfigureAwait(false);
                int landingCount = await _MergeQueue.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                return prCount + landingCount;
            };
            _LandingService.OnPerformLanding = _MissionLanding.HandleMissionCompleteAsync;

            // Initialize REST API (Watson7)
            WebserverSettings wsSettings = new WebserverSettings();
            wsSettings.Hostname = _Settings.Rest.Hostname;
            wsSettings.Port = _Settings.AdmiralPort;
            wsSettings.Ssl.Enable = _Settings.Rest.Ssl;
            wsSettings.WebSockets.Enable = _Settings.WebSocketEnabled;

            _App = new Webserver(wsSettings, DashboardDefaultRouteAsync);
            _App.Events.Logger = (string message) => _Logging.Debug(_Header + message);

            _App.UseOpenApi(openApi =>
            {
                openApi.Info.Title = ArmadaConstants.ProductName + " API";
                openApi.Info.Version = ArmadaConstants.ProductVersion;
                openApi.Info.Description = "Multi-agent orchestration API for scaling human developers with AI captains across git worktrees.";

                // Tags for route grouping
                openApi.Tags.Add(new OpenApiTag { Name = "Status", Description = "Health check and system status" });
                openApi.Tags.Add(new OpenApiTag { Name = "Fleets", Description = "Fleet (repository collection) management" });
                openApi.Tags.Add(new OpenApiTag { Name = "Vessels", Description = "Vessel (git repository) management" });
                openApi.Tags.Add(new OpenApiTag { Name = "Workspace", Description = "Workspace browsing, editing, search, and dispatch handoff" });
                openApi.Tags.Add(new OpenApiTag { Name = "Objectives", Description = "Cross-repository objectives and intake-style scope records" });
                openApi.Tags.Add(new OpenApiTag { Name = "WorkflowProfiles", Description = "Project-specific build, test, release, deploy, and verification command profiles" });
                openApi.Tags.Add(new OpenApiTag { Name = "Environments", Description = "First-class deployment environment metadata for vessels" });
                openApi.Tags.Add(new OpenApiTag { Name = "CheckRuns", Description = "Structured build, test, deploy, and verification executions with durable results" });
                openApi.Tags.Add(new OpenApiTag { Name = "Releases", Description = "First-class release records linking work, checks, notes, versions, and artifacts" });
                openApi.Tags.Add(new OpenApiTag { Name = "Deployments", Description = "First-class deployment records with approval, verification, and rollback state" });
                openApi.Tags.Add(new OpenApiTag { Name = "Incidents", Description = "Incident, rollback, and hotfix records tied to current delivery state" });
                openApi.Tags.Add(new OpenApiTag { Name = "Runbooks", Description = "Executable operational runbooks backed by playbooks and execution records" });
                openApi.Tags.Add(new OpenApiTag { Name = "RequestHistory", Description = "Captured REST request history, summaries, and replay metadata" });
                openApi.Tags.Add(new OpenApiTag { Name = "Memories", Description = "Native captain memory records: episodic, semantic, and procedural findings with provenance" });
                openApi.Tags.Add(new OpenApiTag { Name = "History", Description = "Cross-entity operational timeline and historical memory" });
                openApi.Tags.Add(new OpenApiTag { Name = "Voyages", Description = "Voyage (mission batch) management" });
                openApi.Tags.Add(new OpenApiTag { Name = "Missions", Description = "Mission (atomic work unit) management" });
                openApi.Tags.Add(new OpenApiTag { Name = "Planning", Description = "Captain planning sessions and transcript-to-dispatch flow" });
                openApi.Tags.Add(new OpenApiTag { Name = "Playbooks", Description = "Markdown playbook management" });
                openApi.Tags.Add(new OpenApiTag { Name = "Captains", Description = "Captain (AI agent) management" });
                openApi.Tags.Add(new OpenApiTag { Name = "Signals", Description = "Signal (inter-agent messaging) management" });
                openApi.Tags.Add(new OpenApiTag { Name = "Events", Description = "System event log" });
                openApi.Tags.Add(new OpenApiTag { Name = "Runtimes", Description = "Runtime-specific integration helpers and discovery" });
                openApi.Tags.Add(new OpenApiTag { Name = "MergeQueue", Description = "Bors-style merge queue with batch testing" });
                openApi.Tags.Add(new OpenApiTag { Name = "Authentication", Description = "Authentication and identity" });
                openApi.Tags.Add(new OpenApiTag { Name = "Tenants", Description = "Multi-tenant management" });
                openApi.Tags.Add(new OpenApiTag { Name = "Users", Description = "User management" });
                openApi.Tags.Add(new OpenApiTag { Name = "Credentials", Description = "Credential (API token) management" });

                // API key security scheme
                openApi.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
                {
                    Type = "apiKey",
                    Name = "X-Api-Key",
                    In = "header",
                    Description = "API key for authenticating requests. Configure via ArmadaSettings.ApiKey."
                };
            });

            // Set timestamp on request start
            _App.Routes.PreRouting = async (HttpContextBase ctx) =>
            {
                ctx.Timestamp.Start = DateTime.UtcNow;
                ctx.Response.ContentType = "application/json";
                await Task.CompletedTask.ConfigureAwait(false);
            };

            // Log every API call and apply CORS on every response
            _App.Routes.PostRouting = async (HttpContextBase ctx) =>
            {
                ctx.Timestamp.End = DateTime.UtcNow;
                _Logging.Debug(
                    _Header +
                    ctx.Request.Method + " " +
                    ctx.Request.Url.RawWithQuery + " " +
                    ctx.Response.StatusCode + " " +
                    "(" + (ctx.Timestamp.TotalMs.HasValue ? ctx.Timestamp.TotalMs.Value.ToString("F2") : "?") + "ms)");

                ApplyCorsHeaders(ctx);
                await CaptureRequestHistoryAsync(ctx).ConfigureAwait(false);
                await Task.CompletedTask.ConfigureAwait(false);
            };

            // CORS preflight handler. Browsers send an OPTIONS before cross-origin requests;
            // we must answer 204 with the allow headers before the real call can proceed.
            _App.Routes.Preflight = async (HttpContextBase ctx) =>
            {
                ApplyCorsHeaders(ctx);
                ctx.Response.StatusCode = 200;
                await ctx.Response.Send().ConfigureAwait(false);
            };

            // Initialize WebSocket hub (before routes so it's available for injection)
            // REST, WebSocket, and MCP status transitions share this one path so a manual Complete
            // meets the same gates on every entry point.
            _StatusTransitions = new MissionStatusTransitionService(
                _Database, _Admiral, _MissionService, _Git, _AgentLifecycle.IsMissionProcessActiveAsync,
                _MissionLanding.HandleMissionCompleteAsync, EmitEventAsync, _Logging);
            _WebSocketHub = new ArmadaWebSocketHub(_Logging, _Admiral, _Database, _MergeQueue, _AuthenticationService, _Settings, _Git, () => { OnStopping?.Invoke(); _TokenSource.Cancel(); }, _StatusTransitions);
            _StatusTransitions.SetWebSocketHub(_WebSocketHub);
            _AgentLifecycle.SetWebSocketHub(_WebSocketHub);
            _MissionLanding.SetWebSocketHub(_WebSocketHub);
            missionService.OnReviewRequested = _WebSocketHub.BroadcastApprovalNeeded;
            _CheckRunService.OnCheckRunChanged = _WebSocketHub.BroadcastCheckRunChange;
            _ObjectiveService.OnObjectiveChanged = objective =>
            {
                _WebSocketHub.BroadcastObjectiveChange(objective);
                _ObjectiveScheduler.NotifyObjectiveChanged(objective);
            };
            _ObjectiveService.OnObjectiveDeleted = _WebSocketHub.BroadcastObjectiveDeleted;
            _DeploymentService.OnDeploymentChanged = _WebSocketHub.BroadcastDeploymentChange;
            _IncidentService.OnIncidentChanged = _WebSocketHub.BroadcastIncidentChange;
            _RunbookService.OnRunbookExecutionChanged = _WebSocketHub.BroadcastRunbookExecutionChange;
            _PlanningSessions = new PlanningSessionCoordinator(
                _Logging,
                _Database,
                _Settings,
                _Docks,
                _Admiral,
                _RuntimeFactory,
                EmitEventAsync,
                _WebSocketHub,
                _ObjectiveService,
                _ObjectiveDispatchPreviewService);
            _ObjectiveRefinementSessions = new ObjectiveRefinementCoordinator(
                _Logging,
                _Database,
                _Settings,
                _RuntimeFactory,
                EmitEventAsync,
                _WebSocketHub);

            _CoordinationService = new CoordinationService(_Logging, _Database, _WebSocketHub);
            _CoordinationService.BoardWakeEmitter = async (participantKey, text, token) =>
            {
                // participantKey null targets the registered AgentWake session.
                AgentWakeSessionRegistration? registered = _RemoteTriggerService.GetAgentWakeSession();
                string? target = participantKey ?? registered?.ParticipantKey;
                if (String.IsNullOrEmpty(target)) return;
                await _RemoteTriggerService.FireBoardWakeAsync(target!, text, token).ConfigureAwait(false);
            };

            // D5 preflight text-half adapter. Always wired; without a key the effective mode is Off, so
            // the preview stays fully deterministic. It runs after the
            // deterministic preflight block in the preview and posts an owner-addressed board note for
            // a Q13 owner ruling through the coordination service, targeting the registered AgentWake
            // session the same way the board wake emitter above does.
            {
                CoordinationOwnerDecisionNotePoster ownerNotePoster = new CoordinationOwnerDecisionNotePoster(
                    _CoordinationService,
                    () => _RemoteTriggerService.GetAgentWakeSession()?.ParticipantKey,
                    _Logging);
                _ObjectiveDispatchPreviewService.PreflightAdapter = new PreflightTextAdapter(
                    _Settings.TypedDecisions, _TypedDecisionClient, _TypedDecisionRecorder, ownerNotePoster, _Logging);

                // D19 stage necessity (dispatch preview) and D20 handoff outcome (stage handoff). Each
                // follows its mode in the decisions map; set Off, the adapter returns the deterministic rule. The D20 owner note reuses the same poster as D5.
                _ObjectiveDispatchPreviewService.StageNecessityAdapter = new TypedStageNecessityAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                missionService.HandoffOutcomeAdapter = new TypedHandoffOutcomeAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                missionService.HandoffOwnerNotePoster = ownerNotePoster;
                // D3 runtime_failure reports a fleet-wide provider fault as a broadcast board note. The
                // adapter was built before the coordination service existed, so the poster is set here.
                if (admiralService.RuntimeFailureAdapter != null)
                    admiralService.RuntimeFailureAdapter.NotePoster = new CoordinationBroadcastNotePoster(_CoordinationService, _Logging);
                // D21 revision_kind (Judge NEEDS_REVISION), D22 test_covers (TestEngineer handoff), and
                // D24 lint_finding (Linter handoff). Each follows its mode in the decisions map; set Off,
                // the seam runs its deterministic path.
                missionService.RevisionKindAdapter = new TypedRevisionKindAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                missionService.TestCoversAdapter = new TypedTestCoversAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                missionService.LintFindingAdapter = new TypedLintFindingAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);

                // D26 prior_art. One adapter over a deterministic retriever feeds two admiral seams: the
                // dispatch preflight (already_done / integrate / uncertain-band analyst issues) and the
                // Worker->Judge handoff (a re-implementation review instruction). It follows its mode in
                // the decisions map; set Off, both seams run their deterministic path unchanged. The retriever reads the four surfaces through the git service (a GitService is
                // also the branch inventory) and the objective store.
                if (_Git is IBranchInventory priorArtBranchInventory)
                {
                    IPriorArtRetriever priorArtRetriever = new PriorArtRetriever(
                        new GitPriorArtSource(_Git, priorArtBranchInventory, _Database, _Logging), _Logging);
                    TypedPriorArtAdapter priorArtAdapter = new TypedPriorArtAdapter(
                        _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, priorArtRetriever, _Logging);
                    _ObjectiveDispatchPreviewService.PriorArtAdapter = priorArtAdapter;
                    missionService.PriorArtAdapter = priorArtAdapter;
                }

                // D13 owner_digest runner. It reuses the owner-addressed note poster above, ranks each
                // owner-decision candidate with its adapter, and — driven daily by the health loop —
                // posts one owner-addressed digest note and one owner_decisions.digest event per UTC
                // day. It is dormant until the owner_digest decision is enabled, and never answers a
                // question. The hit collector reads only owner-decision preparation claims that an
                // anchor change re-opened; other sources attach as they land.
                OwnerDigestHitCollector ownerDigestHits = new OwnerDigestHitCollector(
                    _Database, () => DateTime.UtcNow, _Logging);
                TypedOwnerDigestAdapter ownerDigestAdapter = new TypedOwnerDigestAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
                _OwnerDigestRunner = new OwnerDigestRunner(
                    _Settings.TypedDecisions,
                    ownerDigestAdapter,
                    ownerNotePoster,
                    ownerDigestHits.CollectAsync,
                    _Database,
                    () => DateTime.UtcNow,
                    _Logging);

                // D18 memory_candidate and D23 seam B memory_review. Both store proposals in the database
                // through one writer, because the AI-Memory folder is read-only to the admiral. The weekly
                // papercut sweep is driven by the health loop; the Recorder review runs when a Recorder
                // stage finishes. Each is dormant while its decision is Off.
                DatabaseMemoryCandidateProposalWriter memoryProposalWriter = new DatabaseMemoryCandidateProposalWriter(_Database, _Logging);
                MemoryCandidateAdapter memoryCandidateAdapter = new MemoryCandidateAdapter(
                    _Settings.TypedDecisions, _TypedDecisionClient, _TypedDecisionRecorder, memoryProposalWriter, _Logging);
                _PapercutMemorySweepRunner = new PapercutMemorySweepRunner(
                    _Settings.TypedDecisions,
                    memoryCandidateAdapter,
                    _PapercutMergeAdapter,
                    _Database,
                    () => DateTime.UtcNow,
                    _Logging);
                missionService.RecorderMemoryReviewAdapter = new RecorderMemoryReviewAdapter(
                    _Settings.TypedDecisions, _TypedDecisionClient, _TypedDecisionRecorder, memoryProposalWriter, _Database, _Logging);
                // D10 criteria_lint: append model-flagged criteria_review lines to a refinement summary.
                _ObjectiveRefinementSessions.CriteriaLintAdapter = new CriteriaLintAdapter(
                    _Settings.TypedDecisions, _TypedDecisionClient, _TypedDecisionRecorder, _Logging);

                // D11 inbox_triage: annotate and sort inbox items and board notes by attention. Threaded
                // into the inbox and coordination-read MCP tools through the registrar.
                _InboxTriageAdapter = new InboxTriageAdapter(
                    _Settings.TypedDecisions, _TypedDecisionClient, _TypedDecisionRecorder, _Logging);

                // D12 followup_routing: give each Judge follow-up a home (Triaged objective, evidence
                // note, or link). The router creates no voyage; a blocking item is flagged for the owner.
                ServerFollowUpRouter followUpRouter = new ServerFollowUpRouter(
                    _Database, _ObjectiveService, _CoordinationService, ownerNotePoster, _Logging);
                _FollowUpRoutingAdapter = new FollowUpRoutingAdapter(
                    _Settings.TypedDecisions, _TypedDecisionClient, _TypedDecisionRecorder, followUpRouter, _Logging);
                _FollowUpRouter = followUpRouter;
                _ChangeQualityAdapter = new TypedChangeQualityAdapter(
                    _TypedDecisionClient, _TypedDecisionRecorder, _Settings.TypedDecisions, _Logging);
            }

            _CaptainTools = new CaptainToolService(
                _Logging,
                _Database,
                _Settings,
                sessionTokens: _SessionTokenService);

            _RemoteTunnel.OnHandleRequest = HandleRemoteTunnelRequestAsync;

            RegisterRoutes();
            InitializeDashboard();

            // Register WebSocket route on the main REST server
            _App.WebSocket("/ws", _WebSocketHub.HandleWebSocketAsync);
            _Logging.Info(_Header + "WebSocket route registered at /ws");

            await ReconcileHarborJobsAsync().ConfigureAwait(false);
            RegisterHarbor();

            // Watson 7 StartAsync is long-running; Start() binds and returns after
            // scheduling the accept loop.
            _App.Start(_TokenSource.Token);
            _Logging.Info(_Header + "REST API started on port " + _Settings.AdmiralPort);

            // Settings are otherwise read once at startup, so a hand edit to
            // settings.json would not take effect until the next restart. Started after
            // the services above are wired so a reload cannot race construction.
            _SettingsWatcher = new SettingsFileWatcher(_Settings, _Logging);
            _SettingsWatcher.Start();

            // Initialize MCP server
            _McpServer = new ArmadaMcpHttpServer(_Settings.Rest.Hostname, _Settings.McpPort);
            _McpServer.ServerName = ArmadaConstants.ProductName;
            _McpServer.ServerVersion = ArmadaConstants.ProductVersion;

            // Every MCP request authenticates like a REST request. Missing or invalid credentials are
            // refused; nothing falls back to a default administrative identity.
            _McpServer.Authenticator = AuthenticateMcpRequestAsync;
            _McpServer.ToolAuthorizer = McpToolAccessPolicy.IsAllowed;

            // Deliver directed wakes on whatever tool the session calls next. Before this,
            // a wake reached a session only through the two coordination tools below, so a
            // session monitoring a voyage could hold unread mail for the whole run.
            _McpServer.WakeBannerExcludedTools.Add("armada_coordination_read");
            _McpServer.WakeBannerExcludedTools.Add("armada_coordination_heartbeat");
            _McpServer.PendingWakeProvider = async (participantKey, token) =>
            {
                List<Signal> wakes = await _CoordinationService
                    .EnumerateUnreadWakesAsync(participantKey, token).ConfigureAwait(false);
                return wakes.Select(wake => wake.Payload ?? String.Empty).ToList();
            };

            RegisterMcpTools();

            await _McpServer.StartAsync(_TokenSource.Token).ConfigureAwait(false);
            _Logging.Info(_Header + "MCP server started on port " + _Settings.McpPort);

            _RemoteTunnel.Start(_TokenSource.Token);
            _Logging.Info(_Header + "remote tunnel manager started");

            try
            {
                await _PlanningSessions.RecoverSessionsAsync(_TokenSource.Token).ConfigureAwait(false);
                _Logging.Info(_Header + "planning session recovery completed");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "planning session recovery error: " + ex.Message);
            }

            try
            {
                await _PlanningSessions.MaintainSessionsAsync(_TokenSource.Token).ConfigureAwait(false);
                _Logging.Info(_Header + "planning session maintenance completed");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "planning session maintenance error: " + ex.Message);
            }

            try
            {
                await _ObjectiveRefinementSessions.RecoverSessionsAsync(_TokenSource.Token).ConfigureAwait(false);
                _Logging.Info(_Header + "objective refinement session recovery completed");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "objective refinement session recovery error: " + ex.Message);
            }

            try
            {
                await _ObjectiveRefinementSessions.MaintainSessionsAsync(_TokenSource.Token).ConfigureAwait(false);
                _Logging.Info(_Header + "objective refinement session maintenance completed");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "objective refinement session maintenance error: " + ex.Message);
            }

            // Generate the context index (manifest + derived core bundle) from AI-Memory and the docs
            // tree. Additive and non-critical: it only READS the sources and writes two artifacts into
            // the data directory, changing nothing about how memory currently loads. A failure logs a
            // warning and never breaks startup (fail-open).
            GenerateContextIndex();

            // Start health check loop
            _HealthCheckTask = HealthCheckLoopAsync(_TokenSource.Token);
            _ModelEndpointHealthTask = ModelEndpointHealthLoopAsync(_TokenSource.Token);
        }

        /// <summary>
        /// Build the context retrieval service in-process by reusing the context-index generator: it
        /// reads AI-Memory and the docs tree and returns the chunks (with bodies) that the retrieval
        /// layer ranks. Building in-process, rather than reading the manifest file the startup
        /// generator writes, keeps the tool independent of file write ordering; both derive from the
        /// same generator, so they agree. Fully guarded: any failure logs a warning and returns null,
        /// and the fetch tool is then simply not registered.
        /// </summary>
        private Armada.Core.Context.ContextRetrievalService? BuildContextRetrievalService()
        {
            try
            {
                string? docsRoot = ResolveDocsRoot();
                Armada.Core.Context.ContextIndexGenerator generator = new Armada.Core.Context.ContextIndexGenerator(_Logging);
                Armada.Core.Context.ContextIndex index = generator.Build(_Settings.AiMemoryRoot, docsRoot, _Settings.ContextRetrieval.ChunkMetadataPath);
                Armada.Core.Context.ContextRetrievalService service =
                    new Armada.Core.Context.ContextRetrievalService(index.Chunks, null, _Logging);
                _Logging.Info(_Header + "context retrieval service built: core=" + service.CoreCount +
                    " leaves=" + service.LeafCount);
                return service;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "context retrieval service build error: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Generate the context index at startup: a manifest over every AI-Memory and Armada docs
        /// chunk, plus the derived always-on core bundle. Writes <c>context-index/manifest.json</c> and
        /// <c>context-index/context-core.md</c> under the data directory. Reads AI-Memory but never
        /// writes it, and touches no loader. Fully guarded: any failure logs a warning and returns.
        /// </summary>
        private void GenerateContextIndex()
        {
            try
            {
                string outputDirectory = Path.Combine(_Settings.DataDirectory, "context-index");
                string? docsRoot = ResolveDocsRoot();
                ContextIndexGenerator generator = new ContextIndexGenerator(_Logging);
                ContextIndexGenerationSummary summary = generator.Generate(_Settings.AiMemoryRoot, docsRoot, outputDirectory, _Settings.ContextRetrieval.ChunkMetadataPath);

                _Logging.Info(_Header + "context index generated: chunks=" + summary.ChunkCount +
                    " core=" + summary.CoreCount + " core_bytes=" + summary.CoreBundleBytes +
                    " total_bytes=" + summary.TotalChunkBytes + " written=" + summary.Written +
                    (String.IsNullOrEmpty(summary.Note) ? "" : " note=" + summary.Note));
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "context index generation error: " + ex.Message);
            }
        }

        /// <summary>
        /// Best-effort resolution of the Armada docs directory for context indexing. Prefers the
        /// <c>ARMADA_DOCS_ROOT</c> environment variable, then probes upward from the running assembly
        /// and the current directory for a <c>docs</c> folder holding <c>armada-ops.md</c>. Returns null
        /// when none is found; the generator then indexes AI-Memory only, which still yields the whole
        /// core bundle (every core rule is an AI-Memory rule).
        /// </summary>
        private static string? ResolveDocsRoot()
        {
            string? env = Environment.GetEnvironmentVariable("ARMADA_DOCS_ROOT");
            if (!String.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                DirectoryInfo? dir = new DirectoryInfo(start);
                for (int depth = 0; dir != null && depth < 8; depth++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "docs");
                    if (File.Exists(Path.Combine(candidate, "armada-ops.md"))) return candidate;
                }
            }
            return null;
        }

        /// <summary>
        /// Schedule best-effort baseline context-pack cache warming for indexed vessels at startup.
        /// Does not block server initialization.
        /// </summary>
        private void ScheduleStartupBaselineCacheWarmup(ICodeIndexService codeIndexService)
        {
            if (codeIndexService == null) return;
            if (!_Settings.CodeIndex.Enabled) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    List<Vessel> vessels = await _Database.Vessels.EnumerateAsync(_TokenSource.Token).ConfigureAwait(false);
                    int warmed = 0;
                    foreach (Vessel vessel in vessels)
                    {
                        if (_TokenSource.Token.IsCancellationRequested) break;
                        if (vessel == null || String.IsNullOrWhiteSpace(vessel.Id)) continue;

                        try
                        {
                            CodeIndexStatus status = await codeIndexService.GetStatusAsync(vessel.Id, _TokenSource.Token).ConfigureAwait(false);
                            if (String.IsNullOrWhiteSpace(status.IndexedCommitSha)) continue;

                            await codeIndexService.WarmBaselineCacheAsync(vessel.Id, _TokenSource.Token).ConfigureAwait(false);
                            warmed++;
                        }
                        catch (Exception ex)
                        {
                            _Logging.Warn(_Header + "startup baseline cache warm-up failed for vessel " + vessel.Id + ": " + ex.Message);
                        }
                    }

                    if (warmed > 0)
                    {
                        _Logging.Info(_Header + "startup baseline cache warm-up scheduled for " + warmed + " indexed vessel(s)");
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "startup baseline cache warm-up enumeration failed: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Build startup warnings for code-index settings that otherwise silently degrade.
        /// </summary>
        public static IReadOnlyList<string> BuildCodeIndexConfigurationWarnings(CodeIndexSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            List<string> warnings = new List<string>();
            if (!String.IsNullOrWhiteSpace(settings.EmbeddingApiKey) && !settings.UseSemanticSearch)
            {
                warnings.Add("EmbeddingApiKey is configured but UseSemanticSearch=false; semantic search will not run. Set CodeIndex:UseSemanticSearch=true to enable.");
            }

            bool inferenceKeyConfigured =
                !String.IsNullOrWhiteSpace(settings.SummarizerApiKey) ||
                !String.IsNullOrWhiteSpace(settings.EmbeddingApiKey);
            if (inferenceKeyConfigured && !settings.UseSummarizer)
            {
                warnings.Add("SummarizerApiKey is configured but UseSummarizer=false; summarization will not run. Set CodeIndex:UseSummarizer=true to enable.");
            }

            return warnings;
        }

        /// <summary>
        /// Stop the Admiral server.
        /// </summary>
        public void Stop()
        {
            _Logging.Info(_Header + "stopping");
            try
            {
                if (_App?.IsListening == true)
                    _App.Stop();
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "REST API stop error: " + ex.Message);
            }
            try
            {
                _OpenCodeServerLauncher?.Dispose();
            }
            catch
            {
            }
            // Pending account logins own CLI processes; stop them with the Admiral.
            _AccountLogins?.Dispose();
            try
            {
                _SettingsWatcher?.Dispose();
                _SettingsWatcher = null;
            }
            catch
            {
            }
            // Kill agent subprocesses so none survive as orphans after the Admiral exits.
            // Runs before the token is cancelled and the database is disposed (it needs both).
            try
            {
                _Admiral?.StopAllAgentProcessesAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error stopping agent processes on shutdown: " + ex.Message);
            }

            _TokenSource.Cancel();
            try
            {
                _ModelEndpointHealthTask?.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "model endpoint health loop stop error: " + ex.Message);
            }
            _ObjectiveScheduler?.Dispose();
            _RemoteTunnel?.StopAsync().GetAwaiter().GetResult();
            _RemoteDashboardRelay?.DisposeAsync().GetAwaiter().GetResult();
            _McpServer?.StopAsync().GetAwaiter().GetResult();
            try
            {
                _TelemetryHost?.Dispose();
                _TelemetryHost = null;
            }
            catch
            {
            }
            _Database?.Dispose();
            OnStopping?.Invoke();
        }

        #endregion

        #region Private-Methods

        private async Task<AuthContext> AuthenticateRequestAsync(WatsonWebserver.Core.HttpContextBase ctx)
        {
            string? authHeader = ctx.Request.Headers.Get("Authorization");
            string? tokenHeader = ctx.Request.Headers.Get("X-Token");
            string? apiKeyHeader = ctx.Request.Headers.Get("X-Api-Key");
            AuthContext result = await _AuthenticationService.AuthenticateAsync(authHeader, tokenHeader, apiKeyHeader).ConfigureAwait(false);
            _RequestAuthContexts.Remove(ctx);
            _RequestAuthContexts.Add(ctx, result);
            return result;
        }

        /// <summary>
        /// Resolve the credentials of an MCP request. The launch credential this admiral gives its
        /// captain processes maps to a named captain identity with operator tool access, because
        /// captains use the operator catalog; every other credential goes through the REST
        /// authentication service.
        /// </summary>
        private async Task<AuthContext> AuthenticateMcpRequestAsync(McpRequestCredentials credentials, CancellationToken token)
        {
            const string bearerPrefix = "Bearer ";
            string? authorization = credentials.Authorization;
            if (!String.IsNullOrEmpty(authorization)
                && authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                && McpLaunchCredential.Matches(authorization.Substring(bearerPrefix.Length)))
            {
                return AuthContext.Authenticated(
                    ArmadaConstants.DefaultTenantId,
                    ArmadaConstants.DefaultUserId,
                    true,
                    true,
                    "CaptainLaunch",
                    null,
                    "Captain launch credential");
            }

            return await _AuthenticationService.AuthenticateAsync(credentials.Authorization, credentials.SessionToken, credentials.ApiKey, token).ConfigureAwait(false);
        }

        private async Task SeedSyntheticAdminAsync()
        {
            _Logging.Info(_Header + "seeding synthetic admin identity for API key");

            // Create system tenant if not exists
            var existingTenant = await _Database.Tenants.ReadAsync(ArmadaConstants.SystemTenantId).ConfigureAwait(false);
            if (existingTenant == null)
            {
                var systemTenant = new TenantMetadata();
                systemTenant.Id = ArmadaConstants.SystemTenantId;
                systemTenant.Name = ArmadaConstants.SystemTenantName;
                systemTenant.IsProtected = true;
                await _Database.Tenants.CreateAsync(systemTenant).ConfigureAwait(false);
            }

            // Create system user if not exists
            var existingUser = await _Database.Users.ReadByIdAsync(ArmadaConstants.SystemUserId).ConfigureAwait(false);
            if (existingUser == null)
            {
                var systemUser = new UserMaster();
                systemUser.Id = ArmadaConstants.SystemUserId;
                systemUser.TenantId = ArmadaConstants.SystemTenantId;
                systemUser.Email = ArmadaConstants.SystemUserEmail;
                systemUser.PasswordSha256 = UserMaster.ComputePasswordHash("system");
                systemUser.IsAdmin = true;
                systemUser.IsTenantAdmin = true;
                systemUser.IsProtected = true;
                await _Database.Users.CreateAsync(systemUser).ConfigureAwait(false);
            }

            _Logging.Info(_Header + "synthetic admin identity ready");
        }

        /// <summary>
        /// Register the Harbor runner link and Harbor enrollment routes only when Harbor is explicitly enabled.
        /// The link authenticates through the application's authentication service and requires the WebSocket
        /// server; without it Harbor is refused and the refusal is logged.
        /// </summary>
        private void RegisterHarbor()
        {
            if (!_Settings.Harbor.Enabled)
            {
                _Logging.Info(_Header + "Harbor runner link disabled");
                return;
            }
            if (!_Settings.WebSocketEnabled)
            {
                _Logging.Warn(_Header + "Harbor runner link not registered: Harbor requires WebSocketEnabled");
                return;
            }
            Armada.Core.Services.HarborRunnerEnrollmentService enrollments = new Armada.Core.Services.HarborRunnerEnrollmentService(_Database);
            Armada.Core.Services.HarborRunnerSessionRegistry registry = new Armada.Core.Services.HarborRunnerSessionRegistry(true, enrollments);
            Armada.Core.Harbor.HarborJobCoordinator coordinator = new Armada.Core.Harbor.HarborJobCoordinator(registry, enrollments, _Database.HarborJobs, _Logging);
            Armada.Server.Harbor.HarborLinkEndpoint endpoint = new Armada.Server.Harbor.HarborLinkEndpoint(_Settings.Harbor, _AuthenticationService, registry, coordinator, _Logging);
            _App.WebSocket(_Settings.Harbor.LinkPath, endpoint.HandleWebSocketAsync);
            new HarborRunnerEnrollmentRoutes(enrollments, _JsonOptions).Register(_App, AuthenticateRequestAsync, _AuthorizationService);

            // Missions reach a runner only through a configured route; the host is what the lifecycle launches through.
            _AgentLifecycle.SetHarborHost(new Armada.Core.Harbor.HarborMissionExecutor(coordinator, _Logging));
            _HarborJobService = new Armada.Core.Services.HarborJobService(coordinator, _Database.HarborJobs, enrollments);
            new HarborJobRoutes(_HarborJobService, _JsonOptions).Register(_App, AuthenticateRequestAsync, _AuthorizationService);
            StartHarborJobExpiry(coordinator);
            _Logging.Info(_Header + "Harbor runner link registered at " + _Settings.Harbor.LinkPath + " with " + _Settings.Harbor.MissionRoutes.Count + " mission route(s)");
        }

        /// <summary>
        /// Fail the Harbor jobs an earlier Admiral process left unfinished, whether or not Harbor is enabled now, so no
        /// job record stays active with nothing left to report for it.
        /// </summary>
        private async Task ReconcileHarborJobsAsync()
        {
            try
            {
                int lost = await Armada.Core.Harbor.HarborJobCoordinator.ReconcileAfterRestartAsync(_Database.HarborJobs, DateTime.UtcNow).ConfigureAwait(false);
                if (lost > 0)
                    _Logging.Warn(_Header + lost + " unfinished Harbor job(s) from an earlier Admiral process marked lost: " + Armada.Core.Harbor.HarborJobCoordinator.ReasonAdmiralRestarted);
                else
                    _Logging.Info(_Header + "no unfinished Harbor jobs from an earlier Admiral process");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "Harbor job reconciliation failed; unfinished job records keep their last state: " + ex.Message);
            }
        }

        /// <summary>
        /// Periodically lose the jobs of runners that stayed disconnected past the grace period, so their missions
        /// read as a dead process and recover like one.
        /// </summary>
        private void StartHarborJobExpiry(Armada.Core.Harbor.HarborJobCoordinator coordinator)
        {
            CancellationToken token = _TokenSource.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    try
                    {
                        TimeSpan grace = TimeSpan.FromSeconds(_Settings.Harbor.DisconnectedJobGraceSeconds);
                        int expired = await coordinator.ExpireDetachedRunnersAsync(grace, DateTime.UtcNow).ConfigureAwait(false);
                        if (expired > 0)
                            _Logging.Warn(_Header + expired + " Harbor job(s) lost: runner disconnected longer than " + grace.TotalSeconds + "s");
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "Harbor job expiry failed: " + ex.Message);
                    }
                }
            });
        }

        private void RegisterRoutes()
        {
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate = AuthenticateRequestAsync;

            // Authentication & identity
            new AuthRoutes(_SessionTokenService, _AuthenticationService, _Database, _Settings, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Tenants, users, credentials
            new TenantRoutes(_Database, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Status, health, doctor, settings, server control
            new StatusRoutes(_Database, _Settings, _Admiral, () => Stop(), _StartUtc, _JsonOptions, _Logging, _BuildDriftService, _RemoteTunnel.GetStatus, _RemoteTunnel.ReloadAsync,
                () => (_MissionService as MissionService)?.CapacityEscalationAdapter, _TypedDecisionKeys)
                .Register(_App, authenticate, _AuthorizationService);

            // Typed-decision modes and the provider key file (administrator only; never request-history captured)
            new TypedDecisionRoutes(_Settings, _TypedDecisionKeys, _JsonOptions, _Logging, () => _Settings.SaveAsync())
                .Register(_App, authenticate, _AuthorizationService);

            // Subscription account logins driven from the dashboard
            _AccountLogins = new AccountLoginService(
                _Settings.DataDirectory,
                new SystemAccountLoginProcessRunner(),
                message => _Logging.Info(_Header + message),
                (eventType, message, accountId) => _ = EmitEventAsync(eventType, message, "usage_account", accountId));
            _AccountLogins.OnLoginSucceeded = accountId =>
            {
                // A completed login replaces any cached "expired" probe result and starts a fresh check at once.
                UsageRoutingService usage = UsageRoutingService.For(_Settings);
                usage.InvalidateLoginProbe(accountId);
                UsageAccountSettings? account = _Settings.ModelTier.UsageRouting.Accounts.FirstOrDefault(a => String.Equals(a.Id, accountId, StringComparison.Ordinal));
                if (account != null) usage.GetLoginProblem(account, DateTime.UtcNow);
            };
            UsageAccountAdminService accountAdmin = new UsageAccountAdminService(
                _Settings,
                _AccountLogins,
                () => _Settings.SaveAsync(),
                token => _Database.Captains.EnumerateAsync(token),
                message => _Logging.Info(_Header + message),
                (eventType, message, accountId) => _ = EmitEventAsync(eventType, message, "usage_account", accountId));
            new UsageAccountLoginRoutes(_Settings, _AccountLogins, accountAdmin, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Fleets
            new FleetRoutes(_Database, EmitEventAsync, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Vessels
            new VesselRoutes(
                _Database,
                _VesselReadinessService,
                _LandingPreviewService,
                EmitEventAsync,
                _JsonOptions,
                _Docks,
                new VesselContextService(_Database, _RuntimeFactory, _Docks, _PromptTemplateService, _Logging),
                _Git as IBranchInventory,
                new VesselBranchWriteService(_Database, _Logging))
                .Register(_App, authenticate, _AuthorizationService);

            // Workspace
            new WorkspaceRoutes(_Database, _Workspace, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Workflow profiles
            new WorkflowProfileRoutes(_Database, _WorkflowProfileService, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Objectives
            new ObjectiveRoutes(_ObjectiveService, _GitHubIntegrationService, _ObjectiveDispatchPreviewService)
                .Register(_App, authenticate, _AuthorizationService);

            new ObjectiveRefinementRoutes(_Database, _ObjectiveRefinementSessions, _ObjectiveService, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Environments
            new EnvironmentRoutes(_EnvironmentService)
                .Register(_App, authenticate, _AuthorizationService);

            // Managed model endpoints (embedding/inference)
            new ModelEndpointRoutes(_ModelEndpointService)
                .Register(_App, authenticate, _AuthorizationService);

            // Structured check runs
            new CheckRunRoutes(_Database, _CheckRunService, _GitHubIntegrationService, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Releases
            new ReleaseRoutes(_ReleaseService, _ObjectiveService, _GitHubIntegrationService)
                .Register(_App, authenticate, _AuthorizationService);

            // Deployments
            new DeploymentRoutes(_DeploymentService, _ObjectiveService, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Incidents
            new IncidentRoutes(_IncidentService, _ObjectiveService)
                .Register(_App, authenticate, _AuthorizationService);

            // Runbooks
            new RunbookRoutes(_RunbookService)
                .Register(_App, authenticate, _AuthorizationService);

            // Request history
            new RequestHistoryRoutes(_Database, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Inbox (needs you)
            new InboxRoutes(new InboxService(_Database, _Logging), _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Cross-entity history
            new HistoryRoutes(_HistoricalTimelineService)
                .Register(_App, authenticate, _AuthorizationService);

            // Voyages
            new VoyageRoutes(_Database, _Admiral, EmitEventAsync, _WebSocketHub, _Logging, _ObjectiveService, _CodeIndex, _Settings, _JsonOptions, _ObjectiveDispatchPreviewService)
                .Register(_App, authenticate, _AuthorizationService);

            // Missions
            new MissionRoutes(_Database, _Admiral, _MissionService, _Settings, _Git, _LandingService, _LandingPreviewService, _GitHubIntegrationService, EmitEventAsync, _WebSocketHub, _Logging, _JsonOptions, _StatusTransitions)
                .Register(_App, authenticate, _AuthorizationService);

            // Captains
            new CaptainRoutes(_Database, _Admiral, _Settings, _RuntimeFactory, _AgentLifecycle, _CaptainTools, EmitEventAsync, _JsonOptions, _PlanningSessions, _ObjectiveRefinementSessions, _Logging, _CaptainQuarantine)
                .Register(_App, authenticate, _AuthorizationService);

            // Runtime helpers
            new RuntimeRoutes(_Logging)
                .Register(_App, authenticate, _AuthorizationService);

            // Planning sessions
            new PlanningSessionRoutes(_Database, _PlanningSessions, _ObjectiveService, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Docks
            new DockRoutes(_Database, _Docks, EmitEventAsync, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Signals
            new SignalRoutes(_Database, EmitEventAsync, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Coordination board (chatroom)
            new CoordinationRoutes(_Database, _CoordinationService, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Events
            new EventRoutes(_Database, EmitEventAsync, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Token usage
            new TokenUsageRoutes(_Database, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Verified production baseline
            new ProductionRoutes(new VerifiedProductionSummaryService(_Database))
                .Register(_App, authenticate, _AuthorizationService);

            // Merge queue
            new MergeQueueRoutes(_Database, _MergeQueue, EmitEventAsync, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Background jobs
            new Routes.JobRoutes(_Database, _JobService)
                .Register(_App, authenticate, _AuthorizationService);

            // Prompt templates
            new PromptTemplateRoutes(_Database, _PromptTemplateService, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Playbooks
            new PlaybookRoutes(_Database, _Logging, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Personas
            new MemoryRoutes(new MemoryService(_Database, _Logging), _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            new PersonaRoutes(_Database, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Pipelines
            new PipelineRoutes(_Database, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Backup & restore
            new BackupRoutes(new DatabaseBackupService(_Database, _Settings), _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Code index graph queries
            new CodeIndexRoutes(_CodeIndex, _Database, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Project profiles
            new ProjectProfileRoutes(_Database, new ProjectProfileService(_Database, _Logging), _PromptTemplateService, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Skills directory
            new SkillRoutes(_Database, _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);

            // Ask Armada assistant
            new AskRoutes(
                new AskArmadaService(_Database, _Admiral, _Logging),
                new CaptainChatService(_Database, _RuntimeFactory, _WebSocketHub, _PromptTemplateService, _Logging, _Settings, _SessionTokenService),
                _JsonOptions)
                .Register(_App, authenticate, _AuthorizationService);
        }

        private void InitializeDashboard()
        {
            // Check for explicit DashboardPath setting
            if (!String.IsNullOrEmpty(_Settings.DashboardPath))
            {
                string path = _Settings.DashboardPath;
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(_Settings.DataDirectory, path);

                if (Directory.Exists(path))
                {
                    Dashboard.StaticFileHandler.SetExternalPath(path);
                    _Logging.Info(_Header + "dashboard serving from external path: " + path);
                    return;
                }
                else
                {
                    _Logging.Warn(_Header + "configured DashboardPath not found: " + path + ", trying auto-detection");
                }
            }

            // Auto-detect: check for a 'dashboard' directory in the data directory
            string dashboardInData = Path.Combine(_Settings.DataDirectory, "dashboard");
            if (Directory.Exists(dashboardInData) && File.Exists(Path.Combine(dashboardInData, "index.html")))
            {
                Dashboard.StaticFileHandler.SetExternalPath(dashboardInData);
                _Logging.Info(_Header + "dashboard auto-detected at: " + dashboardInData);
                return;
            }

            // Auto-detect: check next to the server executable
            string? exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (exeDir != null)
            {
                string dashboardNextToExe = Path.Combine(exeDir, "dashboard");
                if (Directory.Exists(dashboardNextToExe) && File.Exists(Path.Combine(dashboardNextToExe, "index.html")))
                {
                    Dashboard.StaticFileHandler.SetExternalPath(dashboardNextToExe);
                    _Logging.Info(_Header + "dashboard auto-detected at: " + dashboardNextToExe);
                    return;
                }
            }

            string? sourceDashboardDist = TryFindSourceDashboardDist();
            if (sourceDashboardDist != null)
            {
                Dashboard.StaticFileHandler.SetExternalPath(sourceDashboardDist);
                _Logging.Info(_Header + "dashboard auto-detected source React build at: " + sourceDashboardDist);
                return;
            }

            _Logging.Warn(_Header + "no dashboard directory found; /dashboard serves nothing until DashboardPath or a dashboard directory with index.html exists");
        }

        private static string? TryFindSourceDashboardDist()
        {
            List<string> startDirectories = new List<string>();
            AddCandidateStart(startDirectories, AppContext.BaseDirectory);
            AddCandidateStart(startDirectories, Directory.GetCurrentDirectory());

            string? exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            AddCandidateStart(startDirectories, exeDir);

            foreach (string startDirectory in startDirectories)
            {
                DirectoryInfo? current = new DirectoryInfo(startDirectory);
                for (int depth = 0; current != null && depth < 8; depth++, current = current.Parent)
                {
                    foreach (string candidate in EnumerateSourceDashboardCandidates(current.FullName))
                    {
                        if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html")))
                        {
                            return Path.GetFullPath(candidate);
                        }
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateSourceDashboardCandidates(string baseDirectory)
        {
            yield return Path.Combine(baseDirectory, "src", "Armada.Dashboard", "dist");
            yield return Path.Combine(baseDirectory, "Armada.Dashboard", "dist");
            yield return Path.Combine(baseDirectory, "dist");
        }

        private static void AddCandidateStart(List<string> directories, string? directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) return;

            string fullPath = Path.GetFullPath(directory);
            if (directories.Contains(fullPath)) return;

            directories.Add(fullPath);
        }

        private static void ApplyCorsHeaders(HttpContextBase ctx)
        {
            if (!ctx.Response.Headers.AllKeys.Contains("Access-Control-Allow-Origin"))
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            if (!ctx.Response.Headers.AllKeys.Contains("Access-Control-Allow-Methods"))
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, PATCH, OPTIONS");
            if (!ctx.Response.Headers.AllKeys.Contains("Access-Control-Allow-Headers"))
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Api-Key, X-Token, Authorization");
        }

        private async Task CaptureRequestHistoryAsync(HttpContextBase ctx)
        {
            try
            {
                string route = ctx.Request.Url.RawWithoutQuery ?? String.Empty;
                if (!_RequestHistoryCapture.ShouldCapture(route)) return;

                AuthContext? auth = null;
                _RequestAuthContexts.TryGetValue(ctx, out auth);

                RequestHistoryCaptureInput input = new RequestHistoryCaptureInput
                {
                    Method = ctx.Request.Method.ToString().ToUpperInvariant(),
                    Route = route,
                    RouteTemplate = route,
                    QueryString = ExtractQueryString(ctx),
                    StatusCode = ctx.Response.StatusCode,
                    DurationMs = Math.Round(ctx.Timestamp.TotalMs ?? 0, 2),
                    RequestSizeBytes = ctx.Request.ContentLength,
                    ResponseSizeBytes = ctx.Response.ContentLength,
                    RequestContentType = ctx.Request.ContentType,
                    ResponseContentType = ctx.Response.ContentType,
                    ClientIp = ctx.Request.Source?.IpAddress?.ToString(),
                    CorrelationId = ctx.Request.Headers.Get("X-Correlation-Id") ?? ctx.Request.Headers.Get("X-Request-Id"),
                    RequestHeaders = ExtractHeaders(ctx.Request.Headers),
                    ResponseHeaders = ExtractHeaders(ctx.Response.Headers),
                    RequestBodyText = ReadBodySnapshot(ctx.Request.ContentType, ctx.Request.ContentLength, () => ctx.Request.DataAsString),
                    ResponseBodyText = ReadBodySnapshot(ctx.Response.ContentType, ctx.Response.ContentLength, () => ctx.Response.DataAsString)
                };

                RequestHistoryRecord record = _RequestHistoryCapture.BuildRecord(auth, input);
                await _Database.RequestHistory.CreateAsync(record.Entry, record.Detail, _TokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "request history capture error: " + ex.Message);
            }
        }

        private static Dictionary<string, string?> ExtractHeaders(System.Collections.Specialized.NameValueCollection headers)
        {
            Dictionary<string, string?> results = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (string? key in headers.AllKeys)
            {
                if (String.IsNullOrWhiteSpace(key)) continue;
                results[key] = headers.Get(key);
            }
            return results;
        }

        private string? ReadBodySnapshot(string? contentType, long contentLength, Func<string?> reader)
        {
            int maxPreviewBytes = Math.Max(_Settings.RequestHistoryMaxBodyBytes * 4, _Settings.RequestHistoryMaxBodyBytes);
            if (contentLength > maxPreviewBytes && !IsTextualContent(contentType)) return null;
            if (contentLength > maxPreviewBytes && String.IsNullOrWhiteSpace(contentType)) return null;

            try
            {
                return reader();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsTextualContent(string? contentType)
        {
            if (String.IsNullOrWhiteSpace(contentType)) return true;
            return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ExtractQueryString(HttpContextBase ctx)
        {
            string? rawWithQuery = ctx.Request.Url.RawWithQuery;
            int idx = !String.IsNullOrWhiteSpace(rawWithQuery) ? rawWithQuery.IndexOf('?') : -1;
            if (!String.IsNullOrWhiteSpace(rawWithQuery) && idx >= 0)
            {
                if (idx == rawWithQuery.Length - 1) return null;
                return rawWithQuery.Substring(idx + 1);
            }

            object? url = ctx.Request.Url;
            string? reflectedUrlQuery = ExtractQueryStringFromObject(url);
            if (!String.IsNullOrWhiteSpace(reflectedUrlQuery))
                return reflectedUrlQuery;

            return ExtractQueryStringFromObject(ctx.Request);
        }

        private static string? ExtractQueryStringFromObject(object? source)
        {
            if (source == null) return null;

            Type type = source.GetType();

            foreach (string propertyName in new[] { "Querystring", "QueryString", "Query" })
            {
                System.Reflection.PropertyInfo? property = type.GetProperty(propertyName);
                if (property == null) continue;

                object? value = property.GetValue(source);
                string? serialized = SerializeQueryValue(value);
                if (!String.IsNullOrWhiteSpace(serialized))
                    return serialized;
            }

            return null;
        }

        private static string? SerializeQueryValue(object? value)
        {
            if (value == null) return null;

            if (value is string stringValue)
            {
                if (String.IsNullOrWhiteSpace(stringValue)) return null;
                return stringValue.StartsWith("?") ? stringValue.Substring(1) : stringValue;
            }

            if (value is System.Collections.Specialized.NameValueCollection nameValueCollection)
            {
                List<string> parts = new List<string>();
                foreach (string? key in nameValueCollection.AllKeys)
                {
                    if (String.IsNullOrWhiteSpace(key)) continue;
                    string? itemValue = nameValueCollection.Get(key);
                    parts.Add(String.IsNullOrEmpty(itemValue)
                        ? Uri.EscapeDataString(key)
                        : Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(itemValue));
                }

                return parts.Count > 0 ? String.Join("&", parts) : null;
            }

            if (value is System.Collections.IDictionary dictionary)
            {
                List<string> parts = new List<string>();
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    if (entry.Key == null) continue;
                    string key = entry.Key.ToString() ?? String.Empty;
                    if (String.IsNullOrWhiteSpace(key)) continue;

                    string? itemValue = entry.Value?.ToString();
                    parts.Add(String.IsNullOrEmpty(itemValue)
                        ? Uri.EscapeDataString(key)
                        : Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(itemValue));
                }

                return parts.Count > 0 ? String.Join("&", parts) : null;
            }

            if (value is System.Collections.IEnumerable enumerable)
            {
                List<string> parts = new List<string>();
                foreach (object? item in enumerable)
                {
                    if (item == null) continue;

                    Type itemType = item.GetType();
                    System.Reflection.PropertyInfo? keyProperty = itemType.GetProperty("Key");
                    System.Reflection.PropertyInfo? valueProperty = itemType.GetProperty("Value");
                    if (keyProperty == null || valueProperty == null) continue;

                    string? key = keyProperty.GetValue(item)?.ToString();
                    if (String.IsNullOrWhiteSpace(key)) continue;

                    string? itemValue = valueProperty.GetValue(item)?.ToString();
                    parts.Add(String.IsNullOrEmpty(itemValue)
                        ? Uri.EscapeDataString(key)
                        : Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(itemValue));
                }

                return parts.Count > 0 ? String.Join("&", parts) : null;
            }

            return null;
        }

        /// <summary>
        /// Default route used when no other route matches. Serves the static dashboard
        /// assets, the SPA index fallback, and a JSON 404 for anything else.
        /// </summary>
        private async Task DashboardDefaultRouteAsync(HttpContextBase ctx)
        {
            string path = ctx.Request.Url.RawWithoutQuery;

            // Redirect root to dashboard
            if (path == "/" || path == "")
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.Headers.Add("Location", "/dashboard");
                await ctx.Response.Send().ConfigureAwait(false);
                return;
            }

            // Serve dashboard static files
            if (path.StartsWith("/dashboard"))
            {
                if (Dashboard.StaticFileHandler.TryGetFile(path, out byte[] content, out string contentType))
                {
                    ctx.Response.ContentType = contentType;
                    await ctx.Response.Send(content).ConfigureAwait(false);
                    return;
                }

                // SPA fallback: serve index.html for unmatched dashboard routes
                // (React router handles client-side routing)
                if (Dashboard.StaticFileHandler.TryGetIndex(out byte[] indexContent, out string indexType))
                {
                    ctx.Response.ContentType = indexType;
                    await ctx.Response.Send(indexContent).ConfigureAwait(false);
                    return;
                }
            }

            // Also serve /img/* and /assets/* at root level for the React dashboard
            // (Vite builds reference assets from root, not /dashboard/)
            if (path.StartsWith("/assets/") || path.StartsWith("/img/"))
            {
                string dashPath = "/dashboard" + path;
                if (Dashboard.StaticFileHandler.TryGetFile(dashPath, out byte[] assetContent, out string assetType))
                {
                    ctx.Response.ContentType = assetType;
                    await ctx.Response.Send(assetContent).ConfigureAwait(false);
                    return;
                }
            }

            // 404 for everything else
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Send("{\"error\":\"Not found\"}").ConfigureAwait(false);
        }

        private void RegisterMcpTools()
        {
            _ContextRetrieval = BuildContextRetrievalService();

            McpToolRegistrar.RegisterAll(
                _McpServer.RegisterTool,
                _Database,
                _Admiral,
                _Settings,
                _Git,
                _MergeQueue,
                _Docks,
                _LandingService,
                () => Stop(),
                async (captainId) =>
                {
                    Captain? captain = await _Database.Captains.ReadAsync(captainId).ConfigureAwait(false);
                    if (captain != null)
                        await _AgentLifecycle.HandleStopAgentAsync(captain).ConfigureAwait(false);
                },
                _AgentLifecycle,
                _PromptTemplateService,
                _Logging,
                _RemoteTriggerService,
                _CodeIndex,
                checkRunService: _CheckRunService,
                objectiveService: _ObjectiveService,
                planningSessionCoordinator: _PlanningSessions,
                objectiveRefinementCoordinator: _ObjectiveRefinementSessions,
                releaseService: _ReleaseService,
                cdWebhookDispatcher: _ReleaseWebhookDispatcher,
                deploymentService: _DeploymentService,
                runbookService: _RunbookService,
                incidentService: _IncidentService,
                objectiveScheduler: _ObjectiveScheduler,
                captainQuarantine: _CaptainQuarantine,
                unlandedBranches: new UnlandedBranchService(_Database, new GitService(_Logging), _Logging),
                terminalVoyageMissions: _TerminalVoyageMissions,
                diskLifecycle: _DiskLifecycle,
                longRunningJobs: _LongRunningJobs,
                coordinationService: _CoordinationService,
                dispatchHold: _DispatchHold,
                objectiveDispatchPreviewService: _ObjectiveDispatchPreviewService,
                statusTransitions: _StatusTransitions,
                harborJobs: _HarborJobService,
                typedDecisionClient: _TypedDecisionClient,
                typedDecisionRecorder: _TypedDecisionRecorder,
                typedDecisionParticipantKeyProvider: () => ArmadaMcpHttpServer.CurrentParticipantKey,
                typedDecisionEval: _TypedDecisionEval,
                typedDecisionSamples: _TypedDecisionSamples,
                papercutMergeAdapter: _PapercutMergeAdapter,
                inboxTriageAdapter: _InboxTriageAdapter,
                followUpRoutingAdapter: _FollowUpRoutingAdapter,
                changeQualityAdapter: _ChangeQualityAdapter,
                changeQualityFollowUpRouter: _FollowUpRouter,
                contextRetrieval: _ContextRetrieval,
                contextParticipantKeyProvider: () => ArmadaMcpHttpServer.CurrentParticipantKey,
                missionService: _MissionService);

        }

        private async Task EmitEventAsync(string eventType, string message,
            string? entityType = null, string? entityId = null,
            string? captainId = null, string? missionId = null,
            string? vesselId = null, string? voyageId = null)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent(eventType, message);
                evt.EntityType = entityType;
                evt.EntityId = entityId;
                evt.CaptainId = captainId;
                evt.MissionId = missionId;
                evt.VesselId = vesselId;
                evt.VoyageId = voyageId;
                Armada.Core.Models.EventOwnerScopeResult scope = await Armada.Core.Services.EventOwnerScope.ApplyAsync(_Database, evt).ConfigureAwait(false);
                if (scope.Outcome == Armada.Core.Enums.EventOwnerScopeOutcomeEnum.LookupFailed)
                    _Logging.Warn(_Header + "event " + eventType + " written without owner scope: " + scope.Detail);
                await _Database.Events.CreateAsync(evt).ConfigureAwait(false);

                // Broadcast to WebSocket clients
                if (_WebSocketHub != null)
                {
                    // The event already carries its owner's scope, so it reaches only the sessions
                    // that may read the record it describes; an ownerless event stays admin-only.
                    _WebSocketHub.BroadcastEvent(eventType, message, new
                    {
                        entityType = entityType,
                        entityId = entityId,
                        captainId = captainId,
                        missionId = missionId,
                        vesselId = vesselId,
                        voyageId = voyageId
                    }, Armada.Server.WebSocket.WebSocketDeliveryScope.ForOwner(evt.TenantId, evt.UserId));
                }

                // Mirror selected fleet events onto the coordination board so concurrent
                // operator sessions see fleet activity in the chatroom.
                try
                {
                    string? note = CoordinationService.BuildSystemNoteContent(
                        eventType, message, entityType, entityId, voyageId, missionId, vesselId);
                    if (note != null)
                    {
                        await _CoordinationService.PostMessageAsync(
                            CoordinationService.DefaultRoomKey,
                            Armada.Core.Enums.CoordinationAuthorTypeEnum.System,
                            null,
                            "armada",
                            note,
                            voyageId,
                            missionId,
                            vesselId,
                            null,
                            null).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "failed to mirror event to coordination board: " + ex.Message);
                }

                await _RemoteTunnel.PublishEventAsync(eventType, new
                {
                    message = message,
                    entityType = entityType,
                    entityId = entityId,
                    captainId = captainId,
                    missionId = missionId,
                    vesselId = vesselId,
                    voyageId = voyageId
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error emitting event: " + ex.Message);
            }
        }

        private async Task HealthCheckLoopAsync(CancellationToken token)
        {
            // Reset captains left in Working state with dead processes from previous server run
            try
            {
                await _Admiral.CleanupStaleCaptainsAsync(token).ConfigureAwait(false);
                _Logging.Info(_Header + "startup stale captain cleanup completed");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "startup stale captain cleanup error: " + ex.Message);
            }

    // Recover landing jobs before the first health check so the steady-state
    // merge reconciler cannot process the same in-flight entry concurrently.
    try
    {
        int recovered = await _MergeQueue.RecoverInFlightLandingsAsync(token).ConfigureAwait(false);
        if (recovered > 0)
        {
            _Logging.Info(_Header + "startup landing recovery resumed " + recovered + " in-flight entr" + (recovered == 1 ? "y" : "ies"));
        }
    }
    catch (Exception ex)
    {
        _Logging.Warn(_Header + "startup landing recovery error: " + ex.Message);
    }

    // Cancel Checks left Running by the stopped process: their command died with it, so they can
    // never reach a verdict and would hold a Judge PASS forever.
    try
    {
        int cancelledChecks = await _CheckRunService.CancelInterruptedRunsAsync(_StartUtc, token).ConfigureAwait(false);
        if (cancelledChecks > 0)
        {
            _Logging.Info(_Header + "startup cancelled " + cancelledChecks + " check run"
                + (cancelledChecks == 1 ? "" : "s") + " left Running by a stopped admiral");
        }
    }
    catch (Exception ex)
    {
        _Logging.Warn(_Header + "startup interrupted-check cancellation error: " + ex.Message);
    }

    // Reconcile owned disk storage at startup: purge stale sibling leases left by a crashed
    // Admiral and record the first dry-run byte report. Deletion only happens when the
    // diskLifecycle settings opt in, so startup is always safe.
    try
    {
        await _DiskLifecycle.ReconcileAsync(token).ConfigureAwait(false);
        _Logging.Info(_Header + "startup disk lifecycle reconciliation completed");
    }
    catch (Exception ex)
    {
        _Logging.Warn(_Header + "startup disk lifecycle reconciliation error: " + ex.Message);
    }

            // Run an immediate health check on startup to dispatch any pending missions
            try
            {
                await _Admiral.HealthCheckAsync(token).ConfigureAwait(false);
                _AutomaticCheckRuns.TriggerBackgroundSweep(token);
                _AutonomousRecovery.TriggerBackgroundSweep(token);
                _IncidentLifecycle.TriggerBackgroundSweep(token);
                _ObjectiveScheduler.TriggerBackgroundSweep(token);
                _Logging.Info(_Header + "startup health check completed");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "startup health check error: " + ex.Message);
            }

            List<HealthLoopMaintenanceStep> maintenanceSteps = BuildHealthLoopMaintenanceSteps();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_HealthLoopInterval ?? TimeSpan.FromSeconds(_Settings.HeartbeatIntervalSeconds), token).ConfigureAwait(false);

                    // The health check is isolated from maintenance: a health check that throws on
                    // every tick must not stop the cycle count, or no periodic step would ever run.
                    try
                    {
                        await _Admiral.HealthCheckAsync(token).ConfigureAwait(false);
                        _AutomaticCheckRuns.TriggerBackgroundSweep(token);
                        _AutonomousRecovery.TriggerBackgroundSweep(token);
                        _IncidentLifecycle.TriggerBackgroundSweep(token);
                        _ObjectiveScheduler.TriggerBackgroundSweep(token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "health check error: " + ex.Message);
                    }

                    _HealthCheckCycles++;
                    await HealthLoopMaintenanceRunner.RunDueStepsAsync(
                        _HealthCheckCycles,
                        maintenanceSteps,
                        (name, ex) => _Logging.Warn(_Header + name + " failed: " + ex.Message),
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "health loop error: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Periodic maintenance steps of the health loop, in execution order. Intervals are read on
        /// each cycle, so a settings change applies without a restart.
        /// </summary>
        private List<HealthLoopMaintenanceStep> BuildHealthLoopMaintenanceSteps()
        {
            return new List<HealthLoopMaintenanceStep>
            {
                // Reap background jobs whose worker died so they do not hang in Running.
                HealthLoopMaintenanceStep.EveryCycles("job maintenance", () => 1,
                    async stepToken => await _JobService.MaintainAsync(stepToken).ConfigureAwait(false)),

                // Close objective dispatch attempts whose process stopped between voyage creation and linking.
                HealthLoopMaintenanceStep.EveryCycles("dispatch attempt reconciliation", () => 1, async stepToken =>
                {
                    ObjectiveDispatchAttemptReconciliationResult attempts = await _ObjectiveService
                        .ReconcileDispatchAttemptsAsync(_Admiral.RecallCaptainAsync, stepToken).ConfigureAwait(false);
                    if (attempts.Kept > 0 || attempts.CancelledOrphans > 0 || attempts.Unresolved.Count > 0)
                    {
                        _Logging.Warn(_Header + "dispatch attempt reconciliation kept=" + attempts.Kept
                            + " cancelledOrphans=" + attempts.CancelledOrphans
                            + " unresolved=" + String.Join("; ", attempts.Unresolved));
                    }
                }),

                HealthLoopMaintenanceStep.EveryCycles("log rotation", () => 10, stepToken =>
                {
                    _LogRotation.RotateAllInDirectory(Path.Combine(_Settings.LogDirectory, "captains"));
                    _LogRotation.RotateIfNeeded(Path.Combine(_Settings.LogDirectory, "admiral.log"));
                    return Task.CompletedTask;
                }),

                HealthLoopMaintenanceStep.EveryCycles("planning session maintenance", () => 10,
                    async stepToken => await _PlanningSessions.MaintainSessionsAsync(stepToken).ConfigureAwait(false)),

                HealthLoopMaintenanceStep.EveryCycles("data expiry", () => 100,
                    async stepToken => await _DataExpiry.PurgeExpiredDataAsync(stepToken).ConfigureAwait(false)),

                // Reap background jobs stuck in Accepted or Running past the stale threshold, so a hung
                // or dead background worker reaches a terminal status instead of reading as in-flight.
                HealthLoopMaintenanceStep.EveryCycles("background job stale reap", () => 1, async stepToken =>
                {
                    int reaped = await _LongRunningJobs.ReapStaleJobsAsync(token: stepToken).ConfigureAwait(false);
                    if (reaped > 0)
                    {
                        _Logging.Warn(_Header + "reaped " + reaped + " stale background job" + (reaped == 1 ? "" : "s"));
                    }
                }),

                // Observability and stale-lease purging always run; deletion is gated by diskLifecycle settings.
                HealthLoopMaintenanceStep.EveryCycles("disk lifecycle reconciliation", () => _Settings.DiskLifecycle.ReconcileIntervalCycles,
                    async stepToken => await _DiskLifecycle.ReconcileAsync(stepToken).ConfigureAwait(false)),

                // Detect HEAD changes that did not go through an Armada landing and schedule a reindex,
                // so the dispatch guard does not silently block on a stale index.
                new HealthLoopMaintenanceStep("code index staleness sweep",
                    cycle => _CodeIndex != null
                        && _Settings.CodeIndex.Enabled
                        && cycle % Math.Max(1, _Settings.CodeIndex.StalenessSweepIntervalCycles) == 0,
                    async stepToken => await _CodeIndex!.SweepStalenessAsync(stepToken).ConfigureAwait(false)),

                // Remove landed Armada branches and expired landed preserved refs; the sweep logs its own summary.
                HealthLoopMaintenanceStep.EveryCycles("branch cleanup sweep", () => _Settings.BranchCleanupSweepIntervalCycles,
                    async stepToken => await _BranchCleanupSweep.SweepAsync(stepToken).ConfigureAwait(false)),

                // Move WorkProduced missions under recently ended voyages to a terminal status from landing
                // evidence; no voyage-ending path does it. Older rows are repaired by the operator tool.
                HealthLoopMaintenanceStep.EveryCycles("terminal voyage mission reconciliation", () => 10, async stepToken =>
                {
                    TerminalVoyageMissionReconciliationRequest request = new TerminalVoyageMissionReconciliationRequest
                    {
                        DryRun = false,
                        IncludeHistorical = false,
                        MaxItems = 0
                    };
                    await _TerminalVoyageMissions.ReconcileAsync(request, stepToken).ConfigureAwait(false);
                }),

                // D13 owner_digest. The runner self-guards to once per UTC day and returns immediately
                // while the decision is Off, so this step is cheap to schedule often; it no-ops until a
                // new day rolls with the decision enabled and owner-decision candidates present.
                HealthLoopMaintenanceStep.EveryCycles("owner decision digest", () => 60, async stepToken =>
                {
                    if (_OwnerDigestRunner == null) return;
                    OwnerDigestRunResult digest = await _OwnerDigestRunner.RunOnceAsync(stepToken).ConfigureAwait(false);
                    if (digest.Posted)
                        _Logging.Info(_Header + "owner decision digest posted: " + digest.CandidateCount + " question(s)");
                }),

                // D18 memory_candidate. The sweep self-guards to once per seven days and returns at once
                // while the decision is Off, so scheduling it as often as the digest costs nothing.
                HealthLoopMaintenanceStep.EveryCycles("papercut memory sweep", () => 60, async stepToken =>
                {
                    if (_PapercutMemorySweepRunner == null) return;
                    PapercutMemorySweepResult sweep = await _PapercutMemorySweepRunner.RunOnceAsync(stepToken).ConfigureAwait(false);
                    if (sweep.Nominated > 0)
                        _Logging.Info(_Header + "papercut memory sweep stored " + sweep.Nominated + " memory proposal(s)");
                })
            };
        }

        private async Task ModelEndpointHealthLoopAsync(CancellationToken token)
        {
            await ModelEndpointHealthSweepRunner.RunAsync(
                async sweepToken => await _ModelEndpointService.CheckHealthAllAsync(sweepToken).ConfigureAwait(false),
                TimeSpan.FromMilliseconds(Math.Max(1, _Settings.HeartbeatIntervalSeconds) * 1000),
                ex => _Logging.Warn(_Header + "model endpoint health sweep error: " + ex.Message),
                token).ConfigureAwait(false);
        }

        private async Task<RemoteTunnelRequestResult> HandleRemoteTunnelRequestAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string method = envelope.Method?.Trim().ToLowerInvariant() ?? String.Empty;
            switch (method)
            {
                case "armada.http.request":
                case "armada.ws.open":
                case "armada.ws.message":
                case "armada.ws.close":
                    return await _RemoteDashboardRelay.HandleAsync(envelope, token).ConfigureAwait(false);
            }

            return new RemoteTunnelRequestResult
            {
                StatusCode = 404,
                ErrorCode = "unsupported_method",
                Message = "Tunnel method " + envelope.Method + " is not supported. Use generic dashboard relay methods instead."
            };
        }

        #endregion
    }
}
