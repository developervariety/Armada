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
                    string handoff = (request.Handoff ?? String.Empty).Trim();
                    if (handoff.Length == 0)
                    {
                        return (object)new
                        {
                            Error = "A handoff is required to complete the lead cycle.",
                            CycleId = request.CycleId
                        };
                    }
                    bool handoffPosted = messages.Any(message =>
                        String.Equals(message.AuthorId, participantKey, StringComparison.Ordinal)
                        && HandoffMatches(message.Content, handoff));
                    if (!handoffPosted)
                    {
                        // The gate posts the handoff itself. Refusing here made the lead re-post
                        // and retry until one copy matched, which is where duplicate handoffs
                        // came from; the cycle's own final note is the one that must exist.
                        await coordination.PostMessageAsync(
                            CoordinationService.DefaultRoomKey,
                            CoordinationAuthorTypeEnum.Operator,
                            participantKey,
                            participantKey,
                            handoff).ConfigureAwait(false);
                    }
                    bool completed = await coordinator.CompleteAsync(
                        request.CycleId,
                        handoff).ConfigureAwait(false);
                    return (object)new
                    {
                        Completed = completed,
                        CycleId = request.CycleId,
                        HandoffPosted = handoffPosted ? "already_on_board" : "posted_by_gate"
                    };
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
        /// <summary>
        /// True when a board note carries the handoff: the same text once whitespace runs
        /// are collapsed, or the handoff with a "[ARMADA:LEAD-HANDOFF] Cycle ...:" prefix.
        /// </summary>
        /// <param name="content">Board note content.</param>
        /// <param name="handoff">Handoff text.</param>
        /// <returns>True when they match.</returns>
        internal static bool HandoffMatches(string? content, string? handoff)
        {
            string left = CollapseWhitespace(content);
            string right = CollapseWhitespace(handoff);
            if (left.Length == 0 || right.Length == 0) return false;
            if (String.Equals(left, right, StringComparison.Ordinal)) return true;
            return left.EndsWith(right, StringComparison.Ordinal)
                && left.StartsWith("[ARMADA:LEAD-HANDOFF]", StringComparison.Ordinal);
        }

        private static string CollapseWhitespace(string? text)
        {
            if (String.IsNullOrWhiteSpace(text)) return String.Empty;
            return System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
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
