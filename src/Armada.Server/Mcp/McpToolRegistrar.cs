// TODO: MCP is currently unauthenticated and uses the default tenant context for all operations.
// MCP authentication and per-tenant scoping is planned for a future phase.
namespace Armada.Server.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Server;
    using Armada.Core.Database;
    using Armada.Core.Memory;
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
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

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
        /// <param name="reflectionDispatcher">Optional shared reflection dispatcher.</param>
        /// <param name="reflectionBootstrap">Optional reflection memory bootstrap service used to lazy-bootstrap persona-learned playbooks on persona creation (v2-F2).</param>
        /// <param name="checkRunService">Optional structured check-run service for delivery checks.</param>
        /// <param name="objectiveService">Optional objective service for scope capture workflows.</param>
        /// <param name="planningSessionCoordinator">Optional planning coordinator for scope readiness workflows.</param>
        /// <param name="objectiveRefinementCoordinator">Optional refinement coordinator for backlog refinement workflows.</param>
        /// <param name="releaseService">Optional release service for delivery release workflows.</param>
        /// <param name="deploymentService">Optional deployment service for delivery deployment workflows.</param>
        /// <param name="runbookService">Optional runbook service for guided operational workflows.</param>
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
            ReflectionDispatcher? reflectionDispatcher = null,
            IReflectionMemoryBootstrapService? reflectionBootstrap = null,
            CheckRunService? checkRunService = null,
            ObjectiveService? objectiveService = null,
            PlanningSessionCoordinator? planningSessionCoordinator = null,
            ObjectiveRefinementCoordinator? objectiveRefinementCoordinator = null,
            ReleaseService? releaseService = null,
            DeploymentService? deploymentService = null,
            RunbookService? runbookService = null)
        {
            ArmadaSettings effectiveSettings = settings ?? new ArmadaSettings();
            ReflectionDispatcher effectiveReflectionDispatcher = reflectionDispatcher
                ?? new ReflectionDispatcher(database, admiral, effectiveSettings, new ReflectionMemoryService(database));

            McpStatusTools.Register(register, admiral, onStop);
            McpEnumerateTools.Register(register, database, mergeQueue);
            McpFleetTools.Register(register, database);
            McpVesselTools.Register(register, database, dockService);
            McpVoyageTools.Register(register, database, admiral, settings, onStopCaptain, logging, codeIndexService);
            McpMissionTools.Register(register, database, admiral, settings, git, landingService, onStopCaptain);
            McpCaptainTools.Register(register, database, admiral, settings, onStopCaptain, agentLifecycle);
            McpCaptainDiagnosticsTools.Register(register, database, codeIndexService);
            McpSignalTools.Register(register, database);
            McpEventTools.Register(register, database);
            McpDockTools.Register(register, database, dockService);
            if (logging != null) McpPlaybookTools.Register(register, database, logging);
            if (mergeQueue != null) McpMergeQueueTools.Register(register, mergeQueue);
            if (checkRunService != null) McpCheckRunTools.Register(register, database, checkRunService);
            if (objectiveService != null) McpObjectiveTools.Register(register, database, objectiveService, planningSessionCoordinator, objectiveRefinementCoordinator);
            if (releaseService != null) McpReleaseTools.Register(register, releaseService);
            if (deploymentService != null) McpDeploymentTools.Register(register, deploymentService);
            if (runbookService != null) McpRunbookTools.Register(register, runbookService);
            if (templateService != null) McpPromptTemplateTools.Register(register, database, templateService);
            McpPersonaTools.Register(register, database, reflectionBootstrap);
            McpPipelineTools.Register(register, database);
            if (settings != null) McpBackupTools.Register(register, database, settings);
            McpAgentWakeTools.Register(register, remoteTriggerService);
            McpAuditTools.Register(register, database, remoteTriggerService, effectiveReflectionDispatcher);
            McpArchitectTools.Register(register, database, new ArchitectOutputParser(), admiral, codeIndexService, logging);
            McpReflectionTools.Register(register, database, effectiveReflectionDispatcher, effectiveSettings);
            if (codeIndexService != null) McpCodeIndexTools.Register(register, codeIndexService);
        }

        /// <summary>
        /// Describe the Armada MCP tool catalog that would be registered for the supplied services.
        /// </summary>
        /// <returns>Tool summaries ordered by name.</returns>
        public static List<CaptainToolSummary> DescribeAll(
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings? settings = null,
            IGitService? git = null,
            IMergeQueueService? mergeQueue = null,
            IDockService? dockService = null,
            ILandingService? landingService = null,
            CheckRunService? checkRunService = null,
            ObjectiveService? objectiveService = null,
            PlanningSessionCoordinator? planningSessionCoordinator = null,
            ObjectiveRefinementCoordinator? objectiveRefinementCoordinator = null,
            ReleaseService? releaseService = null,
            DeploymentService? deploymentService = null,
            RunbookService? runbookService = null,
            Action? onStop = null,
            Func<string, Task>? onStopCaptain = null,
            AgentLifecycleHandler? agentLifecycle = null,
            IPromptTemplateService? templateService = null,
            LoggingModule? logging = null,
            IRemoteTriggerService? remoteTriggerService = null,
            ICodeIndexService? codeIndexService = null,
            ReflectionDispatcher? reflectionDispatcher = null,
            IReflectionMemoryBootstrapService? reflectionBootstrap = null)
        {
            List<CaptainToolSummary> tools = new List<CaptainToolSummary>();

            RegisterAll(
                (name, description, inputSchema, handler) =>
                {
                    tools.Add(new CaptainToolSummary
                    {
                        Name = name,
                        Description = description,
                        InputSchemaJson = inputSchema == null ? null : JsonSerializer.Serialize(inputSchema, _JsonOptions)
                    });
                },
                database,
                admiral,
                settings,
                git,
                mergeQueue,
                dockService,
                landingService,
                onStop,
                onStopCaptain,
                agentLifecycle,
                templateService,
                logging,
                remoteTriggerService,
                codeIndexService,
                reflectionDispatcher,
                reflectionBootstrap,
                checkRunService,
                objectiveService,
                planningSessionCoordinator,
                objectiveRefinementCoordinator,
                releaseService,
                deploymentService,
                runbookService);

            return tools
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
