namespace Armada.Server.Mcp
{
    using System.Text.Json;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using SyslogLogging;

    /// <summary>
    /// Builds the explicit least-privilege tool catalog for the Grok Bot lead.
    /// Tools not named here cannot appear on the restricted listener.
    /// </summary>
    public static class GrokMcpToolRegistrar
    {
        #region Private-Members

        private static readonly HashSet<string> _ReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "armada_status",
            "armada_enumerate",
            "armada_coordination_read",
            "armada_campaign_status",
            "inbox",
            "armada_list_incidents",
            "armada_get_incident",
            "armada_objective_scheduler_status",
            "armada_voyage_status",
            "armada_agentwake_status"
        };

        private static readonly HashSet<string> _ReversibleTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "armada_coordination_post",
            "armada_coordination_heartbeat",
            "armada_coordination_claim",
            "armada_send_signal",
            "armada_nudge_voyage",
            "armada_mark_signal_read"
        };

        private static readonly HashSet<string> _LifecycleTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "armada_lead_cycle_status",
            "armada_lead_cycle_begin",
            "armada_lead_cycle_heartbeat",
            "armada_lead_cycle_complete",
            "armada_lead_cycle_fail"
        };

        private const string _ControlledDispatchTool = "armada_dispatch";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the restricted Grok Bot lead catalog.
        /// </summary>
        /// <param name="register">Restricted server registration delegate.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="admiral">Admiral service.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="coordination">Coordination service.</param>
        /// <param name="incidentService">Incident service.</param>
        /// <param name="objectiveService">Objective service.</param>
        /// <param name="objectiveScheduler">Objective scheduler.</param>
        /// <param name="remoteTriggerService">Remote trigger service.</param>
        /// <param name="coordinator">Shared lead-cycle coordinator.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings settings,
            LoggingModule logging,
            CoordinationService coordination,
            IncidentService incidentService,
            ObjectiveService objectiveService,
            AutonomousObjectiveScheduler objectiveScheduler,
            IRemoteTriggerService remoteTriggerService,
            LeadCycleCoordinator coordinator)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (admiral == null) throw new ArgumentNullException(nameof(admiral));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            if (coordination == null) throw new ArgumentNullException(nameof(coordination));
            if (incidentService == null) throw new ArgumentNullException(nameof(incidentService));
            if (objectiveService == null) throw new ArgumentNullException(nameof(objectiveService));
            if (objectiveScheduler == null) throw new ArgumentNullException(nameof(objectiveScheduler));
            if (remoteTriggerService == null) throw new ArgumentNullException(nameof(remoteTriggerService));
            if (coordinator == null) throw new ArgumentNullException(nameof(coordinator));

            RegisterToolDelegate candidate = (name, description, inputSchema, handler) =>
            {
                if (String.Equals(name, _ControlledDispatchTool, StringComparison.Ordinal))
                {
                    if (!settings.GrokLead.ControlledDispatchEnabled) return;
                    register(
                        _ControlledDispatchTool,
                        "Dispatch a bounded voyage after the Grok lead has started an active cycle. The server requires an objective, limits the mission count, forces code context off, and rejects staging files, playbooks, captain overrides, and unresolved cross-dispatch dependencies.",
                        BuildControlledDispatchSchema(settings.GrokLead.MaxControlledDispatchMissions),
                        async (args) =>
                        {
                            await coordinator.RequireActiveGrokCycleAsync(
                                settings.GrokLead.ParticipantKey).ConfigureAwait(false);
                            VoyageDispatchArgs request = ParseControlledDispatch(
                                args, settings.GrokLead.MaxControlledDispatchMissions);
                            JsonElement normalized = JsonSerializer.SerializeToElement(request);
                            return await handler(normalized).ConfigureAwait(false);
                        });
                    return;
                }
                if (_ReadOnlyTools.Contains(name))
                {
                    register(name, description, inputSchema, handler);
                    return;
                }

                if (!_ReversibleTools.Contains(name) || settings.GrokLead.ReadOnly) return;
                register(
                    name,
                    description,
                    inputSchema,
                    async (args) =>
                    {
                        await coordinator.RequireActiveGrokCycleAsync(
                            settings.GrokLead.ParticipantKey).ConfigureAwait(false);
                        object result = await handler(args).ConfigureAwait(false);
                        if (String.Equals(name, "armada_coordination_heartbeat", StringComparison.Ordinal))
                        {
                            string cycleId = await coordinator.RequireActiveGrokCycleAsync(
                                settings.GrokLead.ParticipantKey).ConfigureAwait(false);
                            bool renewed = await coordinator.HeartbeatAsync(cycleId).ConfigureAwait(false);
                            if (!renewed)
                                throw new InvalidOperationException("The lead-cycle lease expired during the coordination heartbeat.");
                        }
                        return result;
                    });
            };

            McpStatusTools.Register(candidate, admiral, null);
            McpEnumerateTools.Register(candidate, database, null);
            McpCoordinationTools.Register(candidate, database, coordination, null);
            McpInboxTools.Register(candidate, database, logging);
            McpIncidentTools.Register(candidate, incidentService, objectiveService);
            McpObjectiveSchedulerTools.Register(candidate, objectiveScheduler, database, objectiveService, coordination);
            McpVoyageTools.Register(candidate, database, admiral, settings);
            McpSignalTools.Register(candidate, database, () => remoteTriggerService.GetAgentWakeStatus().EffectiveParticipantKey);
            McpAgentWakeTools.Register(candidate, remoteTriggerService);
            RegisterToolDelegate lifecycleRegister = (name, description, inputSchema, handler) =>
            {
                if (!_LifecycleTools.Contains(name)) return;
                if (settings.GrokLead.ReadOnly
                    && !String.Equals(name, "armada_lead_cycle_status", StringComparison.Ordinal)) return;
                register(name, description, inputSchema, handler);
            };
            McpLeadCycleTools.Register(
                lifecycleRegister,
                database,
                coordination,
                coordinator,
                LeadRunnerTypeEnum.Grok,
                settings.GrokLead.ParticipantKey);
        }

        /// <summary>
        /// Return the exact read-only tool names approved for the restricted listener.
        /// </summary>
        /// <returns>Read-only tool names.</returns>
        public static IReadOnlyCollection<string> ReadOnlyToolNames()
        {
            return _ReadOnlyTools.ToList();
        }

        /// <summary>
        /// Return the exact reversible tool names approved for the restricted listener.
        /// </summary>
        /// <returns>Reversible tool names.</returns>
        public static IReadOnlyCollection<string> ReversibleToolNames()
        {
            return _ReversibleTools.ToList();
        }

        /// <summary>
        /// Return the exact tool names advertised for the selected gateway mode.
        /// </summary>
        /// <param name="readOnly">True to omit all coordination and lifecycle writes.</param>
        /// <param name="controlledDispatch">True to add the bounded cycle-bound dispatch tool.</param>
        /// <returns>Advertised tool names.</returns>
        public static IReadOnlyCollection<string> AllowedToolNames(bool readOnly, bool controlledDispatch = false)
        {
            HashSet<string> tools = new HashSet<string>(_ReadOnlyTools, StringComparer.Ordinal);
            tools.Add("armada_lead_cycle_status");
            if (readOnly) return tools.ToList();

            if (controlledDispatch) tools.Add(_ControlledDispatchTool);

            tools.UnionWith(_ReversibleTools);
            tools.UnionWith(_LifecycleTools);
            return tools.ToList();
        }

        private static object BuildControlledDispatchSchema(int maxMissions)
        {
            return new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    title = new { type = "string", description = "Short voyage title." },
                    description = new { type = "string", description = "Bounded voyage description." },
                    vesselId = new { type = "string", description = "One target vessel ID." },
                    objectiveId = new { type = "string", description = "Required objective ID for durable scope and evidence lineage." },
                    pipeline = new { type = "string", @enum = new[] { "WorkerOnly", "Tested" }, description = "Optional approved pipeline. Omit to use the vessel default." },
                    missions = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = maxMissions,
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                title = new { type = "string" },
                                description = new { type = "string" },
                                preferredModel = new { type = "string", @enum = new[] { "low", "mid", "high" } },
                                capabilityHint = new { type = "string", @enum = new[] { "audit", "reasoning-heavy", "mechanical", "doc-only" } },
                                mode = new { type = "string", @enum = new[] { "Implementation", "Audit", "Research" } }
                            },
                            required = new[] { "title", "description" }
                        }
                    }
                },
                required = new[] { "title", "vesselId", "objectiveId", "missions" }
            };
        }

        internal static VoyageDispatchArgs ParseControlledDispatch(JsonElement? args, int maxMissions)
        {
            if (!args.HasValue) throw new ArgumentException("Dispatch arguments are required.", nameof(args));
            VoyageDispatchArgs? request = JsonSerializer.Deserialize<VoyageDispatchArgs>(args.Value);
            if (request == null) throw new ArgumentException("Dispatch arguments are invalid.", nameof(args));
            if (String.IsNullOrWhiteSpace(request.Title)
                || String.IsNullOrWhiteSpace(request.VesselId)
                || String.IsNullOrWhiteSpace(request.ObjectiveId))
                throw new ArgumentException("title, vesselId, and objectiveId are required.", nameof(args));
            if (request.Missions == null || request.Missions.Count == 0 || request.Missions.Count > maxMissions)
                throw new ArgumentException("The mission count is outside the controlled dispatch limit.", nameof(args));
            if (!String.IsNullOrWhiteSpace(request.PipelineId))
                throw new ArgumentException("pipelineId is not allowed by controlled dispatch.", nameof(args));
            if (!String.IsNullOrWhiteSpace(request.CodeContextMode)
                && !String.Equals(request.CodeContextMode, "off", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Controlled dispatch requires codeContextMode=off.", nameof(args));
            if (request.SelectedPlaybooks.Count > 0 || request.CaptainAssignments != null)
                throw new ArgumentException("Playbooks and captain assignments are not allowed by controlled dispatch.", nameof(args));
            if (!String.IsNullOrWhiteSpace(request.Pipeline)
                && !String.Equals(request.Pipeline, "WorkerOnly", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(request.Pipeline, "Tested", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only WorkerOnly and Tested pipelines are allowed by controlled dispatch.", nameof(args));

            foreach (MissionDescription mission in request.Missions)
            {
                if (String.IsNullOrWhiteSpace(mission.Title) || String.IsNullOrWhiteSpace(mission.Description))
                    throw new ArgumentException("Every controlled mission needs a title and description.", nameof(args));
                if (mission.PrestagedFiles != null || mission.SelectedPlaybooks != null
                    || !String.IsNullOrWhiteSpace(mission.CodeContextMode)
                    || !String.IsNullOrWhiteSpace(mission.CodeContextQuery)
                    || !String.IsNullOrWhiteSpace(mission.DependsOnMissionId)
                    || !String.IsNullOrWhiteSpace(mission.DependsOnMissionAlias)
                    || !String.IsNullOrWhiteSpace(mission.StartFromRef))
                    throw new ArgumentException("Advanced mission fields are not allowed by controlled dispatch.", nameof(args));
                if (!String.IsNullOrWhiteSpace(mission.PreferredModel)
                    && !new[] { "low", "mid", "high" }.Contains(mission.PreferredModel, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("preferredModel must be low, mid, or high.", nameof(args));
            }
            request.CodeContextMode = "off";
            request.CodeContextTokenBudget = null;
            request.CodeContextMaxResults = null;
            request.SelectedPlaybooks = new List<SelectedPlaybook>();
            request.CaptainAssignments = null;
            return request;
        }

        #endregion
    }
}
