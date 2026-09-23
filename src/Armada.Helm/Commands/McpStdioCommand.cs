namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using System.IO;
    using System.Net.Http;
    using System.Threading;
    using Spectre.Console.Cli;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Server.Mcp;

    /// <summary>
    /// Run Armada as an MCP server over stdio (stdin/stdout).
    /// Designed to be launched by Codex, Claude Code, or other MCP clients as a subprocess.
    /// </summary>
    [Description("Run MCP server over stdio for direct MCP client integration")]
    public class McpStdioCommand : AsyncCommand<McpStdioSettings>
    {
        /// <inheritdoc />
        protected override async Task<int> ExecuteAsync(CommandContext context, McpStdioSettings settings, CancellationToken cancellationToken)
        {
            // Load settings using Armada's configured serializer/options so camelCase settings.json is honored.
            ArmadaSettings armadaSettings = await ArmadaSettings.LoadAsync().ConfigureAwait(false);
            armadaSettings.InitializeDirectories();

            // Quiet logging -- stderr only, no console (stdout is the MCP transport)
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            logging.Settings.FileLogging = FileLoggingMode.FileWithDate;
            if (!Directory.Exists(armadaSettings.LogDirectory))
                Directory.CreateDirectory(armadaSettings.LogDirectory);
            logging.Settings.LogFilename = Path.Combine(armadaSettings.LogDirectory, "mcp-stdio.log");

            // Initialize database using DatabaseDriverFactory (supports SQLite, MySQL, PostgreSQL, SQL Server)
            DatabaseDriver database = DatabaseDriverFactory.Create(armadaSettings.Database, logging);
            await database.InitializeAsync().ConfigureAwait(false);

            // Create stdio MCP server
            ArmadaMcpStdioServer mcpServer = new ArmadaMcpStdioServer();
            mcpServer.ServerName = Constants.ProductName;
            mcpServer.ServerVersion = Constants.ProductVersion;

            // Register all Armada tools
            IGitService git = new GitService(logging, database: database);
            HttpClient codeIndexHttpClient = new HttpClient();
            IEmbeddingClient embeddingClient = await EmbeddingClientFactory.CreateAsync(armadaSettings, database, logging, codeIndexHttpClient, cancellationToken).ConfigureAwait(false);
            OpenCodeServerLauncher openCodeServerLauncher = new OpenCodeServerLauncher(armadaSettings, logging, codeIndexHttpClient);
            IInferenceClient inferenceClient = CodeIndexInferenceClientFactory.Create(armadaSettings, logging, codeIndexHttpClient);
            try
            {
                await openCodeServerLauncher.StartAsync(cancellationToken).ConfigureAwait(false);
                ICodeIndexService codeIndexService = new CodeIndexService(logging, database, armadaSettings, git, embeddingClient, inferenceClient);
                RegisterTools(mcpServer.RegisterTool, database, armadaSettings, logging, git, codeIndexService);

                // Run until stdin closes or process is killed
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                // The stdio host runs in-process with this machine's settings file and database
                // credentials, so its caller is the local operator. The identity is set explicitly
                // here; no tool falls back to a default context when a caller is missing.
                Armada.Core.Models.AuthContext localOperator = Armada.Core.Models.AuthContext.Authenticated(
                    Constants.DefaultTenantId,
                    Constants.DefaultUserId,
                    true,
                    true,
                    "LocalStdio",
                    null,
                    "Local stdio operator");
                using (McpCallerContext.Begin(localOperator))
                {
                    await mcpServer.RunAsync(cts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                openCodeServerLauncher.Dispose();
            }

            return 0;
        }

        /// <summary>
        /// Build the local service graph the stdio host runs against and register every Armada MCP tool
        /// with it. The stdio host supplies the same services the admiral supplies to its HTTP MCP
        /// endpoint, so each tool that depends on an optional service behaves the same on both hosts.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Initialized database driver.</param>
        /// <param name="armadaSettings">Loaded settings.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="git">Git service shared with the code index.</param>
        /// <param name="codeIndexService">Optional code index service.</param>
        public static void RegisterTools(
            RegisterToolDelegate register,
            DatabaseDriver database,
            ArmadaSettings armadaSettings,
            LoggingModule logging,
            IGitService git,
            ICodeIndexService? codeIndexService)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (armadaSettings == null) throw new ArgumentNullException(nameof(armadaSettings));
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            if (git == null) throw new ArgumentNullException(nameof(git));

            IDockService dockService = new DockService(logging, database, armadaSettings, git);
            ICaptainService captainService = new CaptainService(logging, database, armadaSettings, git, dockService);
            IPromptTemplateService promptTemplateService = new PromptTemplateService(database, logging, armadaSettings.AdditionalPromptTemplates);
            IMessageTemplateService messageTemplateService = new MessageTemplateService(logging, promptTemplateService);
            IMissionService missionService = new MissionService(logging, database, armadaSettings, dockService, captainService, promptTemplateService, git);
            IVoyageService voyageService = new VoyageService(logging, database);
            IAdmiralService admiral = new AdmiralService(logging, database, armadaSettings, captainService, missionService, voyageService, dockService);
            AgentRuntimeFactory runtimeFactory = new AgentRuntimeFactory(logging, armadaSettings.CodeIndex.OpenCodeServer, armadaSettings.ModelProviders);
            AgentLifecycleHandler agentLifecycle = new AgentLifecycleHandler(
                logging,
                database,
                armadaSettings,
                runtimeFactory,
                admiral,
                messageTemplateService,
                promptTemplateService,
                null,
                (_, _, _, _, _, _, _, _) => Task.CompletedTask);

            IMergeFailureClassifier mergeFailureClassifier = new MergeFailureClassifier();
            LandingService landingService = new LandingService(logging, database, armadaSettings, git);
            ObjectiveService objectiveService = new ObjectiveService(database);
            IncidentService incidentService = new IncidentService(database);
            WorkflowProfileService workflowProfileService = new WorkflowProfileService(database, logging);
            VesselReadinessService readinessService = new VesselReadinessService(database, workflowProfileService, logging);
            CheckRunService checkRunService = new CheckRunService(database, workflowProfileService, readinessService, logging);
            ObjectiveDispatchPreviewService objectiveDispatchPreviewService = new ObjectiveDispatchPreviewService(
                database,
                workflowProfileService,
                readinessService,
                git,
                armadaSettings);
            IMergeQueueService mergeQueueService = new MergeQueueService(logging, database, armadaSettings, git, mergeFailureClassifier, codeIndexService: codeIndexService);

            // The mission status transition tool refuses without the shared transition service, so the
            // stdio host builds it the same way the admiral does, over its own landing handler.
            Func<string, string, string?, string?, string?, string?, string?, string?, Task> emitEvent = (_, _, _, _, _, _, _, _) => Task.CompletedTask;
            armadaSettings.RemoteTrigger ??= new RemoteTriggerSettings();
            IRemoteTriggerService remoteTriggerService = new RemoteTriggerService(
                armadaSettings.RemoteTrigger,
                new RemoteTriggerHttpClient(logging),
                new AgentWakeProcessHost(logging),
                database,
                logging);
            MissionLandingHandler missionLanding = new MissionLandingHandler(
                logging,
                database,
                armadaSettings,
                git,
                mergeQueueService,
                landingService,
                new AutoLandEvaluator(),
                new ConventionChecker(),
                new CriticalTriggerEvaluator(),
                messageTemplateService,
                promptTemplateService,
                dockService,
                remoteTriggerService,
                null,
                codeIndexService);
            MissionStatusTransitionService statusTransitions = new MissionStatusTransitionService(
                database,
                admiral,
                missionService,
                git,
                agentLifecycle.IsMissionProcessActiveAsync,
                missionLanding.HandleMissionCompleteAsync,
                emitEvent,
                logging);

            // The record-backed services the admiral supplies to its HTTP MCP endpoint act on the shared database
            // and this host's storage, so the stdio host supplies them too. Services that live only inside the
            // running admiral process (the dispatch hold, the objective scheduler, planning and refinement session
            // coordinators, the coordination board's live broadcast and wake, harbor jobs and the context index)
            // are not built here; their tools stay unregistered or report the service as unavailable.
            DeploymentEnvironmentService environmentService = new DeploymentEnvironmentService(database, workflowProfileService, logging);
            ReleaseWebhookDispatcher? releaseWebhookDispatcher = armadaSettings.CdWebhook != null && armadaSettings.CdWebhook.IsConfigured()
                ? new ReleaseWebhookDispatcher(armadaSettings.CdWebhook, logging)
                : null;
            ReleaseService releaseService = new ReleaseService(database, workflowProfileService, logging, releaseWebhookDispatcher);
            DeploymentService deploymentService = new DeploymentService(database, workflowProfileService, environmentService, checkRunService, logging);
            RunbookService runbookService = new RunbookService(database, logging);
            CaptainQuarantineService captainQuarantine = new CaptainQuarantineService(database, armadaSettings, logging, new ProviderResetQuotaProbe());
            UnlandedBranchService? unlandedBranches = git is IBranchInventory branchInventory
                ? new UnlandedBranchService(database, branchInventory, logging)
                : null;
            TerminalVoyageMissionReconciler terminalVoyageMissions = new TerminalVoyageMissionReconciler(logging, database, git);
            DiskLifecycleService diskLifecycle = new DiskLifecycleService(database, armadaSettings, logging);

            McpToolRegistrar.RegisterAll(
                register,
                database,
                admiral,
                armadaSettings,
                git,
                mergeQueueService,
                dockService,
                landingService,
                onStopCaptain: async (captainId) =>
                {
                    Armada.Core.Models.Captain? captain = await database.Captains.ReadAsync(captainId).ConfigureAwait(false);
                    if (captain != null)
                        await agentLifecycle.HandleStopAgentAsync(captain).ConfigureAwait(false);
                },
                agentLifecycle: agentLifecycle,
                templateService: promptTemplateService,
                logging: logging,
                remoteTriggerService: remoteTriggerService,
                codeIndexService: codeIndexService,
                checkRunService: checkRunService,
                objectiveService: objectiveService,
                releaseService: releaseService,
                cdWebhookDispatcher: releaseWebhookDispatcher,
                deploymentService: deploymentService,
                runbookService: runbookService,
                incidentService: incidentService,
                captainQuarantine: captainQuarantine,
                unlandedBranches: unlandedBranches,
                diskLifecycle: diskLifecycle,
                terminalVoyageMissions: terminalVoyageMissions,
                objectiveDispatchPreviewService: objectiveDispatchPreviewService,
                statusTransitions: statusTransitions,
                missionService: missionService);
        }
    }
}
