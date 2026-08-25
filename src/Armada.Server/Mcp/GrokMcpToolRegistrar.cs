namespace Armada.Server.Mcp
{
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Database;
    using Armada.Core.Enums;
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
                if (_ReadOnlyTools.Contains(name))
                {
                    register(name, description, inputSchema, handler);
                    return;
                }

                if (!_ReversibleTools.Contains(name)) return;
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
            McpSignalTools.Register(candidate, database);
            McpAgentWakeTools.Register(candidate, remoteTriggerService);
            McpLeadCycleTools.Register(
                register,
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

        #endregion
    }
}
