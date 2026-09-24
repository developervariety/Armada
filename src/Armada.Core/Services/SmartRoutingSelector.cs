namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Smart Routing: the Legacy Routing order, restricted by optional persona routes, filtered by account usage
    /// (Exhausted removed, Low and Reserve demoted), and grouped by the persona's model preference with the
    /// capacity decision choosing the group tried first. It never re-ranks captains inside a group, and it never
    /// admits a captain the Legacy Routing eligibility layer (persona lock and tier floor) excluded.
    /// </summary>
    public static class SmartRoutingSelector
    {
        #region Public-Members

        /// <summary>Smart Routing is disabled; the Legacy Routing order stands.</summary>
        public const string ReasonDisabled = "usage_routing_disabled";

        /// <summary>No captain passed the Legacy Routing constraints.</summary>
        public const string ReasonNoLegacyCandidate = "no_legacy_candidate";

        /// <summary>Every candidate was removed by usage or account capacity.</summary>
        public const string ReasonUsageBlocked = "usage_exhausted_or_account_capacity";

        /// <summary>The first candidate keeps its Legacy Routing position.</summary>
        public const string ReasonLegacyOrder = "legacy_order_with_allowance";

        /// <summary>Only demoted (Low or Reserve) candidates remain.</summary>
        public const string ReasonDemotedOnly = "low_allowance_no_normal_fallback";

        /// <summary>Prefix of the reason when a persona model group supplied the captain.</summary>
        public const string ReasonGroupPrefix = "persona_models_";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Order the pool for the mission. When the policy is disabled the result is the Legacy Routing order and
        /// nothing else runs. Never throws for a decision failure.
        /// </summary>
        /// <param name="request">The selection inputs.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The decision; its first candidate is the choice.</returns>
        public static async Task<UsageRoutingDecision> SelectAsync(SmartRoutingRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            UsageRoutingDecision decision = new UsageRoutingDecision();
            Mission mission = request.Mission;
            UsageRoutingSettings policy = request.Policy;

            if (!policy.Enabled)
            {
                decision.LegacyOrder = LegacyCaptainSelector.Order(request.Tiers, mission, request.Pool, request.NarrowToLowestTier, request.RandomPick);
                decision.Candidates = new List<Captain>(decision.LegacyOrder);
                decision.Verdicts.AddRange(EligibilityVerdicts(request.Tiers, mission, request.Pool));
                decision.Reason = ReasonDisabled;
                return decision;
            }

            List<UsageRouteSettings>? routes = UsageRoutingService.FindPersonaRoutes(policy, mission.Persona);
            decision.HasPersonaRoutes = routes != null;
            List<Captain> restricted = new List<Captain>();
            List<SmartRoutingCaptainVerdict> outside = new List<SmartRoutingCaptainVerdict>();
            foreach (Captain captain in request.Pool)
            {
                if (UsageRoutingService.PersonaRoutesAdmit(policy, mission.Persona, captain)) restricted.Add(captain);
                else outside.Add(new SmartRoutingCaptainVerdict { CaptainId = captain.Id, Model = captain.Model, Layer = UsageRoutingService.LayerRoutes, Outcome = UsageRoutingService.OutcomeOutsideRoutes, Reason = UsageRoutingService.ReasonOutsideRoutes });
            }

            decision.LegacyOrder = LegacyCaptainSelector.Order(request.Tiers, mission, restricted, request.NarrowToLowestTier, request.RandomPick);
            List<Captain> kept = new List<Captain>();
            List<Captain> demoted = new List<Captain>();
            foreach (Captain captain in decision.LegacyOrder)
            {
                SmartRoutingCaptainVerdict verdict = request.Usage.ClassifyCaptain(policy, mission, captain, request.BusyCaptainIds, request.NowUtc);
                decision.Verdicts.Add(verdict);
                if (verdict.Outcome == UsageRoutingService.OutcomeKept) kept.Add(captain);
                else if (verdict.Outcome == UsageRoutingService.OutcomeDemoted) demoted.Add(captain);
            }
            decision.Verdicts.AddRange(outside);
            decision.Verdicts.AddRange(EligibilityVerdicts(request.Tiers, mission, restricted));

            // The retry skip list outranks demotion: a retry avoids the captain that just failed while any other
            // usable captain remains, exactly as Legacy Routing does over its own pool.
            List<Captain> usable = kept.Concat(demoted).ToList();
            if (usable.Any(c => !MissionService.IsCaptainOnRetrySkipList(mission.RetrySkipCaptainIds, c.Id)))
            {
                kept = kept.Where(c => !MissionService.IsCaptainOnRetrySkipList(mission.RetrySkipCaptainIds, c.Id)).ToList();
                demoted = demoted.Where(c => !MissionService.IsCaptainOnRetrySkipList(mission.RetrySkipCaptainIds, c.Id)).ToList();
            }
            List<Captain> filtered = kept.Concat(demoted).ToList();
            HashSet<string> cursorApiAvailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Captain captain in filtered)
            {
                UsageAccountSettings? account = policy.Accounts.FirstOrDefault(a => a != null
                    && a.CaptainIds.Contains(captain.Id, StringComparer.OrdinalIgnoreCase));
                if (account != null && request.Usage.HasAvailableCursorApiPool(account, captain.Model, request.NowUtc))
                    cursorApiAvailable.Add(captain.Id);
            }
            if (cursorApiAvailable.Count > 0)
                filtered = filtered.OrderByDescending(captain => cursorApiAvailable.Contains(captain.Id)).ToList();

            PersonaModelSettings? models = UsageRoutingService.FindPersonaModels(policy, mission.Persona);
            bool concretePin = !String.IsNullOrEmpty(mission.PreferredModel) && !PreferredModelTierSelector.IsTierSelector(mission.PreferredModel);
            string groupReason = String.Empty;
            if (models != null && !concretePin)
            {
                decision.HasPersonaModels = true;
                CapacityReading reading = request.Capacity != null
                    ? await request.Capacity.ResolveAsync(mission, models, request.WorkText, request.UseCapacityCache, token).ConfigureAwait(false)
                    : new CapacityReading
                    {
                        Choice = CapacityChoiceEnum.Default,
                        Source = models.Lighter.Count == 0 && models.Stronger.Count == 0 ? CapacityEscalationResolver.SourceNoAlternative : CapacityEscalationResolver.SourceNoAdapter
                    };
                decision.Capacity = reading.Choice;
                decision.CapacitySource = reading.Source;

                List<CapacityChoiceEnum> order = new List<CapacityChoiceEnum> { reading.Choice };
                foreach (CapacityChoiceEnum next in new[] { CapacityChoiceEnum.Default, CapacityChoiceEnum.Stronger, CapacityChoiceEnum.Lighter })
                    if (!order.Contains(next)) order.Add(next);

                HashSet<string> placed = new HashSet<string>(StringComparer.Ordinal);
                List<Captain> grouped = new List<Captain>();
                SmartRoutingModelGroup cursorApi = new SmartRoutingModelGroup { Name = "cursor_api" };
                foreach (Captain captain in filtered)
                {
                    if (!cursorApiAvailable.Contains(captain.Id)) continue;
                    placed.Add(captain.Id);
                    cursorApi.CaptainIds.Add(captain.Id);
                    grouped.Add(captain);
                }
                if (cursorApi.CaptainIds.Count > 0)
                {
                    decision.Groups.Add(cursorApi);
                    groupReason = ReasonGroupPrefix + cursorApi.Name;
                }
                List<string> chosenModels = reading.Choice == CapacityChoiceEnum.Lighter ? models.Lighter
                    : reading.Choice == CapacityChoiceEnum.Stronger ? models.Stronger : models.Default;
                if (chosenModels.Count > 0
                    && !decision.LegacyOrder.Any(captain => chosenModels.Contains(captain.Model ?? String.Empty, StringComparer.OrdinalIgnoreCase)))
                {
                    decision.MatchedNothing = true;
                    foreach (string model in chosenModels)
                    {
                        if (String.IsNullOrWhiteSpace(model)) continue;
                        if (PersonaModelListHealth.ModelHasEligibleCaptain(request.Tiers, mission.Persona, model, request.Pool)) continue;
                        decision.DeadListEntries.Add(new DeadPersonaModelEntry
                        {
                            Persona = mission.Persona ?? String.Empty,
                            List = TypedCapacityEscalationAdapter.OptionName(reading.Choice),
                            Model = model.Trim()
                        });
                    }
                }
                foreach (CapacityChoiceEnum choice in order)
                {
                    List<string> list = choice == CapacityChoiceEnum.Lighter ? models.Lighter : choice == CapacityChoiceEnum.Stronger ? models.Stronger : models.Default;
                    SmartRoutingModelGroup group = new SmartRoutingModelGroup { Name = TypedCapacityEscalationAdapter.OptionName(choice), Models = new List<string>(list) };
                    foreach (Captain captain in filtered)
                    {
                        if (placed.Contains(captain.Id) || !list.Contains(captain.Model ?? String.Empty, StringComparer.OrdinalIgnoreCase)) continue;
                        placed.Add(captain.Id);
                        group.CaptainIds.Add(captain.Id);
                        grouped.Add(captain);
                    }
                    if (groupReason.Length == 0 && group.CaptainIds.Count > 0) groupReason = ReasonGroupPrefix + group.Name;
                    decision.Groups.Add(group);
                }
                // A captain whose model is in no list stays reachable after every group, so a persona is never
                // starved by its model preference.
                SmartRoutingModelGroup unlisted = new SmartRoutingModelGroup { Name = "unlisted" };
                foreach (Captain captain in filtered)
                {
                    if (placed.Contains(captain.Id)) continue;
                    unlisted.CaptainIds.Add(captain.Id);
                    grouped.Add(captain);
                }
                if (groupReason.Length == 0 && unlisted.CaptainIds.Count > 0) groupReason = ReasonGroupPrefix + unlisted.Name;
                decision.Groups.Add(unlisted);
                decision.Candidates = grouped;
            }
            else
            {
                decision.Candidates = filtered;
            }

            if (decision.Candidates.Count > 0)
            {
                Captain first = decision.Candidates[0];
                bool firstDemoted = demoted.Contains(first);
                if (decision.MatchedNothing)
                    decision.Reason = PersonaModelListHealth.ReasonMatchedNothing;
                else if (groupReason.StartsWith(ReasonGroupPrefix, StringComparison.Ordinal)
                    && !groupReason.EndsWith("unlisted", StringComparison.Ordinal))
                    decision.Reason = PersonaModelListHealth.ReasonAppliedPrefix + groupReason.Substring(ReasonGroupPrefix.Length) + ":" + first.Id
                        + (firstDemoted ? "_" + ReasonDemotedOnly : String.Empty);
                else
                    decision.Reason = groupReason.Length > 0 ? groupReason + (firstDemoted ? "_" + ReasonDemotedOnly : String.Empty)
                        : firstDemoted ? ReasonDemotedOnly : ReasonLegacyOrder;
            }
            else if (decision.LegacyOrder.Count == 0)
            {
                decision.Reason = ReasonNoLegacyCandidate;
            }
            else
            {
                // A login problem or provider hold names the account, not its allowance; report the first such code
                // so a waiting mission says why instead of reading as a generic allowance shortage.
                SmartRoutingCaptainVerdict? account = decision.Verdicts.FirstOrDefault(v => v.Outcome == UsageRoutingService.OutcomeRemoved && v.Reason.StartsWith("account_", StringComparison.Ordinal));
                decision.Reason = account?.Reason ?? ReasonUsageBlocked;
            }
            return decision;
        }

        #endregion

        #region Private-Methods

        // The eligibility layer runs before Smart Routing and decides membership: a captain it excludes is never
        // reordered into the choice, whatever the persona model lists or the capacity reading say.
        private static List<SmartRoutingCaptainVerdict> EligibilityVerdicts(ModelTierSettings tiers, Mission mission, List<Captain> pool)
        {
            List<SmartRoutingCaptainVerdict> verdicts = new List<SmartRoutingCaptainVerdict>();
            Dictionary<string, string> reasons = LegacyCaptainSelector.ExplainExclusions(tiers, mission, pool);
            foreach (Captain captain in pool)
            {
                if (captain == null || !reasons.TryGetValue(captain.Id, out string? reason)) continue;
                verdicts.Add(new SmartRoutingCaptainVerdict
                {
                    CaptainId = captain.Id,
                    Model = captain.Model,
                    Layer = LegacyCaptainSelector.LayerEligibility,
                    Outcome = UsageRoutingService.OutcomeExcluded,
                    Reason = reason
                });
            }
            return verdicts;
        }

        #endregion
    }
}
