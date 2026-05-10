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
                "Trigger Reflections memory consolidation. v2-F2 extends with persona-curate and captain-curate modes. Mode-dependent required params: vesselId for consolidate/reorganize/consolidate-and-reorganize/pack-curate; personaName for persona-curate; captainId for captain-curate. At most one of vesselId/personaName/captainId may be supplied. Null target for reorganize/pack-curate/persona-curate fan-outs across active targets; captain-curate fan-out is gated by AdmiralOptions.AllowCaptainCurateFanOut.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix). Required for consolidate/combined modes; optional (null fan-outs) for reorganize/pack-curate; ignored for persona-curate/captain-curate." },
                        personaName = new { type = "string", description = "Persona name (e.g. Architect). Required for persona-curate; null fan-outs across registered personas. Mutually exclusive with vesselId/captainId." },
                        captainId = new { type = "string", description = "Captain id (cpt_ prefix). Required for captain-curate; null fan-out gated by AllowCaptainCurateFanOut. Mutually exclusive with vesselId/personaName." },
                        mode = new { type = "string", description = "consolidate (default) | reorganize | consolidate-and-reorganize | pack-curate | persona-curate | captain-curate" },
                        dualJudge = new { type = "boolean", description = "When true, dispatches the ReflectionsDualJudge pipeline; default false" },
                        sinceMissionId = new { type = "string", description = "Optional mission ID whose completion time starts the evidence window. Ignored in reorganize, pack-curate, persona-curate, and captain-curate modes." },
                        instructions = new { type = "string", description = "Optional extra guidance for the consolidator" },
                        tokenBudget = new { type = "integer", description = "Optional token budget. Defaults: 400000 (consolidate/combined/pack-curate/persona-curate/captain-curate); 30000 (reorganize)." }
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

                    // v2-F2: mutually-exclusive target params.
                    int targetCount = 0;
                    if (!String.IsNullOrEmpty(request.VesselId)) targetCount++;
                    if (!String.IsNullOrEmpty(request.PersonaName)) targetCount++;
                    if (!String.IsNullOrEmpty(request.CaptainId)) targetCount++;
                    if (targetCount > 1)
                    {
                        return (object)new { Error = "target_ambiguous" };
                    }

                    // v2-F2 identity-curate dispatch path.
                    if (mode == ReflectionMode.PersonaCurate)
                    {
                        return await DispatchPersonaCurateAsync(database, dispatcher, settings, request, dualJudge).ConfigureAwait(false);
                    }
                    if (mode == ReflectionMode.CaptainCurate)
                    {
                        return await DispatchCaptainCurateAsync(database, dispatcher, settings, request, dualJudge).ConfigureAwait(false);
                    }

                    if (String.IsNullOrEmpty(request.VesselId))
                    {
                        if (mode != ReflectionMode.Reorganize && mode != ReflectionMode.PackCurate)
                        {
                            return (object)new { Error = "vesselId_required" };
                        }

                        return await DispatchCrossVesselFanOutAsync(database, dispatcher, settings, dualJudge, mode, request).ConfigureAwait(false);
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

                    int tokenBudget = request.TokenBudget ?? mode switch
                    {
                        ReflectionMode.Reorganize => settings.DefaultReorganizeTokenBudget,
                        ReflectionMode.PackCurate => settings.DefaultPackCurateTokenBudget,
                        _ => settings.DefaultReflectionTokenBudget,
                    };

                    string? sinceMissionId = (mode == ReflectionMode.Reorganize || mode == ReflectionMode.PackCurate)
                        ? null
                        : request.SinceMissionId;

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
                    else if (mode == ReflectionMode.PackCurate)
                    {
                        if (bundle.EvidenceMissionCount == 0 && bundle.RejectedProposalCount == 0)
                        {
                            return (object)new { Error = "no_pack_evidence_available" };
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
                        judgeVerdicts = result.JudgeVerdicts,
                        appliedHintIds = result.AppliedHintIds,
                        pathWarnings = result.PathWarnings,
                        conflictWarnings = result.ConflictWarnings
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
            ReflectionMode mode,
            ConsolidateMemoryArgs request)
        {
            List<Vessel> allVessels = await database.Vessels.EnumerateAsync().ConfigureAwait(false);
            IReflectionMemoryService memoryService = new ReflectionMemoryService(database);
            List<object> dispatchedMissions = new List<object>();
            List<object> skipped = new List<object>();
            List<string> warnings = new List<string>();
            int defaultTokenBudget = mode == ReflectionMode.PackCurate
                ? settings.DefaultPackCurateTokenBudget
                : settings.DefaultReorganizeTokenBudget;

            foreach (Vessel vessel in allVessels)
            {
                if (!vessel.Active) continue;

                Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                if (inFlight != null)
                {
                    skipped.Add(new { vesselId = vessel.Id, reason = "in_flight", missionId = inFlight.Id });
                    continue;
                }

                if (mode == ReflectionMode.Reorganize)
                {
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
                }

                int tokenBudget = request.TokenBudget ?? defaultTokenBudget;
                ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                    vessel,
                    null,
                    tokenBudget,
                    mode).ConfigureAwait(false);

                if (mode == ReflectionMode.PackCurate
                    && bundle.EvidenceMissionCount == 0
                    && bundle.RejectedProposalCount == 0)
                {
                    skipped.Add(new { vesselId = vessel.Id, reason = "no_pack_evidence" });
                    continue;
                }

                string brief = bundle.Brief;
                if (!String.IsNullOrWhiteSpace(request.Instructions))
                {
                    brief += "\n\n## CALLER INSTRUCTIONS\n" + request.Instructions;
                }

                ReflectionDispatcher.DispatchResult dispatched = await dispatcher
                    .DispatchReflectionAsync(vessel, brief, mode, dualJudge, tokenBudget)
                    .ConfigureAwait(false);
                dispatchedMissions.Add(new { vesselId = vessel.Id, missionId = dispatched.MissionId, voyageId = dispatched.VoyageId });
            }

            if (dualJudge && dispatchedMissions.Count > settings.PackCurateDualJudgeFanOutWarnThreshold)
            {
                warnings.Add(
                    "dual_judge_fan_out_starvation_risk: " + dispatchedMissions.Count
                    + " vessels dispatched with dualJudge=true (threshold "
                    + settings.PackCurateDualJudgeFanOutWarnThreshold
                    + ") may starve the pinned Codex Judge captain.");
            }

            return (object)new
            {
                dispatchedMissions = dispatchedMissions,
                skipped = skipped,
                mode = ReflectionMemoryService.ModeToWireString(mode),
                dualJudge = dualJudge,
                warnings = warnings
            };
        }

        private static bool IsBootstrapTemplate(string trimmed)
        {
            return trimmed.Contains("No accepted reflection facts yet", StringComparison.Ordinal);
        }

        private static async Task<object> DispatchPersonaCurateAsync(
            DatabaseDriver database,
            ReflectionDispatcher dispatcher,
            ArmadaSettings settings,
            ConsolidateMemoryArgs request,
            bool dualJudge)
        {
            int defaultTokenBudget = settings.DefaultIdentityCurateTokenBudget;
            int tokenBudget = request.TokenBudget ?? defaultTokenBudget;

            if (String.IsNullOrEmpty(request.PersonaName))
            {
                return await FanOutPersonaCurateAsync(database, dispatcher, settings, request, dualJudge, tokenBudget).ConfigureAwait(false);
            }

            Persona? persona = await database.Personas.ReadByNameAsync(request.PersonaName).ConfigureAwait(false);
            if (persona == null) return (object)new { Error = "persona_not_found" };

            Mission? inFlight = await dispatcher.IsPersonaCurateInFlightAsync(persona.Name).ConfigureAwait(false);
            if (inFlight != null)
            {
                return (object)new
                {
                    Error = "persona_curate_in_flight",
                    missionId = inFlight.Id
                };
            }

            ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher
                .BuildPersonaCurateBriefAsync(persona, tokenBudget).ConfigureAwait(false);

            if (bundle.EvidenceMissionCount == 0 && bundle.RejectedProposalCount == 0)
            {
                return (object)new { Error = "no_persona_evidence_available" };
            }

            Vessel? anchor = await ResolveAnchorVesselAsync(database).ConfigureAwait(false);
            if (anchor == null) return (object)new { Error = "no_anchor_vessel_available" };

            string brief = bundle.Brief;
            if (!String.IsNullOrWhiteSpace(request.Instructions))
                brief += "\n\n## CALLER INSTRUCTIONS\n" + request.Instructions;

            ReflectionDispatcher.DispatchResult dispatched = await dispatcher.DispatchIdentityCurateAsync(
                ReflectionMode.PersonaCurate,
                persona.Name,
                "Curate persona-learned notes for " + persona.Name,
                brief,
                dualJudge,
                tokenBudget,
                anchor).ConfigureAwait(false);

            return (object)new
            {
                missionId = dispatched.MissionId,
                voyageId = dispatched.VoyageId,
                evidenceMissionCount = bundle.EvidenceMissionCount,
                truncated = bundle.Truncated,
                mode = "persona-curate",
                dualJudge = dualJudge,
                targetType = "persona",
                targetId = persona.Name
            };
        }

        private static async Task<object> DispatchCaptainCurateAsync(
            DatabaseDriver database,
            ReflectionDispatcher dispatcher,
            ArmadaSettings settings,
            ConsolidateMemoryArgs request,
            bool dualJudge)
        {
            int defaultTokenBudget = settings.DefaultIdentityCurateTokenBudget;
            int tokenBudget = request.TokenBudget ?? defaultTokenBudget;

            if (String.IsNullOrEmpty(request.CaptainId))
            {
                if (!settings.AllowCaptainCurateFanOut)
                {
                    return (object)new { Error = "captain_fan_out_disabled" };
                }
                return await FanOutCaptainCurateAsync(database, dispatcher, settings, request, dualJudge, tokenBudget).ConfigureAwait(false);
            }

            Captain? captain = await database.Captains.ReadAsync(request.CaptainId).ConfigureAwait(false);
            if (captain == null) return (object)new { Error = "captain_not_found" };

            Mission? inFlight = await dispatcher.IsCaptainCurateInFlightAsync(captain.Id).ConfigureAwait(false);
            if (inFlight != null)
            {
                return (object)new
                {
                    Error = "captain_curate_in_flight",
                    missionId = inFlight.Id
                };
            }

            ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher
                .BuildCaptainCurateBriefAsync(captain, tokenBudget).ConfigureAwait(false);

            if (bundle.EvidenceMissionCount == 0 && bundle.RejectedProposalCount == 0)
            {
                return (object)new { Error = "no_captain_evidence_available" };
            }

            Vessel? anchor = await ResolveAnchorVesselAsync(database).ConfigureAwait(false);
            if (anchor == null) return (object)new { Error = "no_anchor_vessel_available" };

            string brief = bundle.Brief;
            if (!String.IsNullOrWhiteSpace(request.Instructions))
                brief += "\n\n## CALLER INSTRUCTIONS\n" + request.Instructions;

            ReflectionDispatcher.DispatchResult dispatched = await dispatcher.DispatchIdentityCurateAsync(
                ReflectionMode.CaptainCurate,
                captain.Id,
                "Curate captain-learned notes for " + captain.Id,
                brief,
                dualJudge,
                tokenBudget,
                anchor).ConfigureAwait(false);

            return (object)new
            {
                missionId = dispatched.MissionId,
                voyageId = dispatched.VoyageId,
                evidenceMissionCount = bundle.EvidenceMissionCount,
                truncated = bundle.Truncated,
                mode = "captain-curate",
                dualJudge = dualJudge,
                targetType = "captain",
                targetId = captain.Id
            };
        }

        private static async Task<object> FanOutPersonaCurateAsync(
            DatabaseDriver database,
            ReflectionDispatcher dispatcher,
            ArmadaSettings settings,
            ConsolidateMemoryArgs request,
            bool dualJudge,
            int tokenBudget)
        {
            List<Persona> personas = await database.Personas.EnumerateAsync().ConfigureAwait(false);
            List<object> dispatchedMissions = new List<object>();
            List<object> skipped = new List<object>();
            List<string> warnings = new List<string>();

            Vessel? anchor = await ResolveAnchorVesselAsync(database).ConfigureAwait(false);
            if (anchor == null) return (object)new { Error = "no_anchor_vessel_available" };

            foreach (Persona persona in personas)
            {
                if (!persona.Active) continue;
                if (String.Equals(persona.Name, "MemoryConsolidator", StringComparison.OrdinalIgnoreCase)) continue;

                Mission? inFlight = await dispatcher.IsPersonaCurateInFlightAsync(persona.Name).ConfigureAwait(false);
                if (inFlight != null)
                {
                    skipped.Add(new { personaName = persona.Name, reason = "in_flight", missionId = inFlight.Id });
                    continue;
                }

                ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher
                    .BuildPersonaCurateBriefAsync(persona, tokenBudget).ConfigureAwait(false);

                if (bundle.EvidenceMissionCount == 0 && bundle.RejectedProposalCount == 0)
                {
                    skipped.Add(new { personaName = persona.Name, reason = "no_persona_evidence" });
                    continue;
                }

                string brief = bundle.Brief;
                if (!String.IsNullOrWhiteSpace(request.Instructions))
                    brief += "\n\n## CALLER INSTRUCTIONS\n" + request.Instructions;

                ReflectionDispatcher.DispatchResult dispatched = await dispatcher.DispatchIdentityCurateAsync(
                    ReflectionMode.PersonaCurate,
                    persona.Name,
                    "Curate persona-learned notes for " + persona.Name,
                    brief,
                    dualJudge,
                    tokenBudget,
                    anchor).ConfigureAwait(false);

                dispatchedMissions.Add(new
                {
                    personaName = persona.Name,
                    missionId = dispatched.MissionId,
                    voyageId = dispatched.VoyageId
                });
            }

            if (dualJudge && dispatchedMissions.Count > settings.IdentityCurateDualJudgeFanOutWarnThreshold)
            {
                warnings.Add(
                    "dual_judge_fan_out_starvation_risk: " + dispatchedMissions.Count
                    + " personas dispatched with dualJudge=true (threshold "
                    + settings.IdentityCurateDualJudgeFanOutWarnThreshold
                    + ")");
            }

            return (object)new
            {
                dispatchedMissions = dispatchedMissions,
                skipped = skipped,
                mode = "persona-curate",
                dualJudge = dualJudge,
                warnings = warnings
            };
        }

        private static async Task<object> FanOutCaptainCurateAsync(
            DatabaseDriver database,
            ReflectionDispatcher dispatcher,
            ArmadaSettings settings,
            ConsolidateMemoryArgs request,
            bool dualJudge,
            int tokenBudget)
        {
            List<Captain> captains = await database.Captains.EnumerateAsync().ConfigureAwait(false);
            List<object> dispatchedMissions = new List<object>();
            List<object> skipped = new List<object>();
            List<string> warnings = new List<string>();

            Vessel? anchor = await ResolveAnchorVesselAsync(database).ConfigureAwait(false);
            if (anchor == null) return (object)new { Error = "no_anchor_vessel_available" };

            foreach (Captain captain in captains)
            {
                Mission? inFlight = await dispatcher.IsCaptainCurateInFlightAsync(captain.Id).ConfigureAwait(false);
                if (inFlight != null)
                {
                    skipped.Add(new { captainId = captain.Id, reason = "in_flight", missionId = inFlight.Id });
                    continue;
                }

                ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher
                    .BuildCaptainCurateBriefAsync(captain, tokenBudget).ConfigureAwait(false);

                if (bundle.EvidenceMissionCount == 0 && bundle.RejectedProposalCount == 0)
                {
                    skipped.Add(new { captainId = captain.Id, reason = "no_captain_evidence" });
                    continue;
                }

                string brief = bundle.Brief;
                if (!String.IsNullOrWhiteSpace(request.Instructions))
                    brief += "\n\n## CALLER INSTRUCTIONS\n" + request.Instructions;

                ReflectionDispatcher.DispatchResult dispatched = await dispatcher.DispatchIdentityCurateAsync(
                    ReflectionMode.CaptainCurate,
                    captain.Id,
                    "Curate captain-learned notes for " + captain.Id,
                    brief,
                    dualJudge,
                    tokenBudget,
                    anchor).ConfigureAwait(false);

                dispatchedMissions.Add(new
                {
                    captainId = captain.Id,
                    missionId = dispatched.MissionId,
                    voyageId = dispatched.VoyageId
                });
            }

            if (dualJudge && dispatchedMissions.Count > settings.IdentityCurateDualJudgeFanOutWarnThreshold)
            {
                warnings.Add(
                    "dual_judge_fan_out_starvation_risk: " + dispatchedMissions.Count
                    + " captains dispatched with dualJudge=true (threshold "
                    + settings.IdentityCurateDualJudgeFanOutWarnThreshold
                    + ")");
            }

            return (object)new
            {
                dispatchedMissions = dispatchedMissions,
                skipped = skipped,
                mode = "captain-curate",
                dualJudge = dualJudge,
                warnings = warnings
            };
        }

        private static async Task<Vessel?> ResolveAnchorVesselAsync(DatabaseDriver database)
        {
            List<Vessel> vessels = await database.Vessels.EnumerateAsync().ConfigureAwait(false);
            foreach (Vessel v in vessels)
            {
                if (v.Active) return v;
            }
            return vessels.Count > 0 ? vessels[0] : null;
        }

        private sealed class AcceptMemoryProposalArgs
        {
            public string MissionId { get; set; } = "";

            public string? EditsMarkdown { get; set; }
        }

        private sealed class ConsolidateMemoryArgs
        {
            public string? VesselId { get; set; }

            public string? PersonaName { get; set; }

            public string? CaptainId { get; set; }

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
