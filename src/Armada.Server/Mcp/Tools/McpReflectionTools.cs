namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
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
                "Trigger Reflections memory consolidation. F4-extended: optional mode (consolidate|reorganize|consolidate-and-reorganize), optional dualJudge bool, and vesselId=null cross-vessel fan-out (only with mode=reorganize).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix). Required unless mode=reorganize (then null fan-outs across active vessels)." },
                        mode = new { type = "string", description = "consolidate (default) | reorganize | consolidate-and-reorganize" },
                        dualJudge = new { type = "boolean", description = "When true, dispatches the ReflectionsDualJudge pipeline; default false" },
                        sinceMissionId = new { type = "string", description = "Optional mission ID whose completion time starts the evidence window. Ignored in pure-reorganize mode." },
                        instructions = new { type = "string", description = "Optional extra guidance for the consolidator" },
                        tokenBudget = new { type = "integer", description = "Optional token budget. Defaults to 400000 (consolidate/combined) or 30000 (reorganize)." }
                    }
                },
                async (args) =>
                {
                    ConsolidateMemoryArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<ConsolidateMemoryArgs>(args!.Value, _JsonOptions)!
                        : new ConsolidateMemoryArgs();

                    ReflectionMode mode = ReflectionMode.Consolidate;
                    if (!String.IsNullOrEmpty(request.Mode))
                    {
                        ReflectionMode? parsed = ReflectionMemoryService.ParseModeString(request.Mode);
                        if (!parsed.HasValue)
                        {
                            return (object)new { Error = "invalid_mode" };
                        }

                        mode = parsed.Value;
                    }

                    bool dualJudge = request.DualJudge ?? false;

                    if (String.IsNullOrEmpty(request.VesselId))
                    {
                        if (mode != ReflectionMode.Reorganize)
                        {
                            return (object)new { Error = "vesselId_required" };
                        }

                        return await DispatchCrossVesselFanOutAsync(database, dispatcher, settings, dualJudge, request).ConfigureAwait(false);
                    }

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

                    int tokenBudget = request.TokenBudget ?? (mode == ReflectionMode.Reorganize
                        ? settings.DefaultReorganizeTokenBudget
                        : settings.DefaultReflectionTokenBudget);

                    string? sinceMissionId = mode == ReflectionMode.Reorganize ? null : request.SinceMissionId;

                    if (mode == ReflectionMode.Reorganize)
                    {
                        IReflectionMemoryService memoryService = new ReflectionMemoryService(database);
                        string playbook = await memoryService.ReadLearnedPlaybookContentAsync(vessel).ConfigureAwait(false);
                        string trimmedPlaybook = playbook.Trim();
                        if (String.IsNullOrEmpty(trimmedPlaybook) || IsBootstrapTemplate(trimmedPlaybook))
                        {
                            return (object)new { Error = "playbook_empty" };
                        }

                        if (trimmedPlaybook.Length < settings.ReorganizePlaybookMinCharacters)
                        {
                            return (object)new { Error = "playbook_too_small" };
                        }
                    }

                    ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                        vessel,
                        sinceMissionId,
                        tokenBudget,
                        mode).ConfigureAwait(false);

                    if (mode == ReflectionMode.Consolidate || mode == ReflectionMode.ConsolidateAndReorganize)
                    {
                        if (bundle.EvidenceMissionCount == 0 && bundle.RejectedProposalCount == 0)
                        {
                            return (object)new { Error = "no_evidence_available" };
                        }
                    }

                    string brief = bundle.Brief;
                    if (!String.IsNullOrWhiteSpace(request.Instructions))
                    {
                        brief += "\n\n## CALLER INSTRUCTIONS\n" + request.Instructions;
                    }

                    ReflectionDispatcher.DispatchResult dispatched = await dispatcher
                        .DispatchReflectionAsync(vessel, brief, mode, dualJudge, tokenBudget)
                        .ConfigureAwait(false);
                    return (object)new
                    {
                        missionId = dispatched.MissionId,
                        voyageId = dispatched.VoyageId,
                        evidenceMissionCount = bundle.EvidenceMissionCount,
                        truncated = bundle.Truncated,
                        mode = ReflectionMemoryService.ModeToWireString(mode),
                        dualJudge = dualJudge
                    };
                });

            register(
                "armada_accept_memory_proposal",
                "Apply a reviewed MemoryConsolidator proposal to the vessel learned playbook. F4-extended: enforces reorganize soft-validation gate and dual-Judge gate; editsMarkdown bypasses both.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "MemoryConsolidator mission id (msn_ prefix)" },
                        editsMarkdown = new { type = "string", description = "Optional markdown override; when set, bypasses AgentOutput parsing AND F4 reorganize/dual-Judge gates" }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    AcceptMemoryProposalArgs request = JsonSerializer.Deserialize<AcceptMemoryProposalArgs>(args!.Value, _JsonOptions)!;
                    IReflectionMemoryService memoryService = new ReflectionMemoryService(database);
                    IReflectionOutputParser parser = new ReflectionOutputParser();
                    ReflectionAcceptProposalResult result = await memoryService.AcceptMemoryProposalAsync(
                        request.MissionId,
                        request.EditsMarkdown,
                        parser).ConfigureAwait(false);
                    if (!String.IsNullOrEmpty(result.Error))
                    {
                        return (object)new
                        {
                            Error = result.Error,
                            details = (object?)result.ErrorDetails
                        };
                    }

                    return (object)new
                    {
                        playbookId = result.PlaybookId,
                        playbookVersion = result.PlaybookVersion,
                        appliedContent = result.AppliedContent,
                        mode = result.Mode,
                        judgeVerdicts = result.JudgeVerdicts
                    };
                });

            register(
                "armada_reject_memory_proposal",
                "Reject a MemoryConsolidator proposal and record the rejection reason for the next reflection brief. Persists the dispatched event's mode in the rejection payload so reorganize-mode rejection feedback stays mode-scoped.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "MemoryConsolidator mission id (msn_ prefix)" },
                        reason = new { type = "string", description = "Rejection reason fed into the next reflection brief" }
                    },
                    required = new[] { "missionId", "reason" }
                },
                async (args) =>
                {
                    RejectMemoryProposalArgs request = JsonSerializer.Deserialize<RejectMemoryProposalArgs>(args!.Value, _JsonOptions)!;
                    IReflectionMemoryService memoryService = new ReflectionMemoryService(database);
                    string? error = await memoryService.RejectMemoryProposalAsync(
                        request.MissionId,
                        request.Reason).ConfigureAwait(false);
                    if (!String.IsNullOrEmpty(error))
                    {
                        return (object)new { Error = error };
                    }

                    return (object)new { status = "Rejected" };
                });
        }

        private static async Task<object> DispatchCrossVesselFanOutAsync(
            DatabaseDriver database,
            ReflectionDispatcher dispatcher,
            ArmadaSettings settings,
            bool dualJudge,
            ConsolidateMemoryArgs request)
        {
            List<Vessel> allVessels = await database.Vessels.EnumerateAsync().ConfigureAwait(false);
            IReflectionMemoryService memoryService = new ReflectionMemoryService(database);
            List<object> dispatchedMissions = new List<object>();
            List<object> skipped = new List<object>();

            foreach (Vessel vessel in allVessels)
            {
                if (!vessel.Active) continue;

                Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                if (inFlight != null)
                {
                    skipped.Add(new { vesselId = vessel.Id, reason = "in_flight", missionId = inFlight.Id });
                    continue;
                }

                string playbook = await memoryService.ReadLearnedPlaybookContentAsync(vessel).ConfigureAwait(false);
                string trimmed = playbook.Trim();
                if (String.IsNullOrEmpty(trimmed) || IsBootstrapTemplate(trimmed))
                {
                    skipped.Add(new { vesselId = vessel.Id, reason = "no_playbook" });
                    continue;
                }

                if (trimmed.Length < settings.ReorganizePlaybookMinCharacters)
                {
                    skipped.Add(new { vesselId = vessel.Id, reason = "too_small" });
                    continue;
                }

                int tokenBudget = request.TokenBudget ?? settings.DefaultReorganizeTokenBudget;
                ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                    vessel,
                    null,
                    tokenBudget,
                    ReflectionMode.Reorganize).ConfigureAwait(false);

                string brief = bundle.Brief;
                if (!String.IsNullOrWhiteSpace(request.Instructions))
                {
                    brief += "\n\n## CALLER INSTRUCTIONS\n" + request.Instructions;
                }

                ReflectionDispatcher.DispatchResult dispatched = await dispatcher
                    .DispatchReflectionAsync(vessel, brief, ReflectionMode.Reorganize, dualJudge, tokenBudget)
                    .ConfigureAwait(false);
                dispatchedMissions.Add(new { vesselId = vessel.Id, missionId = dispatched.MissionId, voyageId = dispatched.VoyageId });
            }

            return (object)new
            {
                dispatchedMissions = dispatchedMissions,
                skipped = skipped,
                mode = "reorganize",
                dualJudge = dualJudge
            };
        }

        private static bool IsBootstrapTemplate(string trimmed)
        {
            return trimmed.Contains("No accepted reflection facts yet", StringComparison.Ordinal);
        }

        private sealed class AcceptMemoryProposalArgs
        {
            public string MissionId { get; set; } = "";

            public string? EditsMarkdown { get; set; }
        }

        private sealed class ConsolidateMemoryArgs
        {
            public string? VesselId { get; set; }

            public string? Mode { get; set; }

            public bool? DualJudge { get; set; }

            public string? SinceMissionId { get; set; }

            public string? Instructions { get; set; }

            public int? TokenBudget { get; set; }
        }

        private sealed class RejectMemoryProposalArgs
        {
            public string MissionId { get; set; } = "";

            public string Reason { get; set; } = "";
        }
    }
}
