namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Pure static helper that maps preferredModel tier values (low/mid/high) to
    /// a concrete model name by randomly selecting across eligible peer models.
    /// Tier values are case-insensitive. Literal model names pass through unchanged
    /// and are handled by the calling dispatcher.
    ///
    /// Tier membership is config-driven through <see cref="ModelTierSettings"/>:
    /// the MidTierModels and HighTierModels lists determine which concrete models
    /// belong to each tier. Family-classification rules, specialist personas, and
    /// within-tier policy (non-native preference, random vs preference-order) also
    /// come from that object. Product defaults are empty and policy-neutral: no
    /// model family is assumed, no persona is reserved for high, and selection is
    /// random within a tier. There is no low tier; the legacy low selector maps to
    /// mid.
    /// </summary>
    public static class PreferredModelTierSelector
    {
        #region Public-Members

        /// <summary>Canonical tier name for low-complexity work. Legacy: a low request maps to the mid tier.</summary>
        public const string LowTier = "low";

        /// <summary>Canonical tier name for mid-complexity work.</summary>
        public const string MidTier = "mid";

        /// <summary>Canonical tier name for high-complexity work.</summary>
        public const string HighTier = "high";

        /// <summary>
        /// Capability hint requesting a captain with strong audit reasoning and
        /// cross-repository analysis fit. Maps to the AuditReasoningFit profile dimension.
        /// </summary>
        public const string AuditHint = "audit";

        /// <summary>
        /// Capability hint requesting a captain suited for reasoning-intensive tasks.
        /// Maps to the AuditReasoningFit profile dimension.
        /// </summary>
        public const string ReasoningHeavyHint = "reasoning-heavy";

        /// <summary>
        /// Capability hint requesting a captain optimized for high-volume mechanical
        /// coding work. Maps to the MechanicalThroughput profile dimension.
        /// </summary>
        public const string MechanicalHint = "mechanical";

        /// <summary>
        /// Capability hint requesting a fast, low-cost captain for documentation-only
        /// tasks. Maps to the MechanicalThroughput profile dimension.
        /// </summary>
        public const string DocOnlyHint = "doc-only";

        #endregion

        #region Private-Members

        private static readonly Dictionary<string, string> _Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "quick", MidTier },
            { "medium", MidTier }
        };

        private static readonly HashSet<string> _TierNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LowTier, MidTier, HighTier, "quick", "medium"
        };

        private static readonly HashSet<string> _CapabilityHintNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AuditHint, ReasoningHeavyHint, MechanicalHint, DocOnlyHint
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns true if the value is a recognized tier selector (mid, high, low as a legacy alias, or an alias).
        /// Returns false for null, empty, and literal model names.
        /// </summary>
        public static bool IsTierSelector(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            return _TierNames.Contains(value);
        }

        /// <summary>
        /// Returns true when the value is a recognized capability hint (audit, reasoning-heavy,
        /// mechanical, or doc-only). Matching is case-insensitive. Returns false for null, empty,
        /// whitespace, and unrecognized values.
        /// </summary>
        public static bool IsCapabilityHint(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            return _CapabilityHintNames.Contains(value);
        }

        /// <summary>
        /// Normalizes a capability hint to its canonical lowercase form. Returns null for
        /// null, empty, whitespace, or unrecognized hints -- unrecognized hints degrade
        /// gracefully to no-hint behavior without throwing.
        /// </summary>
        public static string? NormalizeCapabilityHint(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            if (String.Equals(value, AuditHint, StringComparison.OrdinalIgnoreCase)) return AuditHint;
            if (String.Equals(value, ReasoningHeavyHint, StringComparison.OrdinalIgnoreCase)) return ReasoningHeavyHint;
            if (String.Equals(value, MechanicalHint, StringComparison.OrdinalIgnoreCase)) return MechanicalHint;
            if (String.Equals(value, DocOnlyHint, StringComparison.OrdinalIgnoreCase)) return DocOnlyHint;
            return null;
        }

        /// <summary>
        /// Returns true when the persona is in the supplied specialist set.
        /// Worker and null personas return false. When <paramref name="specialistPersonas"/>
        /// is null or empty, no persona is reserved for high: product defaults are empty.
        /// </summary>
        /// <param name="persona">Persona name to test.</param>
        /// <param name="specialistPersonas">Optional override set; null uses the built-in default.</param>
        public static bool RequiresHighTier(string? persona, IReadOnlyCollection<string>? specialistPersonas = null)
        {
            return IsSpecialistPersona(persona, specialistPersonas);
        }

        /// <summary>
        /// Returns a PreferredModel value safe to store on a mission with the given persona.
        /// For specialist personas (Judge, Architect, etc.) this upgrades any tier selector
        /// below "high" to "high". Null/empty preferredModel becomes "high" when the persona
        /// requires it; literal model names are passed through unchanged (operator-pinned
        /// literals stay honest -- the dispatcher's tier-fallback handles the runtime case
        /// if no captain matches). When <paramref name="specialistPersonas"/> is null or
        /// empty, no persona is treated as a specialist.
        /// </summary>
        /// <param name="preferredModel">Requested tier selector or literal model name.</param>
        /// <param name="persona">Persona the mission requires.</param>
        /// <param name="specialistPersonas">Optional specialist set; null or empty treats no persona as a specialist.</param>
        public static string? EnforceHighTierForPersona(
            string? preferredModel,
            string? persona,
            IReadOnlyCollection<string>? specialistPersonas = null)
        {
            if (!RequiresHighTier(persona, specialistPersonas)) return preferredModel;
            if (String.IsNullOrWhiteSpace(preferredModel)) return HighTier;
            if (IsTierSelector(preferredModel))
            {
                if (String.Equals(NormalizeTier(preferredModel), HighTier, StringComparison.OrdinalIgnoreCase))
                    return preferredModel;
                return HighTier;
            }
            return preferredModel;
        }

        /// <summary>
        /// Returns a PreferredModel value safe to store on a mission whose persona may differ
        /// from the mission the value was inherited from. This is the two-way counterpart to
        /// <see cref="EnforceHighTierForPersona"/>: it upgrades to "high" for specialist
        /// personas exactly as that method does, and it additionally caps a "high" selector
        /// down to "mid" for a persona that is not a specialist.
        ///
        /// The cap matters because the high tier is reserved for specialist and reviewer
        /// personas. A non-specialist persona carrying "high" is unassignable whenever the
        /// captain roster has no high-tier captain that accepts that persona: the mission
        /// waits at WaitingForIdleCaptain forever while captains sit Idle, which reads as a
        /// capacity problem and is not one. Inheriting a tier across a persona change is the
        /// way that state is normally reached.
        ///
        /// Literal model names are passed through unchanged so operator-pinned literals stay
        /// honest, and a null or empty value is only filled in when the persona requires high.
        /// </summary>
        /// <param name="preferredModel">Requested tier selector or literal model name.</param>
        /// <param name="persona">Persona the mission will actually run as.</param>
        /// <param name="specialistPersonas">Optional specialist set; null or empty treats no persona as a specialist.</param>
        public static string? ResolveTierForPersona(
            string? preferredModel,
            string? persona,
            IReadOnlyCollection<string>? specialistPersonas = null)
        {
            if (RequiresHighTier(persona, specialistPersonas))
                return EnforceHighTierForPersona(preferredModel, persona, specialistPersonas);

            if (String.IsNullOrWhiteSpace(preferredModel)) return preferredModel;
            if (!IsTierSelector(preferredModel)) return preferredModel;
            if (String.Equals(NormalizeTier(preferredModel), HighTier, StringComparison.OrdinalIgnoreCase))
                return MidTier;

            return preferredModel;
        }

        /// <summary>
        /// Normalizes a tier selector value to its canonical form (mid or high).
        /// The legacy <c>low</c> selector (and its <c>quick</c> alias) maps to mid:
        /// there is no low tier, so a low request is served by the mid tier.
        /// Throws <see cref="ArgumentException"/> if value is not a tier selector.
        /// </summary>
        public static string NormalizeTier(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Tier value must not be null or empty.", nameof(value));

            if (String.Equals(value, LowTier, StringComparison.OrdinalIgnoreCase)) return MidTier;
            if (String.Equals(value, MidTier, StringComparison.OrdinalIgnoreCase)) return MidTier;
            if (String.Equals(value, HighTier, StringComparison.OrdinalIgnoreCase)) return HighTier;

            string? alias = null;
            if (_Aliases.TryGetValue(value, out alias)) return NormalizeTier(alias);

            throw new ArgumentException("Unknown tier selector: " + value, nameof(value));
        }

        /// <summary>
        /// Returns the model names for the specified tier (mid or high). The list is
        /// sourced from <paramref name="modelTierSettings"/> when supplied, otherwise from
        /// the built-in defaults. The returned collection is read-only. A legacy low
        /// request resolves to the mid tier; there is no low tier.
        /// </summary>
        /// <param name="tier">Tier selector value (mid, high, low as a legacy alias, or a tier alias).</param>
        /// <param name="modelTierSettings">Optional tier membership configuration; null uses built-in defaults.</param>
        public static IReadOnlyList<string> GetTierModels(string tier, ModelTierSettings? modelTierSettings = null)
        {
            ModelTierSettings settings = modelTierSettings ?? CreateDefaultSettings();
            string normalized = NormalizeTier(tier);
            if (normalized == MidTier) return settings.MidTierModels.AsReadOnly();
            return settings.HighTierModels.AsReadOnly();
        }

        /// <summary>
        /// Returns all model names in the specified tier and every tier above it.
        /// Used to validate whether a pinned captain's model is acceptable for the
        /// requested tier or its upward fallback chain. A legacy low request resolves
        /// to mid and above; there is no low tier.
        /// </summary>
        /// <param name="tier">Tier selector value (mid, high, low as a legacy alias, or a tier alias).</param>
        /// <param name="modelTierSettings">Optional tier membership configuration; null uses built-in defaults.</param>
        public static IReadOnlyList<string> GetTierAndAboveModels(string tier, ModelTierSettings? modelTierSettings = null)
        {
            ModelTierSettings settings = modelTierSettings ?? CreateDefaultSettings();
            string normalized = NormalizeTier(tier);
            List<string> result = new List<string>();
            if (normalized == MidTier)
            {
                result.AddRange(settings.MidTierModels);
                result.AddRange(settings.HighTierModels);
            }
            else
            {
                result.AddRange(settings.HighTierModels);
            }
            return result;
        }

        /// <summary>
        /// Classifies a concrete model name into its complexity tier (mid or high),
        /// or null when the model is not recognized as belonging to any tier. A model counts
        /// when it is in the configured tier membership lists. When those lists miss, the
        /// configured family-classification rules are applied in order. Product defaults
        /// have empty lists and empty rules, so an unclassified model stays unclassified.
        /// </summary>
        /// <param name="model">Concrete model name (not a tier selector).</param>
        /// <param name="modelTierSettings">Optional tier membership configuration; null uses built-in defaults.</param>
        /// <returns>"low", "mid", "high", or null when unrecognized.</returns>
        public static string? ClassifyModel(string? model, ModelTierSettings? modelTierSettings = null)
        {
            if (String.IsNullOrWhiteSpace(model)) return null;
            string normalized = model.Trim();
            ModelTierSettings settings = modelTierSettings ?? CreateDefaultSettings();

            // Configured membership lists win first -- they are the authority for alias-style
            // names that intentionally do not match a pattern, and for explicit entries.
            if (ContainsModel(settings.HighTierModels, normalized)) return HighTier;
            if (ContainsModel(settings.MidTierModels, normalized)) return MidTier;

            return ClassifyByFamilyRules(normalized, settings.FamilyClassificationRules);
        }

        /// <summary>
        /// Returns true when the given concrete model belongs to the requested tier or any
        /// tier above it (mid &lt; high). Used to validate a captain's model against a
        /// tier pin while honoring the upward-only fallback chain. Returns false for models
        /// that classify into no tier.
        /// </summary>
        /// <param name="model">Concrete model name (not a tier selector).</param>
        /// <param name="requestedTier">Tier selector value (mid, high, or a legacy alias).</param>
        /// <param name="modelTierSettings">Optional tier membership configuration; null uses built-in defaults.</param>
        public static bool ModelMatchesTierOrAbove(string? model, string requestedTier, ModelTierSettings? modelTierSettings = null)
        {
            string? modelTier = ClassifyModel(model, modelTierSettings);
            if (modelTier == null) return false;
            return TierRank(modelTier) >= TierRank(NormalizeTier(requestedTier));
        }

        /// <summary>
        /// Selects a concrete model name based on which idle captains are eligible for the
        /// given persona, honoring tier reservation. Specialist personas only ever resolve to
        /// high-tier captains. Non-specialist personas prefer their requested tier, then the
        /// other non-high tier, and fall up to high only as a last resort -- so a high-tier
        /// captain is never handed to non-specialist work while a mid/low captain sits idle.
        /// Within a tier, models are tried in the configured preference order; the first
        /// model with at least one idle, persona-eligible captain is selected. Tiers without
        /// a configured preference order fall back to random selection across eligible models.
        /// When a recognized capability hint is supplied, eligible models within each tier are
        /// sorted descending by their profile score for the hint's mapped dimension before the
        /// preference-order step, with unprofiled models trailing. Unknown or empty hints
        /// degrade gracefully to the existing preference-order or random path.
        /// </summary>
        /// <param name="tierValue">Tier selector value (mid, high, or a legacy alias).</param>
        /// <param name="idleCaptains">All currently idle captains.</param>
        /// <param name="persona">Optional persona name the mission requires.</param>
        /// <param name="randomPick">
        /// Delegate that accepts an exclusive upper bound and returns a random index in
        /// [0, upperBound). Inject a deterministic function in tests. Used only for tiers
        /// that do not have a configured within-tier preference order.
        /// </param>
        /// <param name="specialistPersonas">
        /// Optional override set of specialist persona names; null uses the built-in default.
        /// </param>
        /// <param name="withinTierPreferenceOrder">
        /// Optional per-tier model preference order. The first listed model with an idle,
        /// persona-eligible captain is chosen. Null or missing entries use random selection.
        /// </param>
        /// <param name="modelTierSettings">Optional tier membership configuration; null uses built-in defaults.</param>
        /// <param name="capabilityHint">
        /// Optional capability hint (audit, reasoning-heavy, mechanical, doc-only). When
        /// recognized and the settings contain a matching profile dimension, eligible models
        /// within a tier are sorted by that dimension score before the preference-order step.
        /// Null, empty, and unrecognized hints are treated as no hint.
        /// </param>
        /// <returns>
        /// A model name string if an eligible model was found, or null if no idle captain
        /// with a tier model is available (mission stays Pending).
        /// </returns>
        public static string? SelectModel(
            string tierValue,
            IReadOnlyList<Captain> idleCaptains,
            string? persona,
            Func<int, int> randomPick,
            IReadOnlyCollection<string>? specialistPersonas = null,
            IReadOnlyDictionary<string, List<string>>? withinTierPreferenceOrder = null,
            ModelTierSettings? modelTierSettings = null,
            string? capabilityHint = null)
        {
            if (randomPick == null)
                throw new ArgumentNullException(nameof(randomPick));

            ModelTierSettings settings = modelTierSettings ?? CreateDefaultSettings();
            if (!settings.HasConfiguredTierMembership)
                return SelectFromUnconfiguredPool(idleCaptains, persona, randomPick);

            string normalized = NormalizeTier(tierValue);
            IReadOnlyCollection<string>? resolvedSpecialists = specialistPersonas ?? settings.SpecialistPersonas;
            bool isSpecialist = IsSpecialistPersona(persona, resolvedSpecialists);
            string[] tierOrder = BuildTierOrder(isSpecialist, normalized);
            string? resolvedHint = NormalizeCapabilityHint(capabilityHint);
            bool preferNonNative = settings.PreferNonNativeFirst;
            bool usePreferenceOrderStrategy = settings.UsesPreferenceOrderThenRandom();
            IReadOnlyDictionary<string, List<string>>? effectivePreferenceOrder =
                withinTierPreferenceOrder ?? settings.WithinTierPreferenceOrder;

            foreach (string tier in tierOrder)
            {
                // Collect the distinct models of idle, persona-eligible captains that classify
                // into this tier. Working from the captains' actual models (rather than a fixed
                // list of known model strings) is what lets a freshly-upgraded model register
                // automatically, as long as ClassifyModel recognizes its family.
                List<string> eligibleModels = new List<string>();
                foreach (Captain captain in idleCaptains)
                {
                    if (captain == null || String.IsNullOrEmpty(captain.Model))
                        continue;
                    if (!String.Equals(ClassifyModel(captain.Model, modelTierSettings), tier, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!IsPersonaEligible(captain, persona))
                        continue;

                    if (!ContainsModel(eligibleModels, captain.Model))
                        eligibleModels.Add(captain.Model);
                }

                if (eligibleModels.Count == 0)
                    continue;

                // Non-native-first is opt-in. When enabled, models with an idle
                // external-provider captain (non-OpenCode runtime carrying its own base URL)
                // are selected before native-only models. OpenCode-runtime captains count as
                // native. When disabled, every eligible model stays in one random pool.
                List<string> selectionPool = eligibleModels;
                if (preferNonNative)
                {
                    List<string> externalEligible = new List<string>();
                    List<string> nativeEligible = new List<string>();
                    foreach (string model in eligibleModels)
                    {
                        if (HasExternalIdleCaptain(idleCaptains, model, persona))
                            externalEligible.Add(model);
                        else
                            nativeEligible.Add(model);
                    }
                    selectionPool = externalEligible.Count > 0 ? externalEligible : nativeEligible;
                }

                IReadOnlyList<string>? preferenceOrder = null;
                bool hasConfiguredPreferenceOrder = TryGetWithinTierPreferenceOrder(tier, effectivePreferenceOrder, out preferenceOrder);
                bool hasPreferenceOrder = usePreferenceOrderStrategy && hasConfiguredPreferenceOrder;

                // When a recognized capability hint maps to a profile dimension, sort eligible
                // models descending by their score for that dimension before the preference-order
                // step. Ties are broken by the configured within-tier preference order;
                // unprofiled models sort last. Because only idle captains are in eligibleModels,
                // a busy best-fit captain is simply absent and the next-best idle captain wins.
                if (resolvedHint != null)
                {
                    string? dimension = null;
                    if (TryGetCapabilityDimension(settings.CapabilityHintDimensionMap, resolvedHint, out dimension)
                        && !String.IsNullOrWhiteSpace(dimension))
                    {
                        List<string> scored = SortByCapabilityScore(selectionPool, dimension, settings.ModelCapabilityProfiles, preferenceOrder);

                        // Equal models are equal: when several models tie for the top score and
                        // share an identical capability profile, pick randomly among them instead
                        // of letting the enumeration order decide.
                        List<string> topTied = new List<string>();
                        int topScore = GetModelDimensionScore(scored[0], dimension, settings.ModelCapabilityProfiles);
                        foreach (string model in scored)
                        {
                            if (GetModelDimensionScore(model, dimension, settings.ModelCapabilityProfiles) != topScore)
                                break;
                            topTied.Add(model);
                        }

                        if (topTied.Count > 1 && ShareIdenticalProfile(topTied, settings.ModelCapabilityProfiles))
                            return topTied[randomPick(topTied.Count)];

                        return scored[0];
                    }
                }

                if (hasPreferenceOrder && preferenceOrder != null && preferenceOrder.Count > 0)
                {
                    // Preference RANK dominates the native/external split: rank across ALL
                    // eligible models (native and external alike), not the external-first
                    // selectionPool, so a higher-ranked model wins even when it is native and a
                    // lower-ranked peer is external-provider served. Ranked models are those the
                    // operator listed that also have an eligible captain, taken in listed order;
                    // models absent from the list are deliberately unranked peers and are handled
                    // by the external-first / random fallback below.
                    List<string> rankedEligible = new List<string>();
                    foreach (string preferred in preferenceOrder)
                    {
                        if (String.IsNullOrWhiteSpace(preferred))
                            continue;
                        foreach (string eligible in eligibleModels)
                        {
                            if (String.Equals(eligible, preferred, StringComparison.OrdinalIgnoreCase)
                                && !ContainsModel(rankedEligible, eligible))
                                rankedEligible.Add(eligible);
                        }
                    }
                    if (rankedEligible.Count > 0)
                        return rankedEligible[0];

                    // No ranked model has an eligible captain. The remaining models are unranked
                    // peers; keep the non-native-first tiebreak (external pool wins when present)
                    // and pick randomly among them rather than favoring the first enumerated one.
                    if (selectionPool.Count > 0)
                        return selectionPool[randomPick(selectionPool.Count)];

                    continue;
                }

                int idx = randomPick(selectionPool.Count);
                return selectionPool[idx];
            }

            return null;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Returns true when a captain is external-provider served: it carries its own
        /// base URL and runs on a non-OpenCode runtime. OpenCode-runtime captains are
        /// treated as native, whatever their model or overlay.
        /// </summary>
        /// <param name="captain">Captain to classify.</param>
        /// <returns>True when the captain should win the non-native preference.</returns>
        private static bool IsExternalServedCaptain(Captain captain)
        {
            if (captain == null) return false;
            if (captain.Runtime == Armada.Core.Enums.AgentRuntimeEnum.OpenCode) return false;
            return !String.IsNullOrWhiteSpace(captain.ApiBaseUrl);
        }

        /// <summary>
        /// Returns true when at least one idle, persona-eligible captain for the model is
        /// external-provider served. Drives the non-native-first model selection.
        /// </summary>
        /// <param name="idleCaptains">All currently idle captains.</param>
        /// <param name="model">Model to test.</param>
        /// <param name="persona">Optional persona the mission requires.</param>
        /// <returns>True when an external captain for the model is idle and eligible.</returns>
        private static bool HasExternalIdleCaptain(IReadOnlyList<Captain> idleCaptains, string model, string? persona)
        {
            foreach (Captain captain in idleCaptains)
            {
                if (captain == null)
                    continue;
                if (!String.Equals(captain.Model, model, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsPersonaEligible(captain, persona))
                    continue;
                if (IsExternalServedCaptain(captain))
                    return true;
            }

            return false;
        }

        private static ModelTierSettings CreateDefaultSettings()
        {
            return new ModelTierSettings();
        }

        /// <summary>
        /// Vanilla path: no membership lists and no family rules, so every idle
        /// persona-eligible captain is an equal peer. Picks one model at random.
        /// </summary>
        private static string? SelectFromUnconfiguredPool(
            IReadOnlyList<Captain> idleCaptains,
            string? persona,
            Func<int, int> randomPick)
        {
            List<string> eligibleModels = new List<string>();
            foreach (Captain captain in idleCaptains)
            {
                if (captain == null || String.IsNullOrEmpty(captain.Model))
                    continue;
                if (!IsPersonaEligible(captain, persona))
                    continue;
                if (!ContainsModel(eligibleModels, captain.Model))
                    eligibleModels.Add(captain.Model);
            }

            if (eligibleModels.Count == 0)
                return null;
            return eligibleModels[randomPick(eligibleModels.Count)];
        }

        private static bool ContainsModel(IReadOnlyList<string> models, string model)
        {
            foreach (string m in models)
            {
                if (String.Equals(m, model, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool TryGetWithinTierPreferenceOrder(
            string tier,
            IReadOnlyDictionary<string, List<string>>? withinTierPreferenceOrder,
            out IReadOnlyList<string>? order)
        {
            order = null;
            if (withinTierPreferenceOrder == null)
                return false;

            if (withinTierPreferenceOrder.TryGetValue(tier, out List<string>? direct))
            {
                order = direct;
                return true;
            }

            foreach (KeyValuePair<string, List<string>> kvp in withinTierPreferenceOrder)
            {
                if (String.Equals(kvp.Key, tier, StringComparison.OrdinalIgnoreCase))
                {
                    order = kvp.Value;
                    return true;
                }
            }

            return false;
        }

        private static List<string> OrderModelsByPreference(List<string> eligibleModels, IReadOnlyList<string> preferenceOrder)
        {
            List<string> ordered = new List<string>();
            HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string preferred in preferenceOrder)
            {
                if (String.IsNullOrWhiteSpace(preferred))
                    continue;

                foreach (string eligible in eligibleModels)
                {
                    if (!String.Equals(eligible, preferred, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (added.Contains(eligible))
                        continue;

                    ordered.Add(eligible);
                    added.Add(eligible);
                }
            }

            foreach (string eligible in eligibleModels)
            {
                if (added.Contains(eligible))
                    continue;

                ordered.Add(eligible);
                added.Add(eligible);
            }

            return ordered;
        }

        /// <summary>
        /// Returns true when every listed model carries a capability profile and all of them
        /// are identical across every dimension. Equal models are interchangeable, so the
        /// selector may randomize among them rather than letting enumeration order decide.
        /// </summary>
        /// <param name="models">Models to compare.</param>
        /// <param name="profiles">Capability profiles keyed by model name.</param>
        /// <returns>True when all models share one identical profile.</returns>
        private static bool ShareIdenticalProfile(List<string> models, Dictionary<string, ModelCapabilityProfile> profiles)
        {
            if (models == null || models.Count < 2)
                return false;

            ModelCapabilityProfile? baseline = null;
            foreach (string model in models)
            {
                if (!profiles.TryGetValue(model, out ModelCapabilityProfile? profile))
                    return false;

                if (baseline == null)
                {
                    baseline = profile;
                    continue;
                }

                if (baseline.TelemetryRichness != profile.TelemetryRichness ||
                    baseline.AuditReasoningFit != profile.AuditReasoningFit ||
                    baseline.MechanicalThroughput != profile.MechanicalThroughput ||
                    baseline.Cost != profile.Cost)
                {
                    return false;
                }
            }

            return true;
        }

        private static string? ClassifyByFamilyRules(string model, IReadOnlyList<ModelFamilyClassificationRule> rules)
        {
            if (rules == null || rules.Count == 0)
                return null;

            foreach (ModelFamilyClassificationRule rule in rules)
            {
                if (rule == null || String.IsNullOrWhiteSpace(rule.Pattern) || String.IsNullOrWhiteSpace(rule.Tier))
                    continue;

                try
                {
                    if (!Regex.IsMatch(model, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        continue;
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (String.Equals(rule.Tier, HighTier, StringComparison.OrdinalIgnoreCase))
                    return HighTier;
                if (String.Equals(rule.Tier, MidTier, StringComparison.OrdinalIgnoreCase)
                    || String.Equals(rule.Tier, LowTier, StringComparison.OrdinalIgnoreCase))
                    return MidTier;
            }

            return null;
        }

        private static bool IsSpecialistPersona(string? persona, IReadOnlyCollection<string>? specialistPersonas)
        {
            if (String.IsNullOrWhiteSpace(persona)) return false;
            if (specialistPersonas == null) return false;
            foreach (string specialist in specialistPersonas)
            {
                if (String.Equals(specialist, persona, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Single source of truth for the ordered tiers a mission is willing to land on.
        // Specialists are reserved for high only. Non-specialists try their requested tier,
        // then the OTHER non-high tier, and reach high only as a last resort -- this is what
        // keeps high-tier captains free while mid/low captains are idle. A non-specialist that
        // explicitly requests high is honored without downgrading (the operator asked for it).
        private static string[] BuildTierOrder(bool isSpecialist, string normalizedTier)
        {
            if (isSpecialist)
                return new string[] { HighTier };
            if (normalizedTier == LowTier)
                return new string[] { MidTier, HighTier };
            if (normalizedTier == MidTier)
                return new string[] { MidTier, HighTier };
            return new string[] { HighTier };
        }

        private static int TierRank(string tier)
        {
            if (String.Equals(tier, LowTier, StringComparison.OrdinalIgnoreCase)) return 0;
            if (String.Equals(tier, MidTier, StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        private static bool IsPersonaEligible(Captain captain, string? persona)
        {
            if (String.IsNullOrEmpty(persona)) return true;
            if (String.IsNullOrEmpty(captain.AllowedPersonas)) return true;
            return captain.AllowedPersonas.Contains("\"" + persona + "\"", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetCapabilityDimension(
            Dictionary<string, string> dimensionMap,
            string hint,
            out string? dimension)
        {
            dimension = null;
            if (dimensionMap.TryGetValue(hint, out string? direct))
            {
                dimension = direct;
                return !String.IsNullOrWhiteSpace(dimension);
            }

            foreach (KeyValuePair<string, string> kvp in dimensionMap)
            {
                if (String.Equals(kvp.Key, hint, StringComparison.OrdinalIgnoreCase))
                {
                    dimension = kvp.Value;
                    return !String.IsNullOrWhiteSpace(dimension);
                }
            }

            return false;
        }

        private static List<string> SortByCapabilityScore(
            List<string> models,
            string dimension,
            Dictionary<string, ModelCapabilityProfile> profiles,
            IReadOnlyList<string>? tieBreakPreference)
        {
            // Establish tiebreak ordering via the configured preference order first,
            // so equal-score models preserve the operator-defined preference.
            List<string> ordered;
            if (tieBreakPreference != null && tieBreakPreference.Count > 0)
                ordered = OrderModelsByPreference(models, tieBreakPreference);
            else
                ordered = new List<string>(models);

            // Stable descending insertion sort by dimension score. Models with no profile
            // entry (score -1) sort last. Equal-score models preserve their tiebreak position.
            for (int i = 1; i < ordered.Count; i++)
            {
                string current = ordered[i];
                int currentScore = GetModelDimensionScore(current, dimension, profiles);
                int j = i - 1;
                while (j >= 0 && GetModelDimensionScore(ordered[j], dimension, profiles) < currentScore)
                {
                    ordered[j + 1] = ordered[j];
                    j--;
                }
                ordered[j + 1] = current;
            }

            return ordered;
        }

        private static int GetModelDimensionScore(string model, string dimension, Dictionary<string, ModelCapabilityProfile> profiles)
        {
            ModelCapabilityProfile? profile = null;
            profiles.TryGetValue(model, out profile);
            if (profile == null)
            {
                foreach (KeyValuePair<string, ModelCapabilityProfile> kvp in profiles)
                {
                    if (String.Equals(kvp.Key, model, StringComparison.OrdinalIgnoreCase))
                    {
                        profile = kvp.Value;
                        break;
                    }
                }
            }

            if (profile == null) return -1;
            return profile.GetDimensionScore(dimension);
        }

        #endregion
    }
}
