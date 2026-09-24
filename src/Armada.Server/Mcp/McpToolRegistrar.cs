// TODO: MCP is currently unauthenticated and uses the default tenant context for all operations.
// MCP authentication and per-tenant scoping is planned for a future phase.
namespace Armada.Server.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Server;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using SyslogLogging;

    /// <summary>
    /// Delegate matching the RegisterTool signature shared by MCP transports.
    /// </summary>
    public delegate void RegisterToolDelegate(
        string name,
        string description,
        object inputSchema,
        Func<JsonElement?, Task<object>> handler);

    /// <summary>
    /// Registers all Armada MCP tools on any MCP server transport.
    /// </summary>
    public static class McpToolRegistrar
    {
        /// <summary>
        /// Register all Armada tools using the provided registration delegate.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database driver for direct queries.</param>
        /// <param name="admiral">Admiral service for orchestration operations.</param>
        /// <param name="settings">Application settings for log/diff paths.</param>
        /// <param name="git">Git service for diff operations.</param>
        /// <param name="mergeQueue">Merge queue service.</param>
        /// <param name="dockService">Dock service for dock management.</param>
        /// <param name="landingService">Landing service for retry landing operations.</param>
        /// <param name="onStop">Callback to stop the server.</param>
        /// <param name="onStopCaptain">Callback to kill a captain's agent process by captain ID. Called before RecallCaptainAsync.</param>
        /// <param name="agentLifecycle">Agent lifecycle handler used for captain model validation.</param>
        /// <param name="templateService">Prompt template service for template operations.</param>
        /// <param name="logging">Logging module for tools that need validation services.</param>
        /// <param name="remoteTriggerService">Remote trigger service for event-driven wake-up integration.</param>
        /// <param name="codeIndexService">Code index service for search and context-pack tools.</param>
        /// <param name="checkRunService">Optional structured check-run service for delivery checks.</param>
        /// <param name="objectiveService">Optional objective service for scope capture workflows.</param>
        /// <param name="planningSessionCoordinator">Optional planning coordinator for scope readiness workflows.</param>
        /// <param name="objectiveRefinementCoordinator">Optional refinement coordinator for backlog refinement workflows.</param>
        /// <param name="releaseService">Optional release service for delivery release workflows.</param>
        /// <param name="deploymentService">Optional deployment service for delivery deployment workflows.</param>
        /// <param name="runbookService">Optional runbook service for guided operational workflows.</param>
        /// <param name="incidentService">Optional incident service for operational incident workflows.</param>
        /// <param name="objectiveScheduler">Optional autonomous objective scheduler for scheduler control tools.</param>
        /// <param name="captainQuarantine">Optional captain quarantine service enabling the bench and unbench tools.</param>
        /// <param name="unlandedBranches">Optional unlanded-branch reporting service enabling armada_unlanded_branches.</param>
        /// <param name="objectiveDispatchPreviewService">Optional read-only objective dispatch preview service.</param>
        /// <param name="missionService">Optional mission service enabling armada_review_hold.</param>
        /// <param name="captainAdministration">Shared captain stop-all and deletion service. When null, one is built from <paramref name="admiral"/> and the supplied session coordinators.</param>
        /// <param name="missionOperations">Shared mission and voyage operations (cancel, purge, restart). When null, one is built from <paramref name="admiral"/>, <paramref name="settings"/> and <paramref name="dockService"/> that writes no events.</param>
        public static void RegisterAll(
            RegisterToolDelegate register,
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings? settings = null,
            IGitService? git = null,
            IMergeQueueService? mergeQueue = null,
            IDockService? dockService = null,
            ILandingService? landingService = null,
            Action? onStop = null,
            Func<string, Task>? onStopCaptain = null,
            AgentLifecycleHandler? agentLifecycle = null,
            IPromptTemplateService? templateService = null,
            LoggingModule? logging = null,
            IRemoteTriggerService? remoteTriggerService = null,
            ICodeIndexService? codeIndexService = null,
            CheckRunService? checkRunService = null,
            ObjectiveService? objectiveService = null,
            PlanningSessionCoordinator? planningSessionCoordinator = null,
            ObjectiveRefinementCoordinator? objectiveRefinementCoordinator = null,
            ReleaseService? releaseService = null,
            IReleaseWebhookDispatcher? cdWebhookDispatcher = null,
            DeploymentService? deploymentService = null,
            RunbookService? runbookService = null,
            IncidentService? incidentService = null,
            AutonomousObjectiveScheduler? objectiveScheduler = null,
            ICaptainQuarantineService? captainQuarantine = null,
            UnlandedBranchService? unlandedBranches = null,
            Armada.Core.Services.DiskLifecycleService? diskLifecycle = null,
            LongRunningJobService? longRunningJobs = null,
            Armada.Server.CoordinationService? coordinationService = null,
            Armada.Core.Services.DispatchHold? dispatchHold = null,
            Armada.Core.Services.TerminalVoyageMissionReconciler? terminalVoyageMissions = null,
            ObjectiveDispatchPreviewService? objectiveDispatchPreviewService = null,
            MissionStatusTransitionService? statusTransitions = null,
            Armada.Core.Services.HarborJobService? harborJobs = null,
            Armada.Core.Services.Interfaces.ITypedDecisionClient? typedDecisionClient = null,
            Armada.Core.Services.TypedDecisionRecorder? typedDecisionRecorder = null,
            Func<string?>? typedDecisionParticipantKeyProvider = null,
            Armada.Core.Services.TypedDecisionEvalService? typedDecisionEval = null,
            Armada.Core.Services.TypedDecisions.TypedDecisionSampleStore? typedDecisionSamples = null,
            Armada.Core.Services.PapercutMergeAdapter? papercutMergeAdapter = null,
            Armada.Core.Services.InboxTriageAdapter? inboxTriageAdapter = null,
            Armada.Core.Services.FollowUpRoutingAdapter? followUpRoutingAdapter = null,
            Armada.Core.Services.TypedChangeQualityAdapter? changeQualityAdapter = null,
            Armada.Core.Services.TypedDispatchStalenessAdapter? dispatchStalenessAdapter = null,
            Armada.Core.Services.Interfaces.IFollowUpRouter? changeQualityFollowUpRouter = null,
            Armada.Core.Context.ContextRetrievalService? contextRetrieval = null,
            Func<string?>? contextParticipantKeyProvider = null,
            Armada.Core.Services.Interfaces.IMissionService? missionService = null,
            CaptainAdministrationService? captainAdministration = null,
            MissionOperations? missionOperations = null)
        {
            ArmadaSettings effectiveSettings = settings ?? new ArmadaSettings();
            missionOperations = missionOperations ?? new MissionOperations(
                database,
                effectiveSettings,
                dockService,
                (captainId, token) => admiral.RecallCaptainAsync(captainId, token),
                OperationNotifier.None,
                logging);
            longRunningJobs = longRunningJobs ?? new LongRunningJobService();

            McpStatusTools.Register(register, admiral, onStop);
            McpLongRunningJobTools.Register(register, longRunningJobs);
            McpEnumerateTools.Register(register, database, mergeQueue);
            McpFleetTools.Register(register, database);
            McpVesselTools.Register(register, database, dockService, missionOperations);
            McpVoyageTools.Register(register, database, admiral, settings, onStopCaptain, logging, codeIndexService, objectiveService, longRunningJobs, objectiveDispatchPreviewService, dispatchStalenessAdapter, missionOperations);
            McpMissionTools.Register(register, database, admiral, settings, git, landingService, statusTransitions, missionOperations);
            if (captainAdministration == null)
            {
                captainAdministration = new CaptainAdministrationService(database, (captainId, token) => admiral.RecallCaptainAsync(captainId, token), logging);
                if (agentLifecycle != null)
                {
                    captainAdministration.StopProcess = agentLifecycle.HandleStopAgentAsync;
                    captainAdministration.ValidateModel = agentLifecycle.ValidateCaptainModelAsync;
                }
                captainAdministration.AttachSessionCoordinators(planningSessionCoordinator, objectiveRefinementCoordinator);
            }
            McpCaptainTools.Register(register, database, admiral, settings, onStopCaptain, agentLifecycle, logging, captainQuarantine, captainAdministration);
            McpCaptainDiagnosticsTools.Register(register, database, codeIndexService);
            if (unlandedBranches != null) McpUnlandedBranchTools.Register(register, unlandedBranches);
            if (coordinationService != null) McpCoordinationTools.Register(register, database, coordinationService, dispatchHold, inboxTriageAdapter, longRunningJobs);
            McpSignalTools.Register(register, database, () => remoteTriggerService?.GetAgentWakeStatus().EffectiveParticipantKey);
            McpEventTools.Register(register, database);
            McpTokenUsageTools.Register(register, database);
            McpProductionTools.Register(register, database);
            McpPapercutTools.Register(register, database, papercutMergeAdapter);
            McpMemoryProposalTools.Register(register, database);
            if (logging != null) McpInboxTools.Register(register, database, logging, inboxTriageAdapter);
            McpDockTools.Register(register, database, dockService);
            if (logging != null) McpPlaybookTools.Register(register, database, logging);
            if (mergeQueue != null) McpMergeQueueTools.Register(register, mergeQueue, longRunningJobs, database, missionOperations.Notifier);
            if (checkRunService != null) McpCheckRunTools.Register(register, database, checkRunService);
            if (objectiveService != null) McpObjectiveTools.Register(register, database, objectiveService, planningSessionCoordinator, objectiveRefinementCoordinator, objectiveDispatchPreviewService);
            if (releaseService != null) McpReleaseTools.Register(register, releaseService);
            if (cdWebhookDispatcher != null) McpCdWebhookTools.Register(register, cdWebhookDispatcher);
            if (deploymentService != null) McpDeploymentTools.Register(register, deploymentService);
            if (runbookService != null) McpRunbookTools.Register(register, runbookService);
            if (logging != null)
            {
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(database, logging);
                DeploymentEnvironmentService environments = new DeploymentEnvironmentService(database, workflowProfiles, logging);
                McpWorkflowProfileTools.Register(register, database, workflowProfiles);
                McpEnvironmentTools.Register(register, environments);
                if (runbookService != null)
                    McpOperationalAssetTools.Register(register, database, workflowProfiles, environments, runbookService);
            }
            if (incidentService != null) McpIncidentTools.Register(register, incidentService, objectiveService);
            if (objectiveScheduler != null && objectiveService != null) McpObjectiveSchedulerTools.Register(register, objectiveScheduler, database, objectiveService, coordinationService);
            if (templateService != null) McpPromptTemplateTools.Register(register, database, templateService);
            McpPersonaTools.Register(register, database);
            McpPipelineTools.Register(register, database);
            McpMemoryTools.Register(register, database, logging);

            // The captain-facing context fetch tool sits in the mission-scoped catalogue beside
            // the memory tools. It is registered only when a built context index was supplied.
            if (contextRetrieval != null)
                McpContextTools.Register(register, contextRetrieval, database, effectiveSettings, logging, contextParticipantKeyProvider);

            // The captain-facing typed-decision tools sit in the mission-scoped catalogue beside the
            // memory tools. They need the recorder, which needs a logging module; where none was
            // supplied (a minimal test harness) the tools are simply not registered.
            Armada.Core.Services.TypedDecisionRecorder? typedRecorder = typedDecisionRecorder
                ?? (logging != null ? new Armada.Core.Services.TypedDecisionRecorder(database, logging) : null);
            if (typedRecorder != null)
            {
                // The D26 prior-art premise tool runs the deterministic retriever for the captain's plan.
                // The retriever needs git tracked-content search and branch inventory (a GitService is
                // both); where no git service is available the tool still registers but finds no
                // candidates, and it is dormant until the prior_art decision leaves Off regardless.
                Armada.Core.Services.Interfaces.IPriorArtRetriever? priorArtRetriever =
                    git is Armada.Core.Services.Interfaces.IBranchInventory branchInventory
                        ? new Armada.Core.Services.PriorArtRetriever(
                            new Armada.Core.Services.GitPriorArtSource(git, branchInventory, database, logging), logging)
                        : null;

                McpTypedDecisionTools.Register(
                    register,
                    database,
                    typedDecisionClient,
                    typedRecorder,
                    effectiveSettings,
                    logging,
                    typedDecisionParticipantKeyProvider,
                    priorArtRetriever);

                // The operator-facing data tools over the same programme: record that a gated outcome
                // was wrong, and report what the host has retained. Operator-scoped, so they are not in
                // the caller-scoped catalogue a mission captain reaches.
                McpTypedDecisionDataTools.Register(
                    register,
                    typedRecorder,
                    typedDecisionSamples,
                    () => effectiveSettings.TypedDecisions,
                    logging);
            }
            if (typedDecisionEval != null) McpTypedDecisionEvalTools.Register(register, typedDecisionEval);
            if (settings != null) McpBackupTools.Register(register, new DatabaseBackupService(database, settings));
            McpAgentWakeTools.Register(register, remoteTriggerService);
            McpAuditTools.Register(register, database, remoteTriggerService, followUpRoutingAdapter);
            McpChangeQualityTools.Register(register, changeQualityAdapter, changeQualityFollowUpRouter, logging);
            McpArchitectTools.Register(register, database, new ArchitectOutputParser(), admiral, codeIndexService, logging, settings, dispatchStalenessAdapter);
            if (codeIndexService != null)
            {
                McpCodeIndexTools.Register(register, codeIndexService, longRunningJobs);

                // The captain-facing code search sits in the mission-scoped catalogue. It resolves the
                // vessel from the calling mission, so a captain reaches its own vessel's index only.
                McpMissionCodeSearchTools.Register(register, codeIndexService, database, effectiveSettings, logging);
            }
            if (diskLifecycle != null) McpDiskLifecycleTools.Register(register, diskLifecycle, longRunningJobs);
            if (terminalVoyageMissions != null) McpTerminalVoyageMissionTools.Register(register, terminalVoyageMissions, longRunningJobs);
            if (harborJobs != null) McpHarborJobTools.Register(register, harborJobs);
            if (missionService != null) McpReviewHoldTools.Register(register, missionService);
        }
    }
}
