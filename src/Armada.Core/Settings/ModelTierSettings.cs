namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Armada.Core.Services;

    /// <summary>
    /// Settings that govern tier routing policy. A captain's tier, its preference rank within the tier,
    /// and a persona's specialist flag live on the captain and persona records, not here. These settings
    /// hold only fleet-wide policy: the reserved Premium slots, the non-native preference, capability
    /// profiles, and usage routing. Product defaults are policy-neutral.
    /// </summary>
    public class ModelTierSettings
    {
        #region Public-Members

        /// <summary>Account usage conservation and persona preference routes. Disabled by default.</summary>
        public UsageRoutingSettings UsageRouting
        {
            get => _UsageRouting;
            set
            {
                UsageRoutingSettings next = value ?? new UsageRoutingSettings();
                Armada.Core.Services.UsageRoutingService.Validate(next);
                _UsageRouting = next;
            }
        }


        /// <summary>
        /// Retired within-tier strategy value that ranked models by the retired preference order. Read only
        /// by the one-time tier record migration.
        /// </summary>
        public const string WithinTierStrategyPreferenceOrderThenRandom = "PreferenceOrderThenRandom";

        /// <summary>
        /// Number of idle Premium captain slots to hold in reserve for specialist
        /// (downstream) missions such as Judge and TestEngineer. When the only idle
        /// capacity left after specialist dispatch is Premium and at or below this
        /// count, non-specialist (Worker) dispatch is deferred for one scheduler cycle
        /// so the held-back slots stay free for the next incoming review/landing stage.
        /// Workers always prefer Standard captains, so this never withholds non-Premium
        /// capacity. The reservation is suppressed while no in-flight work could produce
        /// a downstream specialist stage, so a Premium-only fleet never deadlocks (a
        /// Worker primes the pipeline before the reserve engages). Clamped to [0, 10].
        /// Zero disables the reservation. Default is 0.
        /// </summary>
        public int ReservedHighTierSlots
        {
            get => _ReservedHighTierSlots;
            set => _ReservedHighTierSlots = Math.Max(0, Math.Min(10, value));
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
        /// When true, within one tier and preference rank a captain served by an external provider
        /// (own apiBaseUrl, non-OpenCode runtime) is tried before a native captain. OpenCode-runtime
        /// captains count as native. Default false: equal-rank captains are equal random peers.
        /// </summary>
        public bool PreferNonNativeFirst { get; set; } = false;

        /// <summary>
        /// When the one-time tier record migration moved the retired tier keys onto captain and persona
        /// records. Null until it has run. While set, the retired keys are ignored and never migrated again.
        /// </summary>
        public DateTime? TierRecordsMigratedUtc { get; set; } = null;

        /// <summary>Retired: mid-tier model names. Read only by the one-time tier record migration.</summary>
        [JsonPropertyName("midTierModels")]
        public List<string>? RetiredMidTierModels { get; set; } = null;

        /// <summary>Retired: high-tier model names. Read only by the one-time tier record migration.</summary>
        [JsonPropertyName("highTierModels")]
        public List<string>? RetiredHighTierModels { get; set; } = null;

        /// <summary>Retired: model-family classification rules. Read only by the one-time tier record migration.</summary>
        [JsonPropertyName("familyClassificationRules")]
        public List<ModelFamilyClassificationRule>? RetiredFamilyClassificationRules { get; set; } = null;

        /// <summary>Retired: specialist persona names. Read only by the one-time tier record migration.</summary>
        [JsonPropertyName("specialistPersonas")]
        public List<string>? RetiredSpecialistPersonas { get; set; } = null;

        /// <summary>Retired: within-tier strategy. Read only by the one-time tier record migration.</summary>
        [JsonPropertyName("withinTierStrategy")]
        public string? RetiredWithinTierStrategy { get; set; } = null;

        /// <summary>Retired: per-tier model preference order. Read only by the one-time tier record migration.</summary>
        [JsonPropertyName("withinTierPreferenceOrder")]
        public Dictionary<string, List<string>>? RetiredWithinTierPreferenceOrder { get; set; } = null;

        /// <summary>
        /// True when the settings carry any retired tier key.
        /// </summary>
        [JsonIgnore]
        public bool HasRetiredTierKeys
        {
            get
            {
                return RetiredMidTierModels != null
                    || RetiredHighTierModels != null
                    || RetiredFamilyClassificationRules != null
                    || RetiredSpecialistPersonas != null
                    || RetiredWithinTierStrategy != null
                    || RetiredWithinTierPreferenceOrder != null;
            }
        }

        /// <summary>
        /// The routing facts read from captain and persona records: which personas are specialists and the
        /// tier of each model the roster runs. Refreshed from the database; never serialized.
        /// </summary>
        [JsonIgnore]
        public TierRoutingRecords Records
        {
            get => _Records;
            set => _Records = value ?? TierRoutingRecords.Empty;
        }

        /// <summary>
        /// Names of the personas flagged as specialists on their records.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyCollection<string> SpecialistPersonas => _Records.SpecialistPersonas;

        #endregion

        #region Private-Members

        private UsageRoutingSettings _UsageRouting = new UsageRoutingSettings();
        private int _ReservedHighTierSlots = 0;
        private Dictionary<string, ModelCapabilityProfile> _ModelCapabilityProfiles = new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _CapabilityHintDimensionMap = BuildDefaultCapabilityHintDimensionMap();
        private TierRoutingRecords _Records = TierRoutingRecords.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with policy-neutral defaults: no reserved slots, no non-native preference, no
        /// retired tier keys, and an empty record snapshot.
        /// </summary>
        public ModelTierSettings()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns true when the persona is flagged as a specialist on its record. Matching is
        /// case-insensitive and uses the canonical persona spelling. Null, empty, or whitespace personas
        /// return false.
        /// </summary>
        /// <param name="persona">Persona name to test.</param>
        public bool IsSpecialistPersona(string? persona)
        {
            return _Records.IsSpecialist(persona);
        }

        /// <summary>
        /// Remove every retired tier key, so a later save no longer writes them.
        /// </summary>
        public void ClearRetiredTierKeys()
        {
            RetiredMidTierModels = null;
            RetiredHighTierModels = null;
            RetiredFamilyClassificationRules = null;
            RetiredSpecialistPersonas = null;
            RetiredWithinTierStrategy = null;
            RetiredWithinTierPreferenceOrder = null;
        }

        /// <summary>
        /// Copy every value from another instance into this one, in place.
        /// Consumers may hold this object by reference, so an update must mutate the
        /// existing instance rather than replace it. Collections are taken from the
        /// source as-is, which is the same handoff the startup deserialization path
        /// performs. A null member on the source restores that member's built-in
        /// default, matching the individual property setters. The record-derived routing
        /// facts are not settings and are kept.
        /// </summary>
        /// <param name="source">Instance to copy values from. Null is ignored.</param>
        public void CopyFrom(ModelTierSettings source)
        {
            if (source == null) return;
            UsageRouting = source.UsageRouting;
            ReservedHighTierSlots = source.ReservedHighTierSlots;
            ModelCapabilityProfiles = source.ModelCapabilityProfiles;
            CapabilityHintDimensionMap = source.CapabilityHintDimensionMap;
            PreferNonNativeFirst = source.PreferNonNativeFirst;
            TierRecordsMigratedUtc = source.TierRecordsMigratedUtc;
            RetiredMidTierModels = source.RetiredMidTierModels;
            RetiredHighTierModels = source.RetiredHighTierModels;
            RetiredFamilyClassificationRules = source.RetiredFamilyClassificationRules;
            RetiredSpecialistPersonas = source.RetiredSpecialistPersonas;
            RetiredWithinTierStrategy = source.RetiredWithinTierStrategy;
            RetiredWithinTierPreferenceOrder = source.RetiredWithinTierPreferenceOrder;
        }

        #endregion

        #region Private-Methods

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
