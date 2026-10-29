namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// The one per-captain eligibility rule of the assignment pass. <see cref="Evaluate"/> returns null when a
    /// captain may take the mission and otherwise the reason code that excludes it. The pass builds its candidate
    /// pool by calling it, and the hold that explains an empty pool calls it for the same captains, so the pool and
    /// its explanation cannot disagree.
    /// </summary>
    public static class CaptainAssignmentEligibility
    {
        #region Public-Members

        /// <summary>The captain is not idle.</summary>
        public const string ReasonNotIdle = "not_idle";

        /// <summary>A stored assignment, such as a Harbor attempt, binds the mission to another captain.</summary>
        public const string ReasonBoundToAnotherCaptain = "bound_to_another_captain";

        /// <summary>The captain belongs to another tenant than the mission.</summary>
        public const string ReasonOtherTenant = "other_tenant";

        /// <summary>The captain is quarantined.</summary>
        public const string ReasonQuarantined = "quarantined";

        /// <summary>The captain runtime refused this mission's policy and the mission must not return to it.</summary>
        public const string ReasonExcludedAfterRefusal = "excluded_after_refusal";

        /// <summary>Another mission's in-flight assignment holds the captain.</summary>
        public const string ReasonReserved = "reserved";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether the captain may be placed in the mission's candidate pool.
        /// </summary>
        /// <param name="mission">The mission being assigned.</param>
        /// <param name="captain">The captain.</param>
        /// <param name="requiredCaptainId">Captain the mission is bound to, or null when it is bound to none.</param>
        /// <param name="isQuarantined">Tells whether a captain is quarantined.</param>
        /// <param name="reservedBy">Returns the mission that reserves a captain, or null.</param>
        /// <returns>Null when eligible; otherwise the reason code.</returns>
        public static string? Evaluate(
            Mission mission,
            Captain captain,
            string? requiredCaptainId,
            Func<Captain, bool> isQuarantined,
            Func<string, string?> reservedBy)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (isQuarantined == null) throw new ArgumentNullException(nameof(isQuarantined));
            if (reservedBy == null) throw new ArgumentNullException(nameof(reservedBy));

            if (captain.State != CaptainStateEnum.Idle) return ReasonNotIdle;
            if (!String.IsNullOrWhiteSpace(requiredCaptainId)
                && !String.Equals(captain.Id, requiredCaptainId, StringComparison.Ordinal)) return ReasonBoundToAnotherCaptain;
            // A mission is worked only by a captain of its own tenant. The claim enforces the same rule, so a
            // captain of another tenant is never provisioned a dock it could not claim.
            if (!MissionService.CaptainServesTenant(captain, mission.TenantId)) return ReasonOtherTenant;
            if (isQuarantined(captain)) return ReasonQuarantined;
            if (MissionService.IsExcludedForAssignment(mission, captain)) return ReasonExcludedAfterRefusal;
            string? holder = reservedBy(captain.Id);
            if (holder != null && !String.Equals(holder, mission.Id, StringComparison.Ordinal)) return ReasonReserved;
            return null;
        }

        /// <summary>
        /// Why each captain of the mission's tenant cannot take the mission now: the pool gates of
        /// <see cref="Evaluate"/>, then the persona, model-pin and tier layer of Legacy Routing for the idle
        /// captains that pass them. A captain that can take the mission is absent from the result.
        /// </summary>
        /// <param name="tiers">Model tier settings.</param>
        /// <param name="mission">The mission being assigned.</param>
        /// <param name="roster">Every captain, in any state.</param>
        /// <param name="requiredCaptainId">Captain the mission is bound to, or null.</param>
        /// <param name="isQuarantined">Tells whether a captain is quarantined.</param>
        /// <param name="reservedBy">Returns the mission that reserves a captain, or null.</param>
        /// <returns>Reason code keyed by captain identifier, in roster order.</returns>
        public static Dictionary<string, string> ExplainRoster(
            ModelTierSettings tiers,
            Mission mission,
            IReadOnlyList<Captain> roster,
            string? requiredCaptainId,
            Func<Captain, bool> isQuarantined,
            Func<string, string?> reservedBy)
        {
            if (tiers == null) throw new ArgumentNullException(nameof(tiers));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (roster == null) throw new ArgumentNullException(nameof(roster));

            Dictionary<string, string> reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            List<Captain> gated = new List<Captain>();
            foreach (Captain captain in roster)
            {
                if (captain == null) continue;
                string? reason = Evaluate(mission, captain, requiredCaptainId, isQuarantined, reservedBy);
                if (reason == null) gated.Add(captain);
                else reasons[captain.Id] = reason;
            }
            foreach (KeyValuePair<string, string> layer in LegacyCaptainSelector.ExplainExclusions(tiers, mission, gated))
                reasons[layer.Key] = layer.Value;
            return reasons;
        }

        #endregion
    }
}
