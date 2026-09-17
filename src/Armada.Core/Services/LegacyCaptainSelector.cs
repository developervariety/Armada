namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Legacy Routing: the tier captain selector. Selection runs in two layers. Eligibility admits a captain
    /// when its persona lock allows the mission's persona and its Capability tier is at or above the mission's
    /// tier floor (the preferredModel selector, or Premium for a specialist persona). Order then ranks the
    /// eligible captains: the lowest admitted tier first, then higher preference rank, then (optionally) an
    /// external-provider captain before a native one, then a captain whose preferred persona matches; among
    /// equal captains a model is chosen at random. Smart Routing orders captains with this same selector and
    /// only filters and groups its order; it never re-ranks it or admits a captain this selector excludes.
    /// </summary>
    public static class LegacyCaptainSelector
    {
        #region Public-Members

        /// <summary>Layer name for a captain the persona lock or tier floor excludes.</summary>
        public const string LayerEligibility = "eligibility";

        /// <summary>The captain's persona lock does not allow the mission's persona.</summary>
        public const string ReasonPersonaNotAllowed = "persona_not_allowed";

        /// <summary>The captain's tier is below the mission's tier floor.</summary>
        public const string ReasonBelowTierFloor = "below_tier_floor";

        /// <summary>The mission pins a concrete model that another idle captain runs.</summary>
        public const string ReasonModelPinMismatch = "model_pin_mismatch";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Pick one captain from the pool, or null when none satisfies the mission's persona and tier
        /// constraints. The pool is never modified.
        /// </summary>
        /// <param name="tiers">Model tier settings.</param>
        /// <param name="mission">The mission being assigned.</param>
        /// <param name="idleCaptains">The gated, idle candidate pool.</param>
        /// <param name="narrowToLowestTier">True when a requested-captain tier fallback keeps only the lowest tier present.</param>
        /// <param name="randomPick">Returns an index below its argument; used for equal peers.</param>
        /// <returns>The selected captain, or null.</returns>
        public static Captain? Select(ModelTierSettings tiers, Mission mission, List<Captain> idleCaptains, bool narrowToLowestTier, Func<int, int> randomPick)
        {
            if (tiers == null) throw new ArgumentNullException(nameof(tiers));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (idleCaptains == null) throw new ArgumentNullException(nameof(idleCaptains));
            if (randomPick == null) throw new ArgumentNullException(nameof(randomPick));

            List<Captain> eligible = new List<Captain>();
            foreach (Captain captain in idleCaptains)
            {
                if (captain != null && MissionService.CaptainAllowsPersona(captain, mission.Persona))
                    eligible.Add(captain);
            }

            string? preferredModel = mission.PreferredModel;
            if (IsConcretePin(preferredModel) && idleCaptains.Any(c => c != null && RunsModel(c, preferredModel!)))
            {
                // An idle captain runs the pinned model: the pin is an explicit choice, honoured ahead of tiers.
                List<Captain> pinned = eligible.Where(c => RunsModel(c, preferredModel!)).ToList();
                if (pinned.Count == 0) return null;
                if (narrowToLowestTier) pinned = RequestedCaptainAssignmentRule.NarrowToLowestTier(pinned);
                return PickAvoidingRetrySkips(tiers, mission, pinned, randomPick);
            }

            if (eligible.Count == 0) return null;
            if (narrowToLowestTier) eligible = RequestedCaptainAssignmentRule.NarrowToLowestTier(eligible);

            List<CaptainTierEnum> order = TierOrderFor(tiers, mission);

            // A retry avoids the captains on its skip list while any other admitted captain remains, so it
            // routes away from a degraded provider; it reuses one only when nothing else can take the work.
            foreach (bool allowSkipped in new[] { false, true })
            {
                foreach (CaptainTierEnum tier in order)
                {
                    List<Captain> inTier = eligible
                        .Where(c => CaptainTierSelector.EffectiveTier(c) == tier)
                        .Where(c => allowSkipped || !MissionService.IsCaptainOnRetrySkipList(mission.RetrySkipCaptainIds, c.Id))
                        .ToList();
                    if (inTier.Count > 0) return Pick(tiers, mission, inTier, randomPick);
                }
            }

            return null;
        }

        /// <summary>
        /// The Legacy Routing order of the whole pool: the selector's first pick, then its pick from the
        /// captains left, and so on until it picks none. A captain the selector would never pick (a persona or
        /// tier constraint) is not in the order.
        /// </summary>
        /// <param name="tiers">Model tier settings.</param>
        /// <param name="mission">The mission being assigned.</param>
        /// <param name="idleCaptains">The gated, idle candidate pool.</param>
        /// <param name="narrowToLowestTier">True when a requested-captain tier fallback keeps only the lowest tier present.</param>
        /// <param name="randomPick">Returns an index below its argument; used for equal peers.</param>
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

        /// <summary>
        /// Why the eligibility layer excludes each captain it excludes: its persona lock, its tier below the
        /// mission's floor, or a concrete model pin another idle captain satisfies. Captains the layer admits
        /// are absent from the result.
        /// </summary>
        /// <param name="tiers">Model tier settings.</param>
        /// <param name="mission">The mission being assigned.</param>
        /// <param name="idleCaptains">The gated, idle candidate pool.</param>
        /// <returns>Exclusion reason keyed by captain identifier.</returns>
        public static Dictionary<string, string> ExplainExclusions(ModelTierSettings tiers, Mission mission, List<Captain> idleCaptains)
        {
            if (tiers == null) throw new ArgumentNullException(nameof(tiers));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (idleCaptains == null) throw new ArgumentNullException(nameof(idleCaptains));
            Dictionary<string, string> reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            string? preferredModel = mission.PreferredModel;
            bool pinSatisfied = IsConcretePin(preferredModel) && idleCaptains.Any(c => c != null && RunsModel(c, preferredModel!));
            List<CaptainTierEnum> order = TierOrderFor(tiers, mission);
            foreach (Captain captain in idleCaptains)
            {
                if (captain == null) continue;
                if (!MissionService.CaptainAllowsPersona(captain, mission.Persona))
                    reasons[captain.Id] = ReasonPersonaNotAllowed;
                else if (pinSatisfied && !RunsModel(captain, preferredModel!))
                    reasons[captain.Id] = ReasonModelPinMismatch;
                else if (!pinSatisfied && !order.Contains(CaptainTierSelector.EffectiveTier(captain)))
                    reasons[captain.Id] = ReasonBelowTierFloor;
            }
            return reasons;
        }

        /// <summary>
        /// The tiers the mission may land on, in the order they are tried: Premium only for a specialist
        /// persona; otherwise the floor its preferredModel names (a tier selector, or the roster tier of a
        /// pinned model no idle captain runs) and every tier above it.
        /// </summary>
        /// <param name="tiers">Model tier settings.</param>
        /// <param name="mission">The mission being assigned.</param>
        /// <returns>The ordered tiers.</returns>
        public static List<CaptainTierEnum> TierOrderFor(ModelTierSettings tiers, Mission mission)
        {
            if (tiers == null) throw new ArgumentNullException(nameof(tiers));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            bool isSpecialist = tiers.IsSpecialistPersona(mission.Persona);
            CaptainTierEnum? floor = null;
            string? preferredModel = mission.PreferredModel;
            if (!String.IsNullOrWhiteSpace(preferredModel))
            {
                floor = PreferredModelTierSelector.IsTierSelector(preferredModel)
                    ? PreferredModelTierSelector.FloorOf(preferredModel)
                    : PreferredModelTierSelector.TierOfModel(preferredModel, tiers);
            }
            return PreferredModelTierSelector.TierOrder(floor, isSpecialist);
        }

        #endregion

        #region Private-Methods

        private static bool IsConcretePin(string? preferredModel)
        {
            return !String.IsNullOrWhiteSpace(preferredModel) && !PreferredModelTierSelector.IsTierSelector(preferredModel);
        }

        private static bool RunsModel(Captain captain, string model)
        {
            return !String.IsNullOrEmpty(captain.Model) && String.Equals(captain.Model, model, StringComparison.OrdinalIgnoreCase);
        }

        private static Captain PickAvoidingRetrySkips(ModelTierSettings tiers, Mission mission, List<Captain> candidates, Func<int, int> randomPick)
        {
            List<Captain> notSkipped = candidates.Where(c => !MissionService.IsCaptainOnRetrySkipList(mission.RetrySkipCaptainIds, c.Id)).ToList();
            return Pick(tiers, mission, notSkipped.Count > 0 ? notSkipped : candidates, randomPick);
        }

        // Rank candidates of one tier: capability-hint fit (when the hint maps to a profile dimension), then
        // preference rank, then external-provider service (when preferred), then a matching preferred persona.
        // Captains equal on every key are peers.
        private static Captain Pick(ModelTierSettings tiers, Mission mission, List<Captain> candidates, Func<int, int> randomPick)
        {
            string? dimension = ResolveHintDimension(tiers, mission.CapabilityHint);
            List<Captain> top = new List<Captain>();
            List<long> best = new List<long>();
            foreach (Captain captain in candidates)
            {
                List<long> key = new List<long>
                {
                    dimension == null ? 0 : ScoreOf(tiers, captain.Model, dimension),
                    captain.PreferenceRank,
                    tiers.PreferNonNativeFirst && IsExternalServed(captain) ? 1 : 0,
                    PrefersPersona(captain, mission.Persona) ? 1 : 0
                };
                int comparison = best.Count == 0 ? 1 : Compare(key, best);
                if (comparison > 0)
                {
                    best = key;
                    top.Clear();
                    top.Add(captain);
                }
                else if (comparison == 0)
                {
                    top.Add(captain);
                }
            }
            if (top.Count == 1) return top[0];

            // Equal captains are peers by model: a model is chosen at random, so a model that several captains
            // run is not favoured by its captain count; the first of its captains takes the work.
            List<string> models = new List<string>();
            foreach (Captain captain in top)
            {
                string model = captain.Model ?? String.Empty;
                if (!models.Contains(model, StringComparer.OrdinalIgnoreCase)) models.Add(model);
            }
            string chosen = models.Count == 1 ? models[0] : models[randomPick(models.Count)];
            return top.First(c => String.Equals(c.Model ?? String.Empty, chosen, StringComparison.OrdinalIgnoreCase));
        }

        private static int Compare(List<long> left, List<long> right)
        {
            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i]) return left[i] > right[i] ? 1 : -1;
            }
            return 0;
        }

        private static bool IsExternalServed(Captain captain)
        {
            return captain.Runtime != AgentRuntimeEnum.OpenCode && !String.IsNullOrWhiteSpace(captain.ApiBaseUrl);
        }

        private static bool PrefersPersona(Captain captain, string? persona)
        {
            return !String.IsNullOrEmpty(persona)
                && !String.IsNullOrEmpty(captain.PreferredPersona)
                && String.Equals(captain.PreferredPersona, persona, StringComparison.OrdinalIgnoreCase);
        }

        private static string? ResolveHintDimension(ModelTierSettings tiers, string? capabilityHint)
        {
            string? hint = PreferredModelTierSelector.NormalizeCapabilityHint(capabilityHint);
            if (hint == null) return null;
            foreach (KeyValuePair<string, string> entry in tiers.CapabilityHintDimensionMap)
            {
                if (String.Equals(entry.Key, hint, StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(entry.Value))
                    return entry.Value;
            }
            return null;
        }

        private static long ScoreOf(ModelTierSettings tiers, string? model, string dimension)
        {
            if (String.IsNullOrEmpty(model)) return -1;
            foreach (KeyValuePair<string, ModelCapabilityProfile> entry in tiers.ModelCapabilityProfiles)
            {
                if (String.Equals(entry.Key, model, StringComparison.OrdinalIgnoreCase))
                    return entry.Value == null ? -1 : entry.Value.GetDimensionScore(dimension);
            }
            return -1;
        }

        #endregion
    }
}
