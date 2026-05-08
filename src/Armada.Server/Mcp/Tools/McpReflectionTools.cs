namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Registers MCP tools for reflection memory consolidation.
    /// </summary>
    public static class McpReflectionTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers reflection MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="dispatcher">Shared reflection dispatcher.</param>
        /// <param name="settings">Application settings.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            ReflectionDispatcher dispatcher,
            ArmadaSettings settings)
        {
            register(
                "armada_consolidate_memory",
                "Manually trigger Reflections v1 memory consolidation for a vessel.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        sinceMissionId = new { type = "string", description = "Optional mission ID whose completion time starts the evidence window" },
                        instructions = new { type = "string", description = "Optional extra guidance for the consolidator" },
                        tokenBudget = new { type = "integer", description = "Optional token budget. Defaults to ArmadaSettings.DefaultReflectionTokenBudget." }
                    },
                    required = new[] { "vesselId" }
                },
                async (args) =>
                {
                    ConsolidateMemoryArgs request = JsonSerializer.Deserialize<ConsolidateMemoryArgs>(args!.Value, _JsonOptions)!;
                    Vessel? vessel = await database.Vessels.ReadAsync(request.VesselId).ConfigureAwait(false);
                    if (vessel == null) return (object)new { Error = "vessel_not_found" };

                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    if (inFlight != null)
                    {
                        return (object)new
                        {
                            Error = "reflection_already_in_flight",
                            missionId = inFlight.Id
                        };
                    }

                    int tokenBudget = request.TokenBudget ?? settings.DefaultReflectionTokenBudget;
                    ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                        vessel,
                        request.SinceMissionId,
                        tokenBudget).ConfigureAwait(false);

                    if (bundle.EvidenceMissionCount == 0 && bundle.RejectedProposalCount == 0)
                    {
                        return (object)new { Error = "no_evidence_available" };
                    }

                    string brief = bundle.Brief;
                    if (!String.IsNullOrWhiteSpace(request.Instructions))
                    {
                        brief += "\n\n## CALLER INSTRUCTIONS\n" + request.Instructions;
                    }

                    ReflectionDispatcher.DispatchResult dispatched = await dispatcher.DispatchReflectionAsync(vessel, brief).ConfigureAwait(false);
                    return (object)new
                    {
                        missionId = dispatched.MissionId,
                        voyageId = dispatched.VoyageId,
                        evidenceMissionCount = bundle.EvidenceMissionCount,
                        truncated = bundle.Truncated
                    };
                });
        }

        private sealed class ConsolidateMemoryArgs
        {
            public string VesselId { get; set; } = "";

            public string? SinceMissionId { get; set; }

            public string? Instructions { get; set; }

            public int? TokenBudget { get; set; }
        }
    }
}
