namespace Armada.Server.Mcp.Tools
{
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers the shared unattended lead-cycle lifecycle tools.
    /// </summary>
    public static class McpLeadCycleTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Register lifecycle tools for one server-assigned lead implementation and participant.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database used to verify that no claims remain.</param>
        /// <param name="coordination">Coordination service used to verify the board handoff.</param>
        /// <param name="coordinator">Shared lead-cycle coordinator.</param>
        /// <param name="runner">Lead implementation assigned to this MCP surface.</param>
        /// <param name="participantKey">Stable participant assigned to this MCP surface.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            CoordinationService coordination,
            LeadCycleCoordinator coordinator,
            LeadRunnerTypeEnum runner,
            string participantKey)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (coordination == null) throw new ArgumentNullException(nameof(coordination));
            if (coordinator == null) throw new ArgumentNullException(nameof(coordinator));
            if (String.IsNullOrWhiteSpace(participantKey)) throw new ArgumentNullException(nameof(participantKey));

            register(
                "armada_lead_cycle_status",
                "Read the effective unattended lead mode and current shared cycle lease.",
                new { type = "object", properties = new { } },
                async (args) =>
                {
                    return (object)await coordinator.GetStatusAsync().ConfigureAwait(false);
                });

            register(
                "armada_lead_cycle_begin",
                "Start one bounded unattended lead cycle. The server assigns lead identity and refuses disabled or overlapping cycles.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        standbyFallback = new
                        {
                            type = "boolean",
                            description = "Legacy lead only: request fallback after the configured Grok inactivity threshold."
                        }
                    }
                },
                async (args) =>
                {
                    LeadCycleBeginArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<LeadCycleBeginArgs>(args.Value, _JsonOptions) ?? new LeadCycleBeginArgs()
                        : new LeadCycleBeginArgs();
                    LeadCycleStartResult result = await coordinator.TryBeginAsync(
                        runner,
                        participantKey,
                        runner == LeadRunnerTypeEnum.Legacy && request.StandbyFallback).ConfigureAwait(false);
                    return (object)result;
                });

            register(
                "armada_lead_cycle_heartbeat",
                "Renew the active lead-cycle lease. A false result means the cycle must stop immediately.",
                BuildCycleSchema(),
                async (args) =>
                {
                    LeadCycleUpdateArgs request = DeserializeUpdate(args);
                    await coordinator.RequireActiveCycleAsync(
                        request.CycleId, runner, participantKey).ConfigureAwait(false);
                    bool renewed = await coordinator.HeartbeatAsync(request.CycleId).ConfigureAwait(false);
                    return (object)new { Renewed = renewed, CycleId = request.CycleId };
                });

            register(
                "armada_lead_cycle_complete",
                "Complete the active lead cycle, record its required handoff, and release the shared lease.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        cycleId = new { type = "string", description = "Cycle ID returned by armada_lead_cycle_begin." },
                        handoff = new { type = "string", description = "Required final handoff that states results and remaining work." }
                    },
                    required = new[] { "cycleId", "handoff" }
                },
                async (args) =>
                {
                    LeadCycleUpdateArgs request = DeserializeUpdate(args);
                    await coordinator.RequireActiveCycleAsync(
                        request.CycleId, runner, participantKey).ConfigureAwait(false);
                    List<CoordinationClaim> activeClaims = await database.CoordinationClaims
                        .EnumerateActiveAsync().ConfigureAwait(false);
                    if (activeClaims.Any(claim => String.Equals(
                        claim.ParticipantKey, participantKey, StringComparison.Ordinal)))
                    {
                        return (object)new
                        {
                            Error = "Release all claims before completing the lead cycle.",
                            CycleId = request.CycleId
                        };
                    }

                    LeadCycleStatus status = await coordinator.GetStatusAsync().ConfigureAwait(false);
                    List<CoordinationMessage> messages = await coordination.ReadMessagesAsync(
                        CoordinationService.DefaultRoomKey,
                        status.StartedUtc,
                        200,
                        visibleToParticipantKey: participantKey).ConfigureAwait(false);
                    string handoff = request.Handoff ?? String.Empty;
                    bool handoffPosted = messages.Any(message =>
                        String.Equals(message.AuthorId, participantKey, StringComparison.Ordinal)
                        && String.Equals(message.Content.Trim(), handoff.Trim(), StringComparison.Ordinal));
                    if (!handoffPosted)
                    {
                        return (object)new
                        {
                            Error = "Post the same handoff to the coordination board before completing the lead cycle.",
                            CycleId = request.CycleId
                        };
                    }
                    bool completed = await coordinator.CompleteAsync(
                        request.CycleId,
                        handoff).ConfigureAwait(false);
                    return (object)new { Completed = completed, CycleId = request.CycleId };
                });

            register(
                "armada_lead_cycle_fail",
                "Record a stopped or failed lead cycle and release its shared lease.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        cycleId = new { type = "string", description = "Cycle ID returned by armada_lead_cycle_begin." },
                        reason = new { type = "string", description = "Required failure or stop reason." }
                    },
                    required = new[] { "cycleId", "reason" }
                },
                async (args) =>
                {
                    LeadCycleUpdateArgs request = DeserializeUpdate(args);
                    await coordinator.RequireActiveCycleAsync(
                        request.CycleId, runner, participantKey).ConfigureAwait(false);
                    bool failed = await coordinator.FailAsync(
                        request.CycleId,
                        request.Reason ?? String.Empty).ConfigureAwait(false);
                    return (object)new { Failed = failed, CycleId = request.CycleId };
                });
        }

        private static object BuildCycleSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    cycleId = new { type = "string", description = "Cycle ID returned by armada_lead_cycle_begin." }
                },
                required = new[] { "cycleId" }
            };
        }

        private static LeadCycleUpdateArgs DeserializeUpdate(JsonElement? args)
        {
            if (!args.HasValue) throw new ArgumentNullException(nameof(args));
            LeadCycleUpdateArgs? request = JsonSerializer.Deserialize<LeadCycleUpdateArgs>(args.Value, _JsonOptions);
            if (request == null || String.IsNullOrWhiteSpace(request.CycleId))
                throw new ArgumentException("cycleId is required.", nameof(args));
            return request;
        }
    }
}
