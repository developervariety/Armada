namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// The one rule that applies a mission's requested captain and stored fallback tier at assignment.
    /// </summary>
    /// <remarks>
    /// Every assignment path reaches captain selection through this rule. The pool it receives has already
    /// passed the gates no choice may override: captain state (Idle), tenant, quarantine, refusal exclusion,
    /// in-flight reservations and, when enabled, usage routing. An idle requested captain in that pool is
    /// assigned as an explicit choice, ahead of persona preference and model-tier selection. Otherwise
    /// normal routing runs over captains whose effective tier is at or above the fallback tier: the stored
    /// mission tier when set, else the requested captain's own effective tier. When no pooled captain meets
    /// that floor the mission waits with a named reason; it is never handed to a lower-tier substitute.
    /// A mission with neither field set gets its pool back unchanged. A requested captain that cannot serve
    /// the mission's persona (its allow-list, its runtime's capability, or the persona's minimum tier) is
    /// never an explicit choice: it is an unavailable requested captain with that reason. The mission's
    /// model pin and tier floor are otherwise the operator's choice and do not disqualify it.
    /// </remarks>
    public static class RequestedCaptainAssignmentRule
    {
        #region Public-Members

        /// <summary>
        /// Event type recorded when assignment substitutes for, or waits on, a requested captain or tier.
        /// </summary>
        public const string EventType = "mission.requested_captain";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a mission stores a requested captain or a fallback tier.
        /// </summary>
        /// <param name="mission">Mission being assigned.</param>
        /// <returns>True when either field is set.</returns>
        public static bool HasRequest(Mission? mission)
        {
            if (mission == null) return false;
            return !String.IsNullOrWhiteSpace(mission.RequestedCaptainId) || mission.Tier.HasValue;
        }

        /// <summary>
        /// Resolve the requested captain and fallback tier for a mission of one persona: the captain the
        /// override names, else the persona's default captain; the fallback tier the override stores.
        /// </summary>
        /// <param name="chosen">The captain override that applies to the persona, or null.</param>
        /// <param name="personaDefaultCaptainId">The persona record's default captain, or null.</param>
        /// <returns>The resolution; both fields null when nothing is requested.</returns>
        public static RequestedCaptainResolution ResolveRequest(CaptainAssignmentOverride? chosen, string? personaDefaultCaptainId)
        {
            RequestedCaptainResolution resolution = new RequestedCaptainResolution();
            if (chosen != null)
            {
                resolution.CaptainId = String.IsNullOrWhiteSpace(chosen.CaptainId) ? null : chosen.CaptainId.Trim();
                resolution.FallbackTier = chosen.FallbackTier;
            }

            if (resolution.CaptainId == null && !String.IsNullOrWhiteSpace(personaDefaultCaptainId))
                resolution.CaptainId = personaDefaultCaptainId.Trim();
            return resolution;
        }

        /// <summary>
        /// Why a requested captain may never take a mission of the supplied persona, or null when it may. The
        /// persona allow-list, the runtime's capability for the persona, and the persona's minimum tier
        /// disqualify it; the mission's model pin and tier floor do not, because naming the captain is the
        /// operator's explicit choice of its model and tier.
        /// </summary>
        /// <param name="requested">The requested captain.</param>
        /// <param name="persona">Mission persona, if any.</param>
        /// <param name="tiers">Model tier settings naming the persona minimum tiers; null applies no minimum.</param>
        /// <returns>The reason, or null when the captain is eligible.</returns>
        public static string? DescribeIneligibility(Captain requested, string? persona, ModelTierSettings? tiers)
        {
            if (requested == null) throw new ArgumentNullException(nameof(requested));
            if (!MissionService.CaptainAllowsPersona(requested, persona))
                return "not eligible for persona " + persona + " (its persona allow-list or runtime capability excludes it)";
            if (MissionService.FailsPersonaMinimumTier(requested, persona, tiers))
                return "not eligible for persona " + persona + ", whose minimum tier is " + tiers!.MinimumTierForPersona(persona);
            return null;
        }

        /// <summary>
        /// Apply the requested captain and fallback tier to a gated pool of idle captains.
        /// </summary>
        /// <param name="mission">Mission being assigned.</param>
        /// <param name="requestedCaptain">The stored requested captain's record, or null when it does not exist.</param>
        /// <param name="requestedUnavailableReason">Why the requested captain cannot take the mission now, or null when nothing prevents it.</param>
        /// <param name="gatedIdleCaptains">Idle captains that passed every non-overridable gate.</param>
        /// <returns>The decision.</returns>
        public static RequestedCaptainAssignmentDecision Decide(
            Mission mission,
            Captain? requestedCaptain,
            string? requestedUnavailableReason,
            IReadOnlyList<Captain> gatedIdleCaptains)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            List<Captain> pool = gatedIdleCaptains == null ? new List<Captain>() : gatedIdleCaptains.Where(c => c != null).ToList();

            RequestedCaptainAssignmentDecision decision = new RequestedCaptainAssignmentDecision();
            decision.Candidates = pool;
            if (!HasRequest(mission)) return decision;

            string? requestedId = String.IsNullOrWhiteSpace(mission.RequestedCaptainId) ? null : mission.RequestedCaptainId!.Trim();
            decision.RequestedCaptainId = requestedId;

            if (requestedId != null && requestedCaptain != null && requestedUnavailableReason == null)
            {
                Captain? pooled = pool.FirstOrDefault(c => String.Equals(c.Id, requestedId, StringComparison.Ordinal));
                if (pooled != null)
                {
                    decision.Outcome = RequestedCaptainOutcomeEnum.AssignRequested;
                    decision.Captain = pooled;
                    return decision;
                }

                requestedUnavailableReason = "not assignable";
            }

            CaptainTierEnum? floor = mission.Tier;
            if (!floor.HasValue && requestedCaptain != null)
                floor = CaptainTierSelector.EffectiveTier(requestedCaptain);
            decision.FallbackTier = floor;

            string requestPart = requestedId == null
                ? "No requested captain"
                : requestedCaptain == null
                    ? "Requested captain " + requestedId + " was not found"
                    : "Requested captain " + requestedId + " is " + (requestedUnavailableReason ?? "not assignable");

            if (!floor.HasValue)
            {
                decision.Outcome = RequestedCaptainOutcomeEnum.RequestedNotFound;
                decision.Reason = requestPart + "; no fallback tier is stored, so normal routing applies.";
                return decision;
            }

            List<Captain> atOrAbove = pool.Where(c => CaptainTierSelector.EffectiveTier(c) >= floor.Value).ToList();
            decision.Candidates = atOrAbove;
            if (atOrAbove.Count == 0)
            {
                decision.Outcome = RequestedCaptainOutcomeEnum.WaitForTier;
                decision.Reason = requestPart + "; waiting because no assignable idle captain is at or above fallback tier " + floor.Value + ".";
                return decision;
            }

            decision.Outcome = RequestedCaptainOutcomeEnum.FallbackByTier;
            decision.Reason = requestPart + "; falling back to an idle captain at or above tier " + floor.Value + ".";
            return decision;
        }

        /// <summary>
        /// Keep only the captains at the lowest effective tier present, so a fallback does not consume a
        /// stronger captain while one at the floor is idle.
        /// </summary>
        /// <param name="captains">Eligible captains, all already at or above the floor.</param>
        /// <returns>The captains at the lowest effective tier, in their original order.</returns>
        public static List<Captain> NarrowToLowestTier(IReadOnlyList<Captain> captains)
        {
            if (captains == null || captains.Count == 0) return new List<Captain>();
            CaptainTierEnum lowest = captains.Min(CaptainTierSelector.EffectiveTier);
            return captains.Where(c => CaptainTierSelector.EffectiveTier(c) == lowest).ToList();
        }

        #endregion
    }
}
