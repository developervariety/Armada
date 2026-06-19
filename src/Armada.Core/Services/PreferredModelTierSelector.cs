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
    /// the LowTierModels, MidTierModels, and HighTierModels lists determine which
    /// concrete models belong to each tier. When no settings are supplied, a fresh
    /// built-in default set is used so existing call sites keep working.
    /// </summary>
    public static class PreferredModelTierSelector
    {
        #region Public-Members

        /// <summary>Canonical tier name for low-complexity work.</summary>
        public const string LowTier = "low";

        /// <summary>Canonical tier name for mid-complexity work.</summary>
        public const string MidTier = "mid";

        /// <summary>Canonical tier name for high-complexity work.</summary>
        public const string HighTier = "high";

        #endregion

        #region Private-Members

        // Canonical model-family patterns. These let routine version bumps within a known
        // family (e.g. claude-opus-4-7 -> claude-opus-4-8 -> claude-opus-5) classify into
        // the correct tier WITHOUT editing the configured membership lists, which is the
        // whole point of tier selectors. Patterns are deliberately anchored to the canonical
        // vendor naming so alias/preview variants (claude-4.6-opus-high-preview,
        // gemini-3.1-pro-preview) do NOT leak in -- those must be listed explicitly in the
        // configured membership list to count.
        private static readonly Regex _CanonicalOpusPattern =
            new Regex(@"^claude-opus-\d+(?:-\d+)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _CanonicalSonnetPattern =
            new Regex(@"^claude-sonnet-\d+(?:-\d+)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Anthropic's most-capable widely-released family (Fable) and its Project Glasswing
        // sibling (Mythos) are top-tier -> high. Anchored to canonical naming so version bumps
        // (claude-fable-5 -> claude-fable-6) register automatically, like the opus pattern.
        private static readonly Regex _CanonicalFablePattern =
            new Regex(@"^claude-(?:fable|mythos)-\d+(?:-\d+)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _GeminiProPattern =
            new Regex(@"^gemini-[\d.]+-pro$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Kimi K2.7 is explicitly mid-tier. This pattern anchors the version so earlier
        // Kimi releases (K2.5, K2.6) stay low and future K2.7 aliases classify mid without
        // requiring each slash-prefixed variant to be added by hand.
        private static readonly Regex _CanonicalKimiK27Pattern =
            new Regex(@"^(?:opencode(?:-go)?/)?kimi-k2\.7(?:[-.].*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, string> _Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "quick", LowTier },
            { "medium", MidTier }
        };

        private static readonly HashSet<string> _TierNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LowTier, MidTier, HighTier, "quick", "medium"
        };

        // Personas that can only be filled by high-tier captains. Mid- and low-tier
        // captains carry ["Worker"] allow-lists and would never match these personas
        // anyway, but enforcing the tier at mission-create time keeps the stored
        // PreferredModel honest and surfaces dispatch errors before they hit routing.
        private static readonly HashSet<string> _HighTierOnlyPersonas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Judge",
            "Architect",
            "TestEngineer",
            "DiagnosticProtocolReviewer",
            "TenantSecurityReviewer",
            "MigrationDataReviewer",
            "PerformanceMemoryReviewer",
            "PortingReferenceAnalyst",
            "FrontendWorkflowReviewer",
            "MemoryConsolidator"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns true if the value is a recognized tier selector (low, mid, high, or an alias).
        /// Returns false for null, empty, and literal model names.
        /// </summary>
        public static bool IsTierSelector(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            return _TierNames.Contains(value);
        }

        /// <summary>
        /// Returns true when the persona is a specialist reserved for high-tier captains
        /// (Judge, Architect, TestEngineer, specialist reviewers, MemoryConsolidator).
        /// Worker and null personas return false. When <paramref name="specialistPersonas"/>
        /// is null the built-in default specialist set is used, so existing call sites keep
        /// their original behavior; operators can override the set via settings.
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
        /// if no captain matches). When <paramref name="specialistPersonas"/> is null the
        /// built-in default specialist set is used.
        /// </summary>
        /// <param name="preferredModel">Requested tier selector or literal model name.</param>
        /// <param name="persona">Persona the mission requires.</param>
        /// <param name="specialistPersonas">Optional override set; null uses the built-in default.</param>
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
        /// Normalizes a tier selector value to its canonical form (low, mid, or high).
        /// Throws <see cref="ArgumentException"/> if value is not a tier selector.
        /// </summary>
        public static string NormalizeTier(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Tier value must not be null or empty.", nameof(value));

            if (String.Equals(value, LowTier, StringComparison.OrdinalIgnoreCase)) return LowTier;
            if (String.Equals(value, MidTier, StringComparison.OrdinalIgnoreCase)) return MidTier;
            if (String.Equals(value, HighTier, StringComparison.OrdinalIgnoreCase)) return HighTier;

            string? alias = null;
            if (_Aliases.TryGetValue(value, out alias)) return alias;

            throw new ArgumentException("Unknown tier selector: " + value, nameof(value));
        }

        /// <summary>
        /// Returns the model names for the specified tier (low, mid, or high). The list is
        /// sourced from <paramref name="modelTierSettings"/> when supplied, otherwise from
        /// the built-in defaults. The returned collection is read-only.
        /// </summary>
        /// <param name="tier">Tier selector value (low, mid, high, or alias).</param>
        /// <param name="modelTierSettings">Optional tier membership configuration; null uses built-in defaults.</param>
        public static IReadOnlyList<string> GetTierModels(string tier, ModelTierSettings? modelTierSettings = null)
        {
            ModelTierSettings settings = modelTierSettings ?? CreateDefaultSettings();
            string normalized = NormalizeTier(tier);
            if (normalized == LowTier) return settings.LowTierModels.AsReadOnly();
            if (normalized == MidTier) return settings.MidTierModels.AsReadOnly();
            return settings.HighTierModels.AsReadOnly();
        }

        /// <summary>
        /// Returns all model names in the specified tier and every tier above it.
        /// Used to validate whether a pinned captain's model is acceptable for the
        /// requested tier or its upward fallback chain.
        /// </summary>
        /// <param name="tier">Tier selector value (low, mid, high, or alias).</param>
        /// <param name="modelTierSettings">Optional tier membership configuration; null uses built-in defaults.</param>
        public static IReadOnlyList<string> GetTierAndAboveModels(string tier, ModelTierSettings? modelTierSettings = null)
        {
            ModelTierSettings settings = modelTierSettings ?? CreateDefaultSettings();
            string normalized = NormalizeTier(tier);
            List<string> result = new List<string>();
            if (normalized == LowTier)
            {
                result.AddRange(settings.LowTierModels);
                result.AddRange(settings.MidTierModels);
                result.AddRange(settings.HighTierModels);
            }
            else if (normalized == MidTier)
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
        /// Classifies a concrete model name into its complexity tier (low, mid, or high),
        /// or null when the model is not recognized as belonging to any tier. A model counts
        /// when it is in the configured tier membership lists OR matches a canonical
        /// model-family pattern, so version bumps within a known family register
        /// automatically.
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
            // names (e.g. claude-4.6-opus-high) that intentionally do not match a pattern, and
            // for explicit entries such as gpt-5.5.
            if (ContainsModel(settings.HighTierModels, normalized)) return HighTier;
            if (ContainsModel(settings.MidTierModels, normalized)) return MidTier;
            if (ContainsModel(settings.LowTierModels, normalized)) return LowTier;

            // Canonical family patterns -- forward-compatible with version bumps.
            if (_CanonicalOpusPattern.IsMatch(normalized)) return HighTier;
            if (_CanonicalFablePattern.IsMatch(normalized)) return HighTier;
            if (_CanonicalKimiK27Pattern.IsMatch(normalized)) return MidTier;
            if (_CanonicalSonnetPattern.IsMatch(normalized)) return MidTier;
            if (_GeminiProPattern.IsMatch(normalized)) return MidTier;
            if (normalized.StartsWith("composer-", StringComparison.OrdinalIgnoreCase)) return MidTier;
            if (normalized.StartsWith("kimi-", StringComparison.OrdinalIgnoreCase)) return LowTier;

            return null;
        }

        /// <summary>
        /// Returns true when the given concrete model belongs to the requested tier or any
        /// tier above it (low &lt; mid &lt; high). Used to validate a captain's model against a
        /// tier pin while honoring the upward-only fallback chain. Returns false for models
        /// that classify into no tier.
        /// </summary>
        /// <param name="model">Concrete model name (not a tier selector).</param>
        /// <param name="requestedTier">Tier selector value (low, mid, high, or alias).</param>
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
        /// </summary>
        /// <param name="tierValue">Tier selector value (low, mid, high, or alias).</param>
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
            ModelTierSettings? modelTierSettings = null)
        {
            if (randomPick == null)
                throw new ArgumentNullException(nameof(randomPick));

            ModelTierSettings settings = modelTierSettings ?? CreateDefaultSettings();
            string normalized = NormalizeTier(tierValue);
            bool isSpecialist = IsSpecialistPersona(persona, specialistPersonas);
            string[] tierOrder = BuildTierOrder(isSpecialist, normalized);

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
                    if (!String.Equals(ClassifyModel(captain.Model, settings), tier, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!IsPersonaEligible(captain, persona))
                        continue;

                    if (!ContainsModel(eligibleModels, captain.Model))
                        eligibleModels.Add(captain.Model);
                }

                if (eligibleModels.Count == 0)
                    continue;

                if (TryGetWithinTierPreferenceOrder(tier, withinTierPreferenceOrder, out IReadOnlyList<string>? preferenceOrder)
                    && preferenceOrder != null
                    && preferenceOrder.Count > 0)
                {
                    List<string> ordered = OrderModelsByPreference(eligibleModels, preferenceOrder);
                    if (ordered.Count > 0)
                        return ordered[0];

                    continue;
                }

                int idx = randomPick(eligibleModels.Count);
                return eligibleModels[idx];
            }

            return null;
        }

        #endregion

        #region Private-Methods

        private static ModelTierSettings CreateDefaultSettings()
        {
            return new ModelTierSettings();
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

        private static bool IsSpecialistPersona(string? persona, IReadOnlyCollection<string>? specialistPersonas)
        {
            if (String.IsNullOrWhiteSpace(persona)) return false;
            IEnumerable<string> set = specialistPersonas ?? _HighTierOnlyPersonas;
            foreach (string specialist in set)
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
                return new string[] { LowTier, MidTier, HighTier };
            if (normalizedTier == MidTier)
                return new string[] { MidTier, LowTier, HighTier };
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

        #endregion
    }
}
