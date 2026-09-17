namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Legacy Routing: the model-tier captain selector. It picks one captain from a gated pool by
    /// model-tier selection, within-tier preference order, capability scoring, external-provider
    /// preference, the persona fence, the retry skip list and persona preference. Smart Routing orders
    /// captains with this same selector and only filters and groups its order; it never re-ranks it.
    /// </summary>
    public static class LegacyCaptainSelector
    {
        #region Public-Methods

        /// <summary>
        /// Pick one captain from the pool, or null when none satisfies the mission's persona and model
        /// constraints. The pool is never modified.
        /// </summary>
        /// <param name="tiers">Model tier settings.</param>
        /// <param name="mission">The mission being assigned.</param>
        /// <param name="idleCaptains">The gated, idle candidate pool.</param>
        /// <param name="narrowToLowestTier">True when a requested-captain tier fallback keeps only the lowest tier present.</param>
        /// <param name="randomPick">Returns an index below its argument; used for equal peers within a tier.</param>
        /// <returns>The selected captain, or null.</returns>
        public static Captain? Select(ModelTierSettings tiers, Mission mission, List<Captain> idleCaptains, bool narrowToLowestTier, Func<int, int> randomPick)
        {
            string? persona = mission.Persona;
            string? preferredModel = mission.PreferredModel;

            List<string> specialistPersonas = tiers.SpecialistPersonas;
            IReadOnlyDictionary<string, List<string>> withinTierPreferenceOrder = tiers.WithinTierPreferenceOrder;
            bool isSpecialist = tiers.IsSpecialistPersona(persona);

            // Model filter: tier selector (random peer selection) or literal match
            if (!String.IsNullOrEmpty(preferredModel))
            {
                if (PreferredModelTierSelector.IsTierSelector(preferredModel))
                {
                    string? selectedModel = PreferredModelTierSelector.SelectModel(
                        preferredModel, idleCaptains, persona, randomPick, specialistPersonas, withinTierPreferenceOrder, tiers, mission?.CapabilityHint);
                    if (selectedModel == null) return null;
                    List<Captain> filtered = new List<Captain>();
                    foreach (Captain captain in idleCaptains)
                    {
                        if (!String.IsNullOrEmpty(captain.Model) &&
                            String.Equals(captain.Model, selectedModel, StringComparison.OrdinalIgnoreCase))
                        {
                            filtered.Add(captain);
                        }
                    }
                    if (filtered.Count == 0) return null;
                    idleCaptains = filtered;
                }
                else
                {
                    // Literal/concrete model pin: try exact match first.
                    List<Captain> filtered = new List<Captain>();
                    foreach (Captain captain in idleCaptains)
                    {
                        if (!String.IsNullOrEmpty(captain.Model) &&
                            String.Equals(captain.Model, preferredModel, StringComparison.OrdinalIgnoreCase))
                        {
                            filtered.Add(captain);
                        }
                    }
                    if (filtered.Count > 0)
                    {
                        idleCaptains = filtered;
                    }
                    else
                    {
                        // No exact match: classify the pinned model into a tier and re-resolve.
                        string? classifiedTier = PreferredModelTierSelector.ClassifyModel(preferredModel, tiers);
                        if (classifiedTier != null)
                        {
                            string? fallbackModel = PreferredModelTierSelector.SelectModel(
                                classifiedTier, idleCaptains, persona, randomPick, specialistPersonas, withinTierPreferenceOrder, tiers, mission?.CapabilityHint);
                            if (fallbackModel == null) return null;
                            List<Captain> tierFiltered = new List<Captain>();
                            foreach (Captain captain in idleCaptains)
                            {
                                if (!String.IsNullOrEmpty(captain.Model) &&
                                    String.Equals(captain.Model, fallbackModel, StringComparison.OrdinalIgnoreCase))
                                {
                                    tierFiltered.Add(captain);
                                }
                            }
                            if (tierFiltered.Count == 0) return null;
                            idleCaptains = tierFiltered;
                        }
                        // Else: unclassified concrete model -- leave idleCaptains unrestricted;
                        // persona filtering below narrows to compatible candidates.
                    }
                }
            }
            else
            {
                // No preferredModel: route through the unified selector with a sensible
                // default tier (high for specialists, mid for everyone else) so a non-specialist
                // mission is never handed an idle high-tier captain while a mid/low one is free.
                // If the selector finds no classified captain, fall through unrestricted so
                // captains carrying custom/unclassified models still receive work.
                string defaultTier = isSpecialist ? PreferredModelTierSelector.HighTier : PreferredModelTierSelector.MidTier;
                string? defaultedModel = PreferredModelTierSelector.SelectModel(
                    defaultTier, idleCaptains, persona, randomPick, specialistPersonas, withinTierPreferenceOrder, tiers, mission?.CapabilityHint);
                if (defaultedModel != null)
                {
                    List<Captain> filtered = new List<Captain>();
                    foreach (Captain captain in idleCaptains)
                    {
                        if (!String.IsNullOrEmpty(captain.Model) &&
                            String.Equals(captain.Model, defaultedModel, StringComparison.OrdinalIgnoreCase))
                        {
                            filtered.Add(captain);
                        }
                    }
                    if (filtered.Count > 0)
                        idleCaptains = filtered;
                }
            }

            // Prefer external-provider-served captains over native ones: a captain carrying
            // its own provider base URL on a non-OpenCode runtime consumes the alternate
            // (cheaper) subscription, so it wins the tie for an equal model and saves the
            // native provider's usage. OpenCode-runtime captains are treated as native.
            // Native captains remain the fallback when no external captain is idle. Applied
            // to the model-filtered set so both the no-persona shortcut and the persona
            // path honor it.
            {
                List<Captain> external = new List<Captain>();
                List<Captain> native = new List<Captain>();
                foreach (Captain captain in idleCaptains)
                {
                    if (captain.Runtime != AgentRuntimeEnum.OpenCode &&
                        !String.IsNullOrWhiteSpace(captain.ApiBaseUrl))
                    {
                        external.Add(captain);
                    }
                    else
                    {
                        native.Add(captain);
                    }
                }
                idleCaptains = external;
                idleCaptains.AddRange(native);
            }

            // If no persona requirement, return any idle captain
            if (String.IsNullOrEmpty(persona))
                return narrowToLowestTier ? RequestedCaptainAssignmentRule.NarrowToLowestTier(idleCaptains)[0] : idleCaptains[0];

            // Filter by AllowedPersonas (null = any persona is allowed)
            List<Captain> eligible = new List<Captain>();
            foreach (Captain captain in idleCaptains)
            {
                // Normalized match, so a captain whose allow-list carries the legacy spelling of a
                // persona is still eligible for that persona's missions.
                if (MissionService.CaptainAllowsPersona(captain, persona))
                {
                    eligible.Add(captain);
                }
            }

            if (eligible.Count == 0)
            {
                return null;
            }

            // In-place re-run routing: a mission whose judges produced empty output records the
            // failing captain on RetrySkipCaptainIds. Exclude those captains from re-dispatch so
            // the re-run routes to a different (native fallback) captain instead of re-selecting
            // the same degraded provider. If the exclusion would empty the pool entirely (a
            // single-captain fleet), fall back to the full eligible set so work is never stranded.
            List<Captain> eligibleFiltersSkipped = new List<Captain>();
            foreach (Captain captain in eligible)
            {
                if (!MissionService.IsCaptainOnRetrySkipList(mission?.RetrySkipCaptainIds, captain.Id))
                {
                    eligibleFiltersSkipped.Add(captain);
                }
            }
            if (eligibleFiltersSkipped.Count > 0)
            {
                eligible = eligibleFiltersSkipped;
            }

            if (narrowToLowestTier)
                eligible = RequestedCaptainAssignmentRule.NarrowToLowestTier(eligible);

            // Prefer captains whose PreferredPersona matches
            foreach (Captain captain in eligible)
            {
                if (!String.IsNullOrEmpty(captain.PreferredPersona) &&
                    String.Equals(captain.PreferredPersona, persona, StringComparison.OrdinalIgnoreCase))
                {
                    return captain;
                }
            }

            // No preferred match -- return first eligible
            return eligible[0];
        }

        /// <summary>
        /// The Legacy Routing order of the whole pool: the selector's first pick, then its pick from the
        /// captains left, and so on until it picks none. A captain the selector would never pick (a model
        /// or persona constraint) is not in the order.
        /// </summary>
        /// <param name="tiers">Model tier settings.</param>
        /// <param name="mission">The mission being assigned.</param>
        /// <param name="idleCaptains">The gated, idle candidate pool.</param>
        /// <param name="narrowToLowestTier">True when a requested-captain tier fallback keeps only the lowest tier present.</param>
        /// <param name="randomPick">Returns an index below its argument; used for equal peers within a tier.</param>
        /// <returns>The captains in selection order.</returns>
        public static List<Captain> Order(ModelTierSettings tiers, Mission mission, List<Captain> idleCaptains, bool narrowToLowestTier, Func<int, int> randomPick)
        {
            if (tiers == null) throw new ArgumentNullException(nameof(tiers));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (idleCaptains == null) throw new ArgumentNullException(nameof(idleCaptains));
            List<Captain> remaining = new List<Captain>(idleCaptains);
            List<Captain> ordered = new List<Captain>();
            while (remaining.Count > 0)
            {
                Captain? next = Select(tiers, mission, remaining, narrowToLowestTier, randomPick);
                if (next == null) break;
                ordered.Add(next);
                remaining.Remove(next);
            }
            return ordered;
        }

        #endregion
    }
}
