namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Detects persona model-list entries that no eligible captain can satisfy. A list only reorders
    /// captains Legacy Routing already admitted; an entry that admits nobody is dead. Reporting never
    /// refuses a save or a dispatch.
    /// </summary>
    public static class PersonaModelListHealth
    {
        /// <summary>Event recorded when the capacity-chosen list matched no eligible captain.</summary>
        public const string EventType = "routing.persona_model_list_dead";

        /// <summary>Reason prefix when a list supplied the captain. Followed by the group name and captain id.</summary>
        public const string ReasonAppliedPrefix = "persona_models_applied_";

        /// <summary>Reason when the capacity-chosen list named models no eligible captain runs.</summary>
        public const string ReasonMatchedNothing = "persona_models_matched_nothing";

        /// <summary>
        /// Event recorded when no captain that may serve the persona runs a listed model and can take work, so the
        /// lists stop restricting and every admitted captain is reachable.
        /// </summary>
        public const string FallbackEventType = "routing.persona_model_list_fallback";

        /// <summary>Reason prefix when dead lists fell back to every admitted captain. Followed by the captain id.</summary>
        public const string ReasonDeadFallbackPrefix = "persona_models_dead_fallback:";

        /// <summary>
        /// Whether the captain can take work now or after its current run: a working, stalled or idle captain can;
        /// a benched, quarantined or stopping one cannot.
        /// </summary>
        public static bool CanTakeWork(Captain captain, DateTime nowUtc)
        {
            if (captain == null) return false;
            if (captain.State == CaptainStateEnum.Benched || captain.State == CaptainStateEnum.Stopping) return false;
            return !CaptainQuarantineService.IsQuarantinedAt(captain, nowUtc);
        }

        /// <summary>
        /// Whether the persona's model lists still have a captain to wait for: true when the policy is disabled, the
        /// persona names no model, or a captain of the mission's tenant that may serve the persona, that the persona
        /// routes admit, and that can take work (busy counts) runs a listed model. False means every listed model is
        /// dead for this mission, and waiting for the lists would never end.
        /// </summary>
        public static bool ListsHaveServingCaptain(
            ModelTierSettings tiers,
            UsageRoutingSettings policy,
            Mission mission,
            IReadOnlyList<Captain> roster,
            DateTime nowUtc)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (!policy.Enabled) return true;
            PersonaModelSettings? models = UsageRoutingService.FindPersonaModels(policy, mission.Persona);
            if (models == null || models.Default.Count + models.Stronger.Count + models.Lighter.Count == 0) return true;
            if (roster == null) return false;
            foreach (Captain captain in roster)
            {
                if (captain == null || !CanTakeWork(captain, nowUtc)) continue;
                if (!MissionService.CaptainServesTenant(captain, mission.TenantId)) continue;
                if (!IsEligible(tiers, mission.Persona ?? String.Empty, captain)) continue;
                if (!UsageRoutingService.PersonaRoutesAdmit(policy, mission.Persona, captain)) continue;
                string model = captain.Model ?? String.Empty;
                if (models.Default.Contains(model, StringComparer.OrdinalIgnoreCase)
                    || models.Stronger.Contains(model, StringComparer.OrdinalIgnoreCase)
                    || models.Lighter.Contains(model, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Every model-list entry that no captain who may serve that persona can satisfy.
        /// </summary>
        public static List<DeadPersonaModelEntry> FindDead(
            ModelTierSettings tiers,
            UsageRoutingSettings policy,
            IReadOnlyList<Captain> captains)
        {
            List<DeadPersonaModelEntry> dead = new List<DeadPersonaModelEntry>();
            if (tiers == null || policy?.PersonaModels == null || captains == null) return dead;
            foreach (KeyValuePair<string, PersonaModelSettings> pair in policy.PersonaModels)
            {
                if (pair.Value == null) continue;
                AddDead(dead, pair.Key, "default", pair.Value.Default, tiers, captains);
                AddDead(dead, pair.Key, "lighter", pair.Value.Lighter, tiers, captains);
                AddDead(dead, pair.Key, "stronger", pair.Value.Stronger, tiers, captains);
            }
            return dead;
        }

        /// <summary>
        /// Per-persona floor, eligible captains, and live/dead marks for each list entry.
        /// </summary>
        public static List<PersonaModelRoutingView> BuildViews(
            ModelTierSettings tiers,
            UsageRoutingSettings policy,
            IReadOnlyList<Captain> captains)
        {
            List<PersonaModelRoutingView> views = new List<PersonaModelRoutingView>();
            if (tiers == null || policy?.PersonaModels == null || captains == null) return views;
            foreach (KeyValuePair<string, PersonaModelSettings> pair in policy.PersonaModels)
            {
                if (pair.Value == null) continue;
                CaptainTierEnum? minimumTier = tiers.MinimumTierForPersona(pair.Key);
                List<Captain> eligible = captains
                    .Where(captain => captain != null && IsEligible(tiers, pair.Key, captain))
                    .ToList();
                views.Add(new PersonaModelRoutingView
                {
                    Persona = pair.Key,
                    Floor = minimumTier?.ToString() ?? "none",
                    MinimumTier = minimumTier?.ToString(),
                    EligibleCaptainIds = eligible.Select(captain => captain.Id).ToList(),
                    Default = Mark(pair.Value.Default, pair.Key, tiers, captains),
                    Lighter = Mark(pair.Value.Lighter, pair.Key, tiers, captains),
                    Stronger = Mark(pair.Value.Stronger, pair.Key, tiers, captains)
                });
            }
            return views;
        }

        /// <summary>True when a captain may serve the persona under the lock and the capability floor.</summary>
        public static bool IsEligible(ModelTierSettings tiers, string persona, Captain captain)
        {
            if (captain == null || String.IsNullOrWhiteSpace(persona)) return false;
            if (!MissionService.CaptainAllowsPersona(captain, persona)) return false;
            CaptainTierEnum? minimumTier = tiers?.MinimumTierForPersona(persona);
            List<CaptainTierEnum> order = PreferredModelTierSelector.TierOrder(null, minimumTier);
            return order.Contains(CaptainTierSelector.EffectiveTier(captain));
        }

        /// <summary>True when at least one eligible captain for the persona runs the model.</summary>
        public static bool ModelHasEligibleCaptain(
            ModelTierSettings tiers,
            string persona,
            string model,
            IReadOnlyList<Captain> captains)
        {
            if (String.IsNullOrWhiteSpace(model) || captains == null) return false;
            foreach (Captain captain in captains)
            {
                if (captain == null || !SameModel(captain.Model, model)) continue;
                if (IsEligible(tiers, persona, captain)) return true;
            }
            return false;
        }

        /// <summary>One-line summary for a save response or a startup log.</summary>
        public static string FormatSummary(IReadOnlyList<DeadPersonaModelEntry> dead)
        {
            if (dead == null || dead.Count == 0)
                return "persona model lists: every entry has an eligible captain";
            return "persona model lists: " + dead.Count + " dead ("
                + String.Join("; ", dead.Select(entry => entry.Persona + "." + entry.List + "=" + entry.Model)) + ")";
        }

        /// <summary>Record that the capacity-chosen list matched nothing eligible. Never throws into assignment.</summary>
        public static async Task TryEmitAsync(
            DatabaseDriver database,
            Mission mission,
            UsageRoutingDecision decision,
            CancellationToken token = default)
        {
            if (database == null || mission == null || decision == null) return;
            if (decision.ListsDeadFallback)
            {
                try
                {
                    await database.Events.CreateAsync(new ArmadaEvent
                    {
                        TenantId = mission.TenantId,
                        UserId = mission.UserId,
                        EventType = FallbackEventType,
                        EntityType = "mission",
                        EntityId = mission.Id,
                        MissionId = mission.Id,
                        VesselId = mission.VesselId,
                        VoyageId = mission.VoyageId,
                        Message = "persona model lists for " + (mission.Persona ?? "(none)") + " name no captain that can take work;"
                            + " routing falls back to every admitted captain (" + decision.Reason + ")",
                        Payload = JsonSerializer.Serialize(new { persona = mission.Persona, reason = decision.Reason, dead = decision.DeadListEntries })
                    }, token).ConfigureAwait(false);
                }
                catch
                {
                    // Assignment already chose a captain; a failed event must not fail the mission.
                }
            }
            if (!decision.MatchedNothing && (decision.DeadListEntries == null || decision.DeadListEntries.Count == 0))
                return;
            try
            {
                ArmadaEvent evt = new ArmadaEvent
                {
                    TenantId = mission.TenantId,
                    UserId = mission.UserId,
                    EventType = EventType,
                    EntityType = "mission",
                    EntityId = mission.Id,
                    MissionId = mission.Id,
                    VesselId = mission.VesselId,
                    VoyageId = mission.VoyageId,
                    Message = FormatSummary(decision.DeadListEntries),
                    Payload = JsonSerializer.Serialize(new
                    {
                        persona = mission.Persona,
                        reason = decision.Reason,
                        dead = decision.DeadListEntries
                    })
                };
                await database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch
            {
                // Assignment already chose a captain; a failed event must not fail the mission.
            }
        }

        private static void AddDead(
            List<DeadPersonaModelEntry> dead,
            string persona,
            string list,
            IEnumerable<string> models,
            ModelTierSettings tiers,
            IReadOnlyList<Captain> captains)
        {
            if (models == null) return;
            foreach (string model in models)
            {
                if (String.IsNullOrWhiteSpace(model)) continue;
                if (ModelHasEligibleCaptain(tiers, persona, model, captains)) continue;
                dead.Add(new DeadPersonaModelEntry { Persona = persona, List = list, Model = model.Trim() });
            }
        }

        private static List<PersonaModelListMark> Mark(
            IEnumerable<string> models,
            string persona,
            ModelTierSettings tiers,
            IReadOnlyList<Captain> captains)
        {
            List<PersonaModelListMark> marks = new List<PersonaModelListMark>();
            if (models == null) return marks;
            foreach (string model in models)
            {
                if (String.IsNullOrWhiteSpace(model)) continue;
                marks.Add(new PersonaModelListMark
                {
                    Model = model.Trim(),
                    Live = ModelHasEligibleCaptain(tiers, persona, model, captains)
                });
            }
            return marks;
        }

        private static bool SameModel(string? left, string right)
        {
            return !String.IsNullOrEmpty(left) && String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
