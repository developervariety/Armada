namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Settings that govern how captain model tiers are reserved across personas.
    /// Product defaults are empty and policy-neutral: a fresh install classifies no
    /// model family, reserves no specialist personas, and selects randomly within a
    /// tier. Operators populate membership lists, family rules, and routing policy
    /// in settings.json or the Dashboard.
    /// </summary>
    public class ModelTierSettings
    {
        #region Public-Members

        /// <summary>
        /// Canonical within-tier strategy: pick uniformly among eligible models in the tier.
        /// </summary>
        public const string WithinTierStrategyRandom = "Random";

        /// <summary>
        /// Within-tier strategy: try the configured preference order first, then pick
        /// randomly among unlisted eligible models.
        /// </summary>
        public const string WithinTierStrategyPreferenceOrderThenRandom = "PreferenceOrderThenRandom";

        /// <summary>
        /// Persona names that are reserved for high-tier captains. A mission whose
        /// persona is in this set is routed only to high-tier captains; all other
        /// personas prefer mid and fall up to high only when no mid captain
        /// is available. Setting this to null restores the empty default set.
        /// </summary>
        public List<string> SpecialistPersonas
        {
            get => _SpecialistPersonas;
            set => _SpecialistPersonas = value ?? new List<string>();
        }

        /// <summary>
        /// Number of idle high-tier captain slots to hold in reserve for specialist
        /// (downstream) missions such as Judge and TestEngineer. When the only idle
        /// capacity left after specialist dispatch is high-tier and at or below this
        /// count, non-specialist (Worker) dispatch is deferred for one scheduler cycle
        /// so the held-back slots stay free for the next incoming review/landing stage.
        /// Workers always prefer mid captains, so this never withholds non-high-tier
        /// capacity. The reservation is suppressed while no in-flight work could produce
        /// a downstream specialist stage, so a high-tier-only fleet never deadlocks (a
        /// Worker primes the pipeline before the reserve engages). Clamped to [0, 10].
        /// Zero disables the reservation. Default is 0.
        /// </summary>
        public int ReservedHighTierSlots
        {
            get => _ReservedHighTierSlots;
            set => _ReservedHighTierSlots = Math.Max(0, Math.Min(10, value));
        }

        /// <summary>
        /// Per-tier within-tier model preference order. Used only when
        /// <see cref="WithinTierStrategy"/> is <see cref="WithinTierStrategyPreferenceOrderThenRandom"/>.
        /// When a tier has idle captains across multiple models, the selector tries each
        /// listed model in order and picks the first one with at least one idle,
        /// persona-eligible captain. Models not listed are considered only after all
        /// listed models are exhausted. A loaded settings file replaces this ranking
        /// outright per tier; setting it to null restores the empty default.
        /// </summary>
        public Dictionary<string, List<string>> WithinTierPreferenceOrder
        {
            get => _WithinTierPreferenceOrder;
            set => _WithinTierPreferenceOrder = value ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Per-model capability profiles keyed by concrete model name. Used by the tier
        /// selector to rank idle captains within a tier by best fit for a capability hint.
        /// Each profile scores the model across TelemetryRichness, AuditReasoningFit,
        /// MechanicalThroughput, and Cost dimensions on a 0-100 scale. Setting this to
        /// null restores the empty default.
        /// </summary>
        public Dictionary<string, ModelCapabilityProfile> ModelCapabilityProfiles
        {
            get => _ModelCapabilityProfiles;
            set => _ModelCapabilityProfiles = value ?? new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Maps capability hint names to the profile dimension they optimize. The selector
        /// looks up the hint here to determine which ModelCapabilityProfile property to
        /// score models by, so operators can remap hints to different dimensions without a
        /// code change. Setting this to null restores the built-in generic mapping.
        /// Built-in defaults: "audit" and "reasoning-heavy" map to AuditReasoningFit;
        /// "mechanical" and "doc-only" map to MechanicalThroughput.
        /// </summary>
        public Dictionary<string, string> CapabilityHintDimensionMap
        {
            get => _CapabilityHintDimensionMap;
            set => _CapabilityHintDimensionMap = value ?? BuildDefaultCapabilityHintDimensionMap();
        }

        /// <summary>
        /// Mid-complexity model names. A captain whose model is in this list is
        /// eligible for mid-tier dispatch and falls up to high only through the
        /// upward fallback chain. Setting this to null restores the empty default.
        /// </summary>
        public List<string> MidTierModels
        {
            get => _MidTierModels;
            set => _MidTierModels = value ?? new List<string>();
        }

        /// <summary>
        /// High-complexity model names. Specialist personas are reserved for captains
        /// whose model is in this list (or matches a configured family classification
        /// rule). Setting this to null restores the empty default.
        /// </summary>
        public List<string> HighTierModels
        {
            get => _HighTierModels;
            set => _HighTierModels = value ?? new List<string>();
        }

        /// <summary>
        /// Model-family classification rules applied when a concrete model is not in
        /// <see cref="MidTierModels"/> or <see cref="HighTierModels"/>. The first
        /// matching pattern wins. Setting this to null restores the empty default.
        /// </summary>
        public List<ModelFamilyClassificationRule> FamilyClassificationRules
        {
            get => _FamilyClassificationRules;
            set => _FamilyClassificationRules = value ?? new List<ModelFamilyClassificationRule>();
        }

        /// <summary>
        /// When true, models with an idle non-native captain (own apiBaseUrl, non-OpenCode
        /// runtime) are selected before native-only models, and within a model the
        /// non-native captain wins the tie. OpenCode-runtime captains count as native.
        /// Default false: every eligible model in the tier is an equal random peer.
        /// </summary>
        public bool PreferNonNativeFirst { get; set; } = false;

        /// <summary>
        /// Within-tier selection strategy. <see cref="WithinTierStrategyRandom"/> (default)
        /// picks uniformly among eligible models. <see cref="WithinTierStrategyPreferenceOrderThenRandom"/>
        /// tries <see cref="WithinTierPreferenceOrder"/> first, then picks randomly among
        /// remaining eligible models. Unrecognized values are treated as Random.
        /// </summary>
        public string WithinTierStrategy
        {
            get => _WithinTierStrategy;
            set => _WithinTierStrategy = NormalizeWithinTierStrategy(value);
        }

        #endregion

        #region Private-Members

        private List<string> _SpecialistPersonas = new List<string>();
        private int _ReservedHighTierSlots = 0;
        private Dictionary<string, List<string>> _WithinTierPreferenceOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, ModelCapabilityProfile> _ModelCapabilityProfiles = new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _CapabilityHintDimensionMap = BuildDefaultCapabilityHintDimensionMap();
        private List<string> _MidTierModels = new List<string>();
        private List<string> _HighTierModels = new List<string>();
        private List<ModelFamilyClassificationRule> _FamilyClassificationRules = new List<ModelFamilyClassificationRule>();
        private string _WithinTierStrategy = WithinTierStrategyRandom;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with empty membership lists, no family rules, and the Random
        /// within-tier strategy.
        /// </summary>
        public ModelTierSettings()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns true when the supplied persona is reserved for high-tier captains.
        /// Matching is case-insensitive. Null, empty, or whitespace personas return false.
        /// </summary>
        /// <param name="persona">Persona name to test.</param>
        public bool IsSpecialistPersona(string? persona)
        {
            if (String.IsNullOrWhiteSpace(persona)) return false;
            foreach (string specialist in _SpecialistPersonas)
            {
                if (String.Equals(specialist, persona, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when any tier-membership list or family-classification rule is
        /// configured. When false, the selector treats every idle persona-eligible
        /// captain as a peer (vanilla dispatch: no family or list assumption).
        /// </summary>
        public bool HasConfiguredTierMembership
        {
            get
            {
                return _MidTierModels.Count > 0
                    || _HighTierModels.Count > 0
                    || _FamilyClassificationRules.Count > 0;
            }
        }

        /// <summary>
        /// Returns true when <see cref="WithinTierStrategy"/> is the preference-order
        /// then random strategy.
        /// </summary>
        public bool UsesPreferenceOrderThenRandom()
        {
            return String.Equals(_WithinTierStrategy, WithinTierStrategyPreferenceOrderThenRandom, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Copy every value from another instance into this one, in place.
        /// Consumers may hold this object by reference, so an update must mutate the
        /// existing instance rather than replace it. Collections are taken from the
        /// source as-is, which is the same handoff the startup deserialization path
        /// performs. A null member on the source restores that member's built-in
        /// default, matching the individual property setters.
        /// </summary>
        /// <param name="source">Instance to copy values from. Null is ignored.</param>
        public void CopyFrom(ModelTierSettings source)
        {
            if (source == null) return;
            SpecialistPersonas = source.SpecialistPersonas;
            ReservedHighTierSlots = source.ReservedHighTierSlots;
            WithinTierPreferenceOrder = source.WithinTierPreferenceOrder;
            ModelCapabilityProfiles = source.ModelCapabilityProfiles;
            CapabilityHintDimensionMap = source.CapabilityHintDimensionMap;
            MidTierModels = source.MidTierModels;
            HighTierModels = source.HighTierModels;
            FamilyClassificationRules = source.FamilyClassificationRules;
            PreferNonNativeFirst = source.PreferNonNativeFirst;
            WithinTierStrategy = source.WithinTierStrategy;
        }

        #endregion

        #region Private-Methods

        private static string NormalizeWithinTierStrategy(string? value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return WithinTierStrategyRandom;
            if (String.Equals(value, WithinTierStrategyPreferenceOrderThenRandom, StringComparison.OrdinalIgnoreCase))
                return WithinTierStrategyPreferenceOrderThenRandom;
            return WithinTierStrategyRandom;
        }

        private static Dictionary<string, string> BuildDefaultCapabilityHintDimensionMap()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "audit", "AuditReasoningFit" },
                { "reasoning-heavy", "AuditReasoningFit" },
                { "mechanical", "MechanicalThroughput" },
                { "doc-only", "MechanicalThroughput" }
            };
        }

        #endregion
    }
}
