namespace Armada.Core.Settings
{
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Top-level application settings.
    /// </summary>
    public class ArmadaSettings
    {
        #region Public-Members

        /// <summary>
        /// Root data directory for Armada.
        /// </summary>
        public string DataDirectory
        {
            get => _DataDirectory;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(DataDirectory));
                _DataDirectory = value;
            }
        }

        /// <summary>
        /// Database file path.
        /// </summary>
        public string DatabasePath
        {
            get => _DatabasePath;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(DatabasePath));
                _DatabasePath = value;
                _DatabasePathConfigured = true;
            }
        }

        /// <summary>
        /// Log directory path.
        /// </summary>
        public string LogDirectory
        {
            get => _LogDirectory;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(LogDirectory));
                _LogDirectory = value;
            }
        }

        /// <summary>
        /// Directory for git worktree docks.
        /// </summary>
        public string DocksDirectory
        {
            get => _DocksDirectory;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(DocksDirectory));
                _DocksDirectory = value;
            }
        }

        /// <summary>
        /// Directory for bare repository clones.
        /// </summary>
        public string ReposDirectory
        {
            get => _ReposDirectory;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(ReposDirectory));
                _ReposDirectory = value;
            }
        }

        /// <summary>
        /// Admiral REST API port.
        /// </summary>
        public int AdmiralPort
        {
            get => _AdmiralPort;
            set
            {
                if (value < 1 || value > 65535) throw new ArgumentOutOfRangeException(nameof(AdmiralPort));
                _AdmiralPort = value;
            }
        }

        /// <summary>
        /// MCP server port.
        /// </summary>
        public int McpPort
        {
            get => _McpPort;
            set
            {
                if (value < 1 || value > 65535) throw new ArgumentOutOfRangeException(nameof(McpPort));
                _McpPort = value;
            }
        }

        /// <summary>
        /// Whether the WebSocket endpoint (/ws) is enabled on the Admiral port.
        /// When true, clients can connect to ws://host:port/ws for real-time events.
        /// </summary>
        public bool WebSocketEnabled { get; set; } = true;

        /// <summary>
        /// Heartbeat check interval in seconds. Must be >= 5.
        /// </summary>
        public int HeartbeatIntervalSeconds
        {
            get => _HeartbeatIntervalSeconds;
            set
            {
                if (value < 5) throw new ArgumentOutOfRangeException(nameof(HeartbeatIntervalSeconds), "Must be >= 5");
                _HeartbeatIntervalSeconds = value;
            }
        }

        /// <summary>
        /// Grace window in seconds for freshly launched active missions whose process ID
        /// has not yet been durably recorded. Clamped to [5, 300].
        /// </summary>
        public int LaunchProcessIdGraceSeconds
        {
            get => _LaunchProcessIdGraceSeconds;
            set => _LaunchProcessIdGraceSeconds = Math.Max(5, Math.Min(300, value));
        }

        /// <summary>
        /// How many times a mission is re-dispatched after its runtime reports an interrupted run (exit code -1
        /// from a stop, shutdown or restart) before the next interruption fails it. Counted per
        /// mission from its <c>mission.interrupted_redispatched</c> events and separate from the
        /// autonomous-rescue budget. Clamped to [0, 10]; 0 fails every interrupted run. Default 2.
        /// </summary>
        public int MaxInterruptedExitRedispatchAttempts
        {
            get => _MaxInterruptedExitRedispatchAttempts;
            set => _MaxInterruptedExitRedispatchAttempts = Math.Max(0, Math.Min(10, value));
        }

        /// <summary>
        /// Stall detection threshold in minutes. Must be >= 1.
        /// </summary>
        public int StallThresholdMinutes
        {
            get => _StallThresholdMinutes;
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(StallThresholdMinutes), "Must be >= 1");
                _StallThresholdMinutes = value;
            }
        }

        /// <summary>
        /// Stage-level watchdog timeout in minutes for Assigned/WorkProduced missions
        /// that have no captain/process heartbeat. Clamped to [5, 180].
        /// </summary>
        public int StageWatchdogTimeoutMinutes
        {
            get => _StageWatchdogTimeoutMinutes;
            set => _StageWatchdogTimeoutMinutes = Math.Max(5, Math.Min(180, value));
        }

        /// <summary>
        /// Maximum number of automatic landing retries when landing fails due to target-branch drift.
        /// Set to 0 to disable auto-retry. Must be in range [0, 10].
        /// </summary>
        public int MaxLandingRetries
        {
            get => _MaxLandingRetries;
            set
            {
                if (value < 0) value = 0;
                if (value > 10) value = 10;
                _MaxLandingRetries = value;
            }
        }

        /// <summary>
        /// Global landing mode for completed missions. Determines how work is integrated.
        /// When set, takes precedence over the legacy boolean flags (AutoPush, AutoCreatePullRequests, AutoMergePullRequests).
        /// Can be overridden per-vessel or per-voyage.
        /// Resolution order: voyage.LandingMode > vessel.LandingMode > settings.LandingMode > derive from booleans.
        /// </summary>
        public LandingModeEnum? LandingMode { get; set; } = null;

        /// <summary>
        /// Global branch cleanup policy after successful landing.
        /// Can be overridden per-vessel. Default: LocalAndRemote.
        /// </summary>
        public BranchCleanupPolicyEnum BranchCleanupPolicy { get; set; } = BranchCleanupPolicyEnum.LocalAndRemote;

        /// <summary>
        /// Whether to automatically push changes to the remote on mission completion.
        /// Legacy setting — prefer LandingMode when possible.
        /// </summary>
        public bool AutoPush { get; set; } = true;

        /// <summary>
        /// Whether to automatically create pull requests on mission completion.
        /// Legacy setting — prefer LandingMode when possible.
        /// Requires AutoPush to be effective.
        /// </summary>
        public bool AutoCreatePullRequests { get; set; } = false;

        /// <summary>
        /// Whether to automatically merge pull requests after creation.
        /// Legacy setting — prefer LandingMode when possible.
        /// Requires AutoCreatePullRequests to be effective.
        /// </summary>
        public bool AutoMergePullRequests { get; set; } = false;

        /// <summary>
        /// Maximum number of auto-recovery attempts for stalled captains. Must be >= 0.
        /// </summary>
        public int MaxRecoveryAttempts
        {
            get => _MaxRecoveryAttempts;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(MaxRecoveryAttempts), "Must be >= 0");
                _MaxRecoveryAttempts = value;
            }
        }

        /// <summary>
        /// Maximum log file size in bytes before rotation. Must be >= 1024.
        /// </summary>
        public long MaxLogFileSizeBytes
        {
            get => _MaxLogFileSizeBytes;
            set
            {
                if (value < 1024) throw new ArgumentOutOfRangeException(nameof(MaxLogFileSizeBytes), "Must be >= 1024");
                _MaxLogFileSizeBytes = value;
            }
        }

        /// <summary>
        /// Maximum number of rotated log files to keep per captain. Must be >= 1.
        /// </summary>
        public int MaxLogFileCount
        {
            get => _MaxLogFileCount;
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(MaxLogFileCount), "Must be >= 1");
                _MaxLogFileCount = value;
            }
        }

        /// <summary>
        /// Syslog targets for process-level logging.
        /// Defaults to a local syslog listener on 127.0.0.1:514.
        /// </summary>
        public List<SyslogServer> SyslogServers { get; set; } = new List<SyslogServer>
        {
            new SyslogServer("127.0.0.1", 514)
        };

        /// <summary>
        /// Data retention period in days for completed voyages, missions, signals, and events.
        /// Records older than this are purged by the background expiry task.
        /// Set to 0 to disable automatic expiry. Must be >= 0.
        /// </summary>
        public int DataRetentionDays
        {
            get => _DataRetentionDays;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(DataRetentionDays), "Must be >= 0");
                _DataRetentionDays = value;
            }
        }

        /// <summary>
        /// Retention period in days for the append-only production metric facts: mission attempt
        /// facts, preparation claim observations and lane state transitions. Facts older than this
        /// are purged by the background expiry task, and production summary windows before it report
        /// those measures as unobserved. Defaults to 365. Set to 0 to keep facts forever. Must be >= 0.
        /// </summary>
        public int ProductionFactRetentionDays
        {
            get => _ProductionFactRetentionDays;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(ProductionFactRetentionDays), "Must be >= 0");
                _ProductionFactRetentionDays = value;
            }
        }

        /// <summary>
        /// Number of health-check cycles between maintenance sweeps that prune Armada-owned branches
        /// and preserved refs already landed on each vessel's default branch (self-healing ref
        /// accumulation). Defaults to 200 (~100 minutes at the default heartbeat).
        /// </summary>
        public int BranchCleanupSweepIntervalCycles
        {
            get => _BranchCleanupSweepIntervalCycles;
            set => _BranchCleanupSweepIntervalCycles = Math.Max(10, Math.Min(10080, value));
        }

        /// <summary>
        /// Days the branch cleanup sweep keeps a landed recovery ref written at dock reclaim
        /// (refs/armada-preserved/..., refs/armada/docks/... and refs/armada/missions/...), measured
        /// from the committer time of its tip. All three are written by the same reclaim step for the
        /// same purpose, recovering a produced commit, so they share one window. A ref whose tip is
        /// not an ancestor of the default branch is never removed. Zero keeps every such ref.
        /// Defaults to 14; clamped to 0-3650.
        /// </summary>
        public int BranchCleanupPreservedRefRetentionDays
        {
            get => _BranchCleanupPreservedRefRetentionDays;
            set => _BranchCleanupPreservedRefRetentionDays = Math.Max(0, Math.Min(3650, value));
        }

        /// <summary>
        /// Whether HTTP request-history capture is enabled.
        /// </summary>
        public bool RequestHistoryEnabled { get; set; } = true;

        /// <summary>
        /// Retention period in days for stored request history.
        /// Set to 0 to disable automatic request-history expiry.
        /// </summary>
        public int RequestHistoryRetentionDays
        {
            get => _RequestHistoryRetentionDays;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(RequestHistoryRetentionDays), "Must be >= 0");
                _RequestHistoryRetentionDays = value;
            }
        }

        /// <summary>
        /// Maximum number of request or response body bytes persisted per entry.
        /// Set to 0 to disable body capture.
        /// </summary>
        public int RequestHistoryMaxBodyBytes
        {
            get => _RequestHistoryMaxBodyBytes;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(RequestHistoryMaxBodyBytes), "Must be >= 0");
                _RequestHistoryMaxBodyBytes = value;
            }
        }

        /// <summary>
        /// Route prefixes or exact paths excluded from request-history capture.
        /// </summary>
        public List<string> RequestHistoryExcludeRoutes { get; set; } = new List<string>
        {
            "/api/v1/status/health",
            "/api/v1/request-history"
        };

        /// <summary>
        /// Whether sanitized request headers should be stored with request history.
        /// </summary>
        public bool RequestHistoryCaptureRequestHeaders { get; set; } = true;

        /// <summary>
        /// Whether sanitized response headers should be stored with request history.
        /// </summary>
        public bool RequestHistoryCaptureResponseHeaders { get; set; } = true;

        /// <summary>
        /// Retention period in days for stopped or failed planning sessions.
        /// Set to 0 to disable automatic planning-session deletion.
        /// </summary>
        public int PlanningSessionRetentionDays
        {
            get => _PlanningSessionRetentionDays;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(PlanningSessionRetentionDays), "Must be >= 0");
                _PlanningSessionRetentionDays = value;
            }
        }

        /// <summary>
        /// Inactivity timeout in minutes for active planning sessions with no running process.
        /// Set to 0 to disable automatic inactivity stop.
        /// </summary>
        public int PlanningSessionInactivityTimeoutMinutes
        {
            get => _PlanningSessionInactivityTimeoutMinutes;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(PlanningSessionInactivityTimeoutMinutes), "Must be >= 0");
                _PlanningSessionInactivityTimeoutMinutes = value;
            }
        }

        /// <summary>
        /// Abandonment timeout in minutes for planning sessions with no running process.
        /// Set to 0 to disable abandonment cleanup.
        /// </summary>
        public int PlanningSessionAbandonmentTimeoutMinutes
        {
            get => _PlanningSessionAbandonmentTimeoutMinutes;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(PlanningSessionAbandonmentTimeoutMinutes), "Must be >= 0");
                _PlanningSessionAbandonmentTimeoutMinutes = value;
            }
        }

        /// <summary>
        /// Minimum number of idle captains to maintain.
        /// When idle count drops below this, new captains are spawned automatically.
        /// Set to 0 to disable auto-scaling. Must be >= 0.
        /// </summary>
        public int MinIdleCaptains
        {
            get => _MinIdleCaptains;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(MinIdleCaptains), "Must be >= 0");
                _MinIdleCaptains = value;
            }
        }

        /// <summary>
        /// Maximum total captains allowed. Set to 0 for unlimited. Must be >= 0.
        /// </summary>
        public int MaxCaptains
        {
            get => _MaxCaptains;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(MaxCaptains), "Must be >= 0");
                _MaxCaptains = value;
            }
        }

        /// <summary>
        /// Maximum number of captain workloads (Assigned + InProgress missions) that may run
        /// concurrently across all vessels. Caps the combined memory pressure of running
        /// captain agent processes and the compilers/builds they spawn, preventing host OOM
        /// when many missions dispatch at once. Set to 0 to disable the global gate (unlimited).
        /// Must be >= 0. The per-vessel AllowConcurrentMissions flag still applies underneath
        /// this global cap.
        /// </summary>
        public int MaxConcurrentCaptainWorkloads
        {
            get => _MaxConcurrentCaptainWorkloads;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentCaptainWorkloads), "Must be >= 0");
                _MaxConcurrentCaptainWorkloads = value;
            }
        }

        /// <summary>
        /// Byte budget for a generated captain instruction file. Exceeding it does not block the mission;
        /// it emits a warning and is recorded in the mission.prompt_budget telemetry event, so an oversized
        /// brief cannot ship unnoticed. Set to 0 to disable the warning. Must be >= 0.
        /// </summary>
        public int CaptainInstructionByteBudget
        {
            get => _CaptainInstructionByteBudget;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(CaptainInstructionByteBudget), "Must be >= 0");
                _CaptainInstructionByteBudget = value;
            }
        }

        /// <summary>
        /// Names of licensed contexts available to captains, checked by dispatch preview against an
        /// objective's declared execution requirements. Names only; never license values or credentials.
        /// </summary>
        public List<string> AvailableLicensedContexts
        {
            get => _AvailableLicensedContexts;
            set => _AvailableLicensedContexts = value ?? new List<string>();
        }

        private List<string> _AvailableLicensedContexts = new List<string>();

        /// <summary>
        /// Absolute path to the shared AI-Memory root on the host where captains run, or null to omit
        /// the AI-Memory module from captain instructions.
        ///
        /// When set, every generated instruction file names the memory index once, for every runtime.
        /// Only the index path is emitted, never memory content: inlining it would re-create the very
        /// prompt bloat this module is measured against, and the captain can read what it needs.
        /// Set the path as it resolves on the captain's host; a workstation path handed to a server
        /// captain is a path it cannot open. The vessel's own folder under repos/ is matched by name
        /// with case and separators ignored, and is named by its real folder name.
        /// </summary>
        public string? AiMemoryRoot
        {
            get => _AiMemoryRoot;
            set => _AiMemoryRoot = String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>
        /// Whether to seed runtime MCP client configuration into every dock, pointing captains at the
        /// Armada MCP server.
        ///
        /// Default true. Each supported runtime receives runtime-appropriate dock or launch
        /// configuration; strict runtime isolation can ignore a project file that merely exists in
        /// the dock. Disable only when the deployment intentionally gives captains no Armada tools.
        /// </summary>
        public bool SeedDockRuntimeMcpConfig { get; set; } = true;

        /// <summary>
        /// Default test command for merge queue verification.
        /// Individual merge entries can override this.
        /// </summary>
        public string? MergeQueueTestCommand { get; set; } = null;

        /// <summary>
        /// Path to the GitHub CLI executable (gh). Default: gh (resolved via PATH).
        /// </summary>
        public string GhCliPath { get; set; } = "gh";

        /// <summary>
        /// Path to the GitLab CLI executable (glab). Default: glab (resolved via PATH).
        /// </summary>
        public string GlabCliPath { get; set; } = "glab";

        /// <summary>
        /// Optional API key for Admiral REST API authentication.
        /// Deprecated: retained for backward compatibility, maps to synthetic admin identity.
        /// </summary>
        public string? ApiKey { get; set; } = null;

        /// <summary>
        /// Optional global GitHub token used for Armada-managed GitHub integrations.
        /// A vessel-specific override takes precedence when present.
        /// </summary>
        public string? GitHubToken { get; set; } = null;

        /// <summary>
        /// Path to the external web dashboard directory (React build output).
        /// When set, the server serves static files from this directory at /dashboard.
        /// When null/empty, the server looks for a dashboard directory holding index.html in the data
        /// directory, next to the executable, and in the source React build output.
        /// </summary>
        public string? DashboardPath { get; set; } = null;

        /// <summary>
        /// Whether self-registration via POST /api/v1/onboarding is enabled.
        /// </summary>
        public bool AllowSelfRegistration { get; set; } = true;

        /// <summary>
        /// Whether POST /api/v1/server/stop requires authentication.
        /// When false (default), the shutdown endpoint is accessible without credentials,
        /// suitable for local development. When true, requires an authenticated identity,
        /// suitable for centralized or Docker deployments.
        /// </summary>
        public bool RequireAuthForShutdown { get; set; } = false;

        /// <summary>
        /// AES-256 encryption key for session tokens.
        /// Auto-generated if not provided.
        /// </summary>
        public string? SessionTokenEncryptionKey { get; set; } = null;

        /// <summary>
        /// Default agent runtime to use when auto-creating captains.
        /// Null means auto-detect from PATH.
        /// </summary>
        public string? DefaultRuntime { get; set; } = null;

        /// <summary>
        /// Hosted cloud providers an API-endpoint captain may run against. Empty by default: only
        /// operator-hosted Ollama and OpenAI-compatible endpoints run until an operator lists a
        /// provider here explicitly.
        /// </summary>
        public List<ModelProviderEnum> ApiCaptainCloudProviders { get; set; } = new List<ModelProviderEnum>();

        /// <summary>
        /// Enable desktop notifications on mission completion/failure.
        /// </summary>
        public bool Notifications { get; set; } = true;

        /// <summary>
        /// Ring terminal bell on mission completion/failure during watch.
        /// </summary>
        public bool TerminalBell { get; set; } = true;

        /// <summary>
        /// Minimum number of <c>AuditDeepPicked = true</c> entries pending review before
        /// admiral fires a desktop notification on the next health-check cycle. 0 disables
        /// the notification entirely. Default is 1 -- as soon as Judge flags one entry,
        /// the human gets pinged. Bump this when the audit drain is healthy and you want
        /// fewer pings (e.g. only ping when a backlog forms).
        /// </summary>
        public int AuditQueueNotifyThreshold { get; set; } = 1;

        /// <summary>
        /// Debounce window in minutes between consecutive audit-queue notifications. Once a
        /// notification fires, admiral waits at least this many minutes before firing
        /// another even if the queue depth is still above the threshold. Prevents a burst
        /// of landings from spamming the desktop. Default is 30 minutes.
        /// </summary>
        public int AuditQueueNotifyDebounceMinutes { get; set; } = 30;

        /// <summary>
        /// Idle captain timeout in seconds before auto-removal.
        /// 0 = disabled (captains persist indefinitely).
        /// </summary>
        public int IdleCaptainTimeoutSeconds
        {
            get => _IdleCaptainTimeoutSeconds;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(IdleCaptainTimeoutSeconds), "Must be >= 0");
                _IdleCaptainTimeoutSeconds = value;
            }
        }

        /// <summary>
        /// Per-runtime agent configuration.
        /// </summary>
        public List<AgentSettings> Agents { get; set; } = new List<AgentSettings>();

        /// <summary>
        /// Escalation rules for automated notifications.
        /// </summary>
        public List<EscalationRule> EscalationRules { get; set; } = new List<EscalationRule>();

        /// <summary>
        /// Database connection settings.
        /// When set, takes precedence over DatabasePath for database initialization.
        /// </summary>
        public DatabaseSettings Database
        {
            get => _Database;
            set => _Database = value ?? new DatabaseSettings();
        }

        /// <summary>
        /// REST API listener settings.
        /// </summary>
        public RestSettings Rest { get; set; } = new RestSettings();

        /// <summary>
        /// Message template settings for commit messages and PR descriptions.
        /// </summary>
        public MessageTemplateSettings MessageTemplates { get; set; } = new MessageTemplateSettings();

        /// <summary>
        /// Detached Harbor runner link settings. Harbor is disabled by default.
        /// </summary>
        public HarborSettings Harbor
        {
            get => _Harbor;
            set => _Harbor = value ?? new HarborSettings();
        }

        /// <summary>
        /// Remote-control tunnel settings.
        /// </summary>
        public RemoteControlSettings RemoteControl
        {
            get => _RemoteControl;
            set => _RemoteControl = value ?? new RemoteControlSettings();
        }

        /// <summary>
        /// Optional: configuration for admiral-side event-driven orchestrator wakes via Claude Code
        /// Routines /fire API. Null or absent means the feature is disabled; admiral runs as today.
        /// </summary>
        public RemoteTriggerSettings? RemoteTrigger { get; set; }

        /// <summary>
        /// Optional: configuration for outbound CD webhooks. When a release is approved
        /// (transitions to Shipped), the admiral POSTs release evidence to the configured endpoint.
        /// Null or absent means the feature is disabled; admiral runs as today.
        /// </summary>
        public CdWebhookSettings? CdWebhook { get; set; }

        /// <summary>
        /// Armada-native mission failure recovery policy.
        /// </summary>
        public AutonomousRecoverySettings AutonomousRecovery
        {
            get => _AutonomousRecovery;
            set => _AutonomousRecovery = value ?? new AutonomousRecoverySettings();
        }

        /// <summary>
        /// Typed-decision system (TypeSafe Jev) policy. Off by default: no decision point consults
        /// the model until the owner enables it and the change is deployed. In the reference-swap
        /// hot-reload list so the mode flips live and an MCP settings write cannot clobber it.
        /// </summary>
        public TypedDecisionSettings TypedDecisions
        {
            get => _TypedDecisions;
            set => _TypedDecisions = value ?? new TypedDecisionSettings();
        }

        /// <summary>
        /// Operator-configured banned-diff patterns. The banned-diff guard fails a change whose added
        /// lines match any of these; the list ships EMPTY, so the guard is a no-op until a deployment
        /// adds a pattern. Domain-specific bans (a deployment's own forbidden code paths) live here as
        /// configuration, never in this product's source. Hot-reloads in place.
        /// </summary>
        public List<BannedDiffPatternRule> BannedDiffPatterns
        {
            get => _BannedDiffPatterns;
            set => _BannedDiffPatterns = value ?? new List<BannedDiffPatternRule>();
        }

        /// <summary>
        /// Captain context fetch tool (<c>armada_fetch_context</c>) policy: whether it is enabled,
        /// the per-call leaf byte budget, and the per-mission call budget. The tool is read-only and
        /// returns only sanitized memory and docs leaf text.
        /// </summary>
        public ContextRetrievalSettings ContextRetrieval
        {
            get => _ContextRetrieval;
            set => _ContextRetrieval = value ?? new ContextRetrievalSettings();
        }

        /// <summary>
        /// Near-instant runtime crash-loop detection and captain benching policy.
        /// </summary>
        public CrashLoopDetectionSettings CrashLoopDetection
        {
            get => _CrashLoopDetection;
            set => _CrashLoopDetection = value ?? new CrashLoopDetectionSettings();
        }

        /// <summary>
        /// Provider usage/quota limit quarantine and auto-restore policy.
        /// </summary>
        public CaptainQuarantineSettings CaptainQuarantine
        {
            get => _CaptainQuarantine;
            set => _CaptainQuarantine = value ?? new CaptainQuarantineSettings();
        }

        /// <summary>
        /// Resource-pressure admission policy applied before a captain is launched.
        /// </summary>
        public ResourcePressureAdmissionSettings ResourcePressureAdmission
        {
            get => _ResourcePressureAdmission;
            set => _ResourcePressureAdmission = value ?? new ResourcePressureAdmissionSettings();
        }

        /// <summary>
        /// Armada-native incident mitigation and closure policy.
        /// </summary>
        public IncidentLifecycleSettings IncidentLifecycle
        {
            get => _IncidentLifecycle;
            set => _IncidentLifecycle = value ?? new IncidentLifecycleSettings();
        }

        /// <summary>
        /// Codebase indexing settings.
        /// </summary>
        public CodeIndexSettings CodeIndex
        {
            get => _CodeIndex;
            set => _CodeIndex = value ?? new CodeIndexSettings();
        }

        /// <summary>
        /// Self-deploy settings for rebuilding and supervised restart when the
        /// self-hosted vessel lands to its own default branch.
        /// </summary>
        public SelfDeploySettings SelfDeploy
        {
            get => _SelfDeploy;
            set => _SelfDeploy = value ?? new SelfDeploySettings();
        }

        /// <summary>
        /// Model-tier reservation settings: which personas are reserved for high-tier
        /// captains versus routed to mid/low with high held back as a last resort.
        /// Product defaults are empty and policy-neutral.
        /// </summary>
        public ModelTierSettings ModelTier
        {
            get => _ModelTier;
            set => _ModelTier = value ?? new ModelTierSettings();
        }

        /// <summary>
        /// Dispatch-time validation policy, including the optional stage-persona title
        /// prefix guard. Default off with an empty prefix list.
        /// </summary>
        public VoyageDispatchSettings VoyageDispatch
        {
            get => _VoyageDispatch;
            set => _VoyageDispatch = value ?? new VoyageDispatchSettings();
        }

        /// <summary>
        /// Read-only captain-log screening policy. Default off with an empty boundary-pattern
        /// list, so a fresh install reads no captain log and the boundary rule reports nothing
        /// until an operator supplies the terms their deployment treats as private.
        /// </summary>
        public CaptainLogScreeningSettings CaptainLogScreening
        {
            get => _CaptainLogScreening;
            set => _CaptainLogScreening = value ?? new CaptainLogScreeningSettings();
        }

        /// <summary>
        /// Extra prompt templates seeded on startup. Product defaults are empty; a
        /// deployment adds specialist-reviewer templates here instead of baking them
        /// into code. Applied at process start; a change requires a restart.
        /// </summary>
        public List<AdditionalPromptTemplateSettings> AdditionalPromptTemplates
        {
            get => _AdditionalPromptTemplates;
            set => _AdditionalPromptTemplates = value ?? new List<AdditionalPromptTemplateSettings>();
        }

        /// <summary>
        /// Extra personas seeded on startup. Product defaults are empty. Applied at
        /// process start; a change requires a restart.
        /// </summary>
        public List<AdditionalPersonaSettings> AdditionalPersonas
        {
            get => _AdditionalPersonas;
            set => _AdditionalPersonas = value ?? new List<AdditionalPersonaSettings>();
        }

        /// <summary>
        /// Extra pipelines seeded on startup. Product defaults are empty. Applied at
        /// process start; a change requires a restart.
        /// </summary>
        public List<AdditionalPipelineSettings> AdditionalPipelines
        {
            get => _AdditionalPipelines;
            set => _AdditionalPipelines = value ?? new List<AdditionalPipelineSettings>();
        }

        /// <summary>
        /// External model provider registry used to route captain models to
        /// non-native endpoints. A provider is registered by model-id namespace
        /// prefix, for example <c>example-provider</c>.
        /// </summary>
        public ModelProvidersSettings ModelProviders
        {
            get => _ModelProviders;
            set => _ModelProviders = value ?? new ModelProvidersSettings();
        }

        /// <summary>
        /// Architect captain decomposition settings.
        /// </summary>
        public ArchitectSettings Architect
        {
            get => _Architect ??= new ArchitectSettings();
            set => _Architect = value ?? throw new ArgumentNullException(nameof(Architect));
        }

        /// <summary>
        /// Path of the settings file this instance is bound to. The settings-file watcher reads it and the
        /// one-time tier-record migration writes it, so a process that gives its settings their own path
        /// neither reacts to nor overwrites another process's settings file. Blank restores the default.
        /// Bound at startup and excluded from hot reload, like the other path settings.
        /// </summary>
        [JsonIgnore]
        public string SettingsFilePath
        {
            get => _SettingsFilePath;
            set => _SettingsFilePath = String.IsNullOrWhiteSpace(value) ? DefaultSettingsPath : value;
        }

        /// <summary>
        /// Autonomous objective scheduler settings.
        /// </summary>
        public AutonomousObjectiveSchedulerSettings AutonomousObjectiveScheduler
        {
            get => _AutonomousObjectiveScheduler;
            set => _AutonomousObjectiveScheduler = value ?? new AutonomousObjectiveSchedulerSettings();
        }

        /// <summary>
        /// Dock-boundary diff scanning settings. Controls secret scanning, private-identifier
        /// denylist evaluation, and public-repo classification for pre-commit and landing gates.
        /// </summary>
        public DockBoundarySettings DockBoundary
        {
            get => _DockBoundary;
            set => _DockBoundary = value ?? new DockBoundarySettings();
        }

        /// <summary>
        /// Definition-of-done gate settings that control in-dock build and unit-test verification
        /// for Worker missions before they are accepted as complete.
        /// </summary>
        public DefinitionOfDoneSettings DefinitionOfDone
        {
            get => _DefinitionOfDone;
            set => _DefinitionOfDone = value ?? new DefinitionOfDoneSettings();
        }

        /// <summary>
        /// Whether dispatch arms a voyage with its own Build and UnitTest Checks, so a voyage is
        /// never condemned at the Judge stage for carrying none.
        /// </summary>
        public VoyageCheckArmingSettings VoyageCheckArming
        {
            get => _VoyageCheckArming;
            set => _VoyageCheckArming = value ?? new VoyageCheckArmingSettings();
        }

        /// <summary>
        /// Disk-lifecycle settings that bound and make observable the reclamation of
        /// Armada-owned storage. Destructive periodic cleanup is off by default (dry-run
        /// observability first, per the direct-edit rollout constraint).
        /// </summary>
        public DiskLifecycleSettings DiskLifecycle
        {
            get => _DiskLifecycle;
            set => _DiskLifecycle = value ?? new DiskLifecycleSettings();
        }

        /// <summary>
        /// Telemetry export settings (OpenTelemetry via Prometheus/Grafana/Loki). Disabled by default,
        /// so a fresh install ships no telemetry surface until an operator opts in.
        /// </summary>
        public TelemetrySettings Telemetry
        {
            get => _Telemetry;
            set => _Telemetry = value ?? new TelemetrySettings();
        }

        #endregion

        #region Private-Members

        private string _DataDirectory = Constants.DefaultDataDirectory;

        private string _DatabasePath = Path.Combine(
            Constants.DefaultDataDirectory,
            Constants.DefaultDatabaseFilename);

        private string _LogDirectory = Path.Combine(
            Constants.DefaultDataDirectory,
            "logs");

        private string _DocksDirectory = Path.Combine(
            Constants.DefaultDataDirectory,
            "docks");

        private string _ReposDirectory = Path.Combine(
            Constants.DefaultDataDirectory,
            "repos");

        private int _AdmiralPort = Constants.DefaultAdmiralPort;
        private int _McpPort = Constants.DefaultMcpPort;
        private int _HeartbeatIntervalSeconds = Constants.DefaultHeartbeatIntervalSeconds;
        private int _LaunchProcessIdGraceSeconds = 30;
        private int _MaxInterruptedExitRedispatchAttempts = 2;
        private int _StallThresholdMinutes = Constants.DefaultStallThresholdMinutes;
        private int _StageWatchdogTimeoutMinutes = 30;
        private int _MaxRecoveryAttempts = Constants.DefaultMaxRecoveryAttempts;
        private long _MaxLogFileSizeBytes = Constants.DefaultMaxLogFileSizeBytes;
        private int _MaxLogFileCount = Constants.DefaultMaxLogFileCount;
        private int _DataRetentionDays = Constants.DefaultDataRetentionDays;
        private int _ProductionFactRetentionDays = Constants.DefaultProductionFactRetentionDays;
        private int _BranchCleanupSweepIntervalCycles = 200;
        private int _BranchCleanupPreservedRefRetentionDays = 14;
        private int _RequestHistoryRetentionDays = Constants.DefaultRequestHistoryRetentionDays;
        private int _RequestHistoryMaxBodyBytes = Constants.DefaultRequestHistoryMaxBodyBytes;
        private int _PlanningSessionRetentionDays = 0;
        private int _PlanningSessionInactivityTimeoutMinutes = 0;
        private int _PlanningSessionAbandonmentTimeoutMinutes = Constants.DefaultPlanningSessionAbandonmentTimeoutMinutes;
        private int _MaxLandingRetries = 3;
        private int _MinIdleCaptains = 0;
        private int _MaxCaptains = 0;
        private int _MaxConcurrentCaptainWorkloads = 0;
        private int _CaptainInstructionByteBudget = 32768;
        private string? _AiMemoryRoot = null;
        private int _IdleCaptainTimeoutSeconds = Constants.DefaultIdleCaptainTimeoutSeconds;
        private RemoteControlSettings _RemoteControl = new RemoteControlSettings();
        private HarborSettings _Harbor = new HarborSettings();
        private DatabaseSettings _Database = new DatabaseSettings();
        private CodeIndexSettings _CodeIndex = new CodeIndexSettings();
        private SelfDeploySettings _SelfDeploy = new SelfDeploySettings();
        private ModelTierSettings _ModelTier = new ModelTierSettings();
        private VoyageDispatchSettings _VoyageDispatch = new VoyageDispatchSettings();
        private CaptainLogScreeningSettings _CaptainLogScreening = new CaptainLogScreeningSettings();
        private List<AdditionalPromptTemplateSettings> _AdditionalPromptTemplates = new List<AdditionalPromptTemplateSettings>();
        private List<AdditionalPersonaSettings> _AdditionalPersonas = new List<AdditionalPersonaSettings>();
        private List<AdditionalPipelineSettings> _AdditionalPipelines = new List<AdditionalPipelineSettings>();
        private ModelProvidersSettings _ModelProviders = new ModelProvidersSettings();
        private ArchitectSettings? _Architect;
        private AutonomousRecoverySettings _AutonomousRecovery = new AutonomousRecoverySettings();
        private TypedDecisionSettings _TypedDecisions = new TypedDecisionSettings();
        private List<BannedDiffPatternRule> _BannedDiffPatterns = new List<BannedDiffPatternRule>();
        private ContextRetrievalSettings _ContextRetrieval = new ContextRetrievalSettings();
        private CrashLoopDetectionSettings _CrashLoopDetection = new CrashLoopDetectionSettings();
        private CaptainQuarantineSettings _CaptainQuarantine = new CaptainQuarantineSettings();
        private ResourcePressureAdmissionSettings _ResourcePressureAdmission = new ResourcePressureAdmissionSettings();
        private IncidentLifecycleSettings _IncidentLifecycle = new IncidentLifecycleSettings();
        private AutonomousObjectiveSchedulerSettings _AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings();
        private DockBoundarySettings _DockBoundary = new DockBoundarySettings();
        private DefinitionOfDoneSettings _DefinitionOfDone = new DefinitionOfDoneSettings();
        private VoyageCheckArmingSettings _VoyageCheckArming = new VoyageCheckArmingSettings();
        private DiskLifecycleSettings _DiskLifecycle = new DiskLifecycleSettings();
        private TelemetrySettings _Telemetry = new TelemetrySettings();
        private string _SettingsFilePath = DefaultSettingsPath;
        private bool _DatabasePathConfigured = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public ArmadaSettings()
        {
            if (Agents.Count == 0)
            {
                Agents.Add(new AgentSettings(
                    Enums.AgentRuntimeEnum.ClaudeCode,
                    "claude",
                    "--dangerously-skip-permissions --print")
                {
                    SupportsResume = true,
                    Environment = new Dictionary<string, string>
                    {
                        ["CLAUDE_CODE_DISABLE_NONINTERACTIVE_HINT"] = "1"
                    }
                });

                Agents.Add(new AgentSettings(
                    Enums.AgentRuntimeEnum.Codex,
                    "codex",
                    "--approval-mode full-auto"));

                Agents.Add(new AgentSettings(
                    Enums.AgentRuntimeEnum.Gemini,
                    "gemini",
                    "--sandbox none -p"));

                Agents.Add(new AgentSettings(
                    Enums.AgentRuntimeEnum.Cursor,
                    "cursor",
                    "--agent --prompt"));

                Agents.Add(new AgentSettings(
                    Enums.AgentRuntimeEnum.OpenCode,
                    "opencode",
                    "run --format json --dangerously-skip-permissions"));

                Agents.Add(new AgentSettings(
                    Enums.AgentRuntimeEnum.Mux,
                    "mux",
                    "print --yolo"));
            }
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Ensure all required directories exist.
        /// </summary>
        public void InitializeDirectories()
        {
            NormalizePaths();
            Directory.CreateDirectory(DataDirectory);
            string? dbDirectory = Path.GetDirectoryName(DatabasePath);
            if (!String.IsNullOrEmpty(dbDirectory))
                Directory.CreateDirectory(dbDirectory);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(DocksDirectory);
            Directory.CreateDirectory(ReposDirectory);
        }

        /// <summary>
        /// Apply the runtime-tunable values from another settings instance into this
        /// one, in place. Used by the settings file watcher so an edit to settings.json
        /// takes effect without a restart, and available to any caller that must update
        /// live settings without swapping the shared instance.
        ///
        /// Only values that are safe to change while the server runs are copied.
        /// Ports, filesystem paths, database settings, API keys, agent runtime
        /// definitions and remote-control settings are deliberately excluded: they are
        /// bound at startup or carry their own change protocol, so changing them here
        /// would leave the process inconsistent with its own bindings. Those still
        /// require a restart.
        ///
        /// Nested sections are merged with CopyFrom rather than reference assignment
        /// where a live service captures the nested object at construction, so the
        /// running service observes the new values.
        /// </summary>
        /// <param name="source">Settings to copy runtime-tunable values from. Null is ignored.</param>
        public void ApplyHotReloadableFrom(ArmadaSettings source)
        {
            if (source == null) return;

            // Capacity and dispatch pacing.
            MinIdleCaptains = source.MinIdleCaptains;
            MaxCaptains = source.MaxCaptains;
            MaxConcurrentCaptainWorkloads = source.MaxConcurrentCaptainWorkloads;
            HeartbeatIntervalSeconds = source.HeartbeatIntervalSeconds;
            LaunchProcessIdGraceSeconds = source.LaunchProcessIdGraceSeconds;
            MaxInterruptedExitRedispatchAttempts = source.MaxInterruptedExitRedispatchAttempts;
            StallThresholdMinutes = source.StallThresholdMinutes;
            StageWatchdogTimeoutMinutes = source.StageWatchdogTimeoutMinutes;
            IdleCaptainTimeoutSeconds = source.IdleCaptainTimeoutSeconds;
            MaxRecoveryAttempts = source.MaxRecoveryAttempts;
            MaxLandingRetries = source.MaxLandingRetries;
            CaptainInstructionByteBudget = source.CaptainInstructionByteBudget;
            AvailableLicensedContexts = new List<string>(source.AvailableLicensedContexts);

            // Landing behaviour.
            AutoPush = source.AutoPush;
            AutoCreatePullRequests = source.AutoCreatePullRequests;
            AutoMergePullRequests = source.AutoMergePullRequests;

            // Planning-session lifecycle.
            PlanningSessionRetentionDays = source.PlanningSessionRetentionDays;
            PlanningSessionInactivityTimeoutMinutes = source.PlanningSessionInactivityTimeoutMinutes;
            PlanningSessionAbandonmentTimeoutMinutes = source.PlanningSessionAbandonmentTimeoutMinutes;

            // The admission gate is constructed with a reference to this nested object,
            // so it must be mutated in place or the running gate keeps the old limits.
            ResourcePressureAdmission.CopyFrom(source.ResourcePressureAdmission);

            // Read through the shared settings instance today, but merged in place so a
            // future by-reference consumer does not silently go stale.
            ApiCaptainCloudProviders = source.ApiCaptainCloudProviders;
            ModelTier.CopyFrom(source.ModelTier);
            VoyageDispatch.CopyFrom(source.VoyageDispatch);

            // The log screen is constructed with a reference to this nested object, so it is copied
            // in place: a sub-section left out of this copy is silently ignored on every reload and
            // the feature reads as off however the settings file is edited.
            CaptainLogScreening.CopyFrom(source.CaptainLogScreening);

            // Read through the shared settings instance on every use.
            CaptainQuarantine = source.CaptainQuarantine;
            AutonomousRecovery = source.AutonomousRecovery;
            // Decision points hold this section by reference, so it is copied in place: the reloaded mode
            // reaches them, and a later settings write serializes the reloaded values rather than stale ones.
            TypedDecisions.CopyFrom(source.TypedDecisions);

            // Banned-diff patterns: replaced in place so a running check reads the current list.
            List<BannedDiffPatternRule> patterns = new List<BannedDiffPatternRule>();
            foreach (BannedDiffPatternRule rule in source.BannedDiffPatterns)
                if (rule != null) patterns.Add(rule.Clone());
            BannedDiffPatterns = patterns;
            CrashLoopDetection = source.CrashLoopDetection;
            AutonomousObjectiveScheduler = source.AutonomousObjectiveScheduler;
            IncidentLifecycle = source.IncidentLifecycle;
            DockBoundary = source.DockBoundary;
            DefinitionOfDone = source.DefinitionOfDone;
            VoyageCheckArming = source.VoyageCheckArming;
            DiskLifecycle = source.DiskLifecycle;
            Architect = source.Architect;

            // RemoteTriggerService captures this section at startup. Preserve the
            // object and mutate it so AgentWake can be armed or disarmed from the
            // watched settings file without an Admiral restart.
            RemoteTrigger ??= new RemoteTriggerSettings();
            RemoteTrigger.CopyFrom(source.RemoteTrigger);

            // MissionService captures the whole settings instance at construction and reads the
            // ContextRetrieval section at brief-generation time. Merge it in place so the
            // brief-slimming flag and its budgets can be turned on or off from the watched settings
            // file without an Admiral restart.
            ContextRetrieval ??= new ContextRetrievalSettings();
            ContextRetrieval.CopyFrom(source.ContextRetrieval);
        }

        /// <summary>
        /// Save settings to a JSON file.
        /// </summary>
        /// <param name="path">File path. Defaults to <see cref="SettingsFilePath"/>, the file this
        /// instance is bound to, so a process never writes another process's settings file.</param>
        public async Task SaveAsync(string? path = null)
        {
            NormalizePaths();
            path ??= SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(this, _SerializerOptions);
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        }

        /// <summary>
        /// Load settings from a JSON file, or return defaults if file does not exist.
        /// </summary>
        /// <param name="path">File path. Defaults to ~/.armada/settings.json.</param>
        /// <returns>Loaded or default settings.</returns>
        public static async Task<ArmadaSettings> LoadAsync(string? path = null)
        {
            path ??= DefaultSettingsPath;
            if (!File.Exists(path))
            {
                ArmadaSettings defaults = new ArmadaSettings();
                defaults.SettingsFilePath = path;
                defaults.NormalizePaths();
                return defaults;
            }
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return FromJson(json, path);
        }

        /// <summary>
        /// Parse settings file content read from <paramref name="path"/>, exactly as <see cref="LoadAsync"/> does.
        /// </summary>
        /// <param name="json">Settings file content.</param>
        /// <param name="path">File the content was read from; the instance is bound to it.</param>
        /// <returns>Parsed settings.</returns>
        /// <exception cref="JsonException">The content is not valid settings JSON.</exception>
        public static ArmadaSettings FromJson(string json, string path)
        {
            ArmadaSettings? settings = JsonSerializer.Deserialize<ArmadaSettings>(json, _SerializerOptions);
            settings ??= new ArmadaSettings();
            // Bind the instance to the file it came from, so a later save, watch, or migration acts on that
            // file rather than on the default one.
            settings.SettingsFilePath = path;
            settings.NormalizePaths();
            return settings;
        }

        /// <summary>
        /// Resolve the effective GitHub token for the supplied vessel.
        /// </summary>
        /// <param name="vessel">Optional vessel context.</param>
        /// <returns>Vessel override first, otherwise the global settings token.</returns>
        public string? ResolveGitHubToken(Vessel? vessel = null)
        {
            if (vessel != null && !String.IsNullOrWhiteSpace(vessel.GitHubTokenOverride))
            {
                return vessel.GitHubTokenOverride!.Trim();
            }

            if (!String.IsNullOrWhiteSpace(GitHubToken))
            {
                return GitHubToken!.Trim();
            }

            return null;
        }

        #endregion

        #region Private-Methods

        private void NormalizePaths()
        {
            _Database ??= new DatabaseSettings();

            if (_Database.Type == DatabaseTypeEnum.Sqlite)
            {
                string sqlitePath = ResolveEffectiveSqlitePath();
                _Database.Filename = sqlitePath;
                _DatabasePath = sqlitePath;
                _DatabasePathConfigured = true;
            }

            _CodeIndex ??= new CodeIndexSettings();
            _CodeIndex.IndexDirectory = ResolvePathRelativeToDataDirectory(_CodeIndex.IndexDirectory);

            _ModelTier ??= new ModelTierSettings();
            _ModelProviders ??= new ModelProvidersSettings();
        }

        private string ResolveEffectiveSqlitePath()
        {
            if (_Database.FilenameConfigured)
            {
                return ResolvePathRelativeToDataDirectory(_Database.Filename);
            }

            if (_DatabasePathConfigured)
            {
                return ResolvePathRelativeToDataDirectory(_DatabasePath);
            }

            return ResolvePathRelativeToDataDirectory(_Database.Filename);
        }

        private string ResolvePathRelativeToDataDirectory(string path)
        {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);

            return Path.GetFullPath(Path.Combine(DataDirectory, path));
        }

        #endregion

        #region Private-Static

        /// <summary>
        /// Default settings file path.
        /// </summary>
        public static readonly string DefaultSettingsPath = Path.Combine(
            Constants.DefaultDataDirectory,
            "settings.json");

        private static readonly JsonSerializerOptions _SerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        #endregion
    }
}
