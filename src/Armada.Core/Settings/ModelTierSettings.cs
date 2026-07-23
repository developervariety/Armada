namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Settings that govern how captain model tiers are reserved across personas.
    /// Specialist personas (reviewers, Judge, Architect, etc.) are reserved for
    /// high-tier captains; non-specialist work runs on mid/low tiers with high held
    /// back as a last resort. The specialist set and tier membership lists are
    /// configurable so operators can reclassify a persona or move a model between
    /// tiers without a code change.
    /// </summary>
    public class ModelTierSettings
    {
        #region Public-Members

        /// <summary>
        /// Persona names that are reserved for high-tier captains. A mission whose
        /// persona is in this set is routed only to high-tier captains; all other
        /// personas prefer mid/low and fall up to high only when no mid/low captain
        /// is available. Setting this to null restores the built-in default set.
        /// </summary>
        public List<string> SpecialistPersonas
        {
            get => _SpecialistPersonas;
            set => _SpecialistPersonas = value ?? BuildDefaultSpecialistPersonas();
        }

        /// <summary>
        /// Number of idle high-tier captain slots to hold in reserve for specialist
        /// (downstream) missions such as Judge and TestEngineer. When the only idle
        /// capacity left after specialist dispatch is high-tier and at or below this
        /// count, non-specialist (Worker) dispatch is deferred for one scheduler cycle
        /// so the held-back slots stay free for the next incoming review/landing stage.
        /// Workers always prefer mid/low captains, so this never withholds non-high-tier
        /// capacity. The reservation is suppressed while no in-flight work could produce
        /// a downstream specialist stage, so a high-tier-only fleet never deadlocks (a
        /// Worker primes the pipeline before the reserve engages). Clamped to [0, 10].
        /// Zero disables the reservation. Default is 1: keeps one opus-class slot free
        /// for the next Judge or TestEngineer that arrives.
        /// </summary>
        public int ReservedHighTierSlots
        {
            get => _ReservedHighTierSlots;
            set => _ReservedHighTierSlots = Math.Max(0, Math.Min(10, value));
        }

        /// <summary>
        /// Per-tier within-tier model preference order. When a tier has idle captains
        /// across multiple models, the selector tries each listed model in order and
        /// picks the first one with at least one idle, persona-eligible captain. Models
        /// not listed are considered only after all listed models are exhausted. Setting
        /// this to null restores the built-in default order (mid tier prefers
        /// opencode-go/kimi-k2.7-code, then claude-sonnet-4-6, then composer-2.5).
        /// </summary>
        public Dictionary<string, List<string>> WithinTierPreferenceOrder
        {
            get => _WithinTierPreferenceOrder;
            set => _WithinTierPreferenceOrder = value ?? BuildDefaultWithinTierPreferenceOrder();
        }

        /// <summary>
        /// Per-model capability profiles keyed by concrete model name. Used by the tier
        /// selector to rank idle captains within a tier by best fit for a capability hint.
        /// Each profile scores the model across TelemetryRichness, AuditReasoningFit,
        /// MechanicalThroughput, and Cost dimensions on a 0-100 scale. Setting this to
        /// null restores the built-in default profiles.
        /// </summary>
        public Dictionary<string, ModelCapabilityProfile> ModelCapabilityProfiles
        {
            get => _ModelCapabilityProfiles;
            set => _ModelCapabilityProfiles = value ?? BuildDefaultModelCapabilityProfiles();
        }

        /// <summary>
        /// Maps capability hint names to the profile dimension they optimize. The selector
        /// looks up the hint here to determine which ModelCapabilityProfile property to
        /// score models by, so operators can remap hints to different dimensions without a
        /// code change. Setting this to null restores the built-in default mapping.
        /// Built-in defaults: "audit" and "reasoning-heavy" map to AuditReasoningFit;
        /// "mechanical" and "doc-only" map to MechanicalThroughput.
        /// </summary>
        public Dictionary<string, string> CapabilityHintDimensionMap
        {
            get => _CapabilityHintDimensionMap;
            set => _CapabilityHintDimensionMap = value ?? BuildDefaultCapabilityHintDimensionMap();
        }

        /// <summary>
        /// Low-complexity model names. A captain whose model is in this list is
        /// eligible for low-tier dispatch and falls up to mid/high only through the
        /// upward fallback chain. Setting this to null restores the built-in default
        /// list.
        /// </summary>
        public List<string> LowTierModels
        {
            get => _LowTierModels;
            set => _LowTierModels = value ?? BuildDefaultLowTierModels();
        }

        /// <summary>
        /// Mid-complexity model names. A captain whose model is in this list is
        /// eligible for mid-tier dispatch and falls up to high only through the
        /// upward fallback chain. Setting this to null restores the built-in default
        /// list.
        /// </summary>
        public List<string> MidTierModels
        {
            get => _MidTierModels;
            set => _MidTierModels = value ?? BuildDefaultMidTierModels();
        }

        /// <summary>
        /// High-complexity model names. Specialist personas are reserved for captains
        /// whose model is in this list (or matches a canonical high-tier family
        /// pattern). Setting this to null restores the built-in default list.
        /// </summary>
        public List<string> HighTierModels
        {
            get => _HighTierModels;
            set => _HighTierModels = value ?? BuildDefaultHighTierModels();
        }

        #endregion

        #region Private-Members

        private List<string> _SpecialistPersonas = BuildDefaultSpecialistPersonas();
        private int _ReservedHighTierSlots = 1;
        private Dictionary<string, List<string>> _WithinTierPreferenceOrder = BuildDefaultWithinTierPreferenceOrder();
        private Dictionary<string, ModelCapabilityProfile> _ModelCapabilityProfiles = BuildDefaultModelCapabilityProfiles();
        private Dictionary<string, string> _CapabilityHintDimensionMap = BuildDefaultCapabilityHintDimensionMap();
        private List<string> _LowTierModels = BuildDefaultLowTierModels();
        private List<string> _MidTierModels = BuildDefaultMidTierModels();
        private List<string> _HighTierModels = BuildDefaultHighTierModels();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with the built-in default specialist persona set and tier
        /// membership lists.
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

        #endregion

        #region Private-Methods

        private static List<string> BuildDefaultSpecialistPersonas()
        {
            return new List<string>
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
        }

        private static Dictionary<string, List<string>> BuildDefaultWithinTierPreferenceOrder()
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "mid",
                    new List<string>
                    {
                        "opencode-go/kimi-k2.7-code",
                        "claude-sonnet-4-6",
                        "composer-2.5"
                    }
                }
            };
        }

        private static List<string> BuildDefaultLowTierModels()
        {
            return new List<string>
            {
                "kimi-k2.5",
                "opencode/kimi-k2.6",
                "opencode-go/kimi-k2.6",
                "opencode/deepseek-v4-flash"
            };
        }

        private static List<string> BuildDefaultMidTierModels()
        {
            return new List<string>
            {
                "composer-2.5",
                "claude-sonnet-4-6",
                "gemini-3.5-pro",
                "gpt-5.3-codex",
                "claude-4.6-sonnet-medium",
                "gemini-3.1-pro",
                "opencode-go/kimi-k2.7-code",
                // Eligible for mid-tier dispatch but deliberately NOT in WithinTierPreferenceOrder:
                // unproven and free-tier (rate-limited), so it is only selected when explicitly
                // forced or when the preferred mid-tier captains are unavailable.
                "opencode/laguna-s-2.1-free"
            };
        }

        private static List<string> BuildDefaultHighTierModels()
        {
            return new List<string>
            {
                "claude-opus-4-7",
                "gpt-5.5",
                "gpt-5.6-sol",
                "claude-4.6-opus-high"
            };
        }

        private static Dictionary<string, ModelCapabilityProfile> BuildDefaultModelCapabilityProfiles()
        {
            return new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase)
            {
                // Low-tier: high mechanical throughput, low telemetry/audit fit, minimal cost.
                { "kimi-k2.5", new ModelCapabilityProfile { TelemetryRichness = 20, AuditReasoningFit = 20, MechanicalThroughput = 80, Cost = 10 } },
                { "opencode/kimi-k2.6", new ModelCapabilityProfile { TelemetryRichness = 20, AuditReasoningFit = 20, MechanicalThroughput = 80, Cost = 10 } },
                { "opencode-go/kimi-k2.6", new ModelCapabilityProfile { TelemetryRichness = 20, AuditReasoningFit = 20, MechanicalThroughput = 80, Cost = 10 } },
                { "opencode/deepseek-v4-flash", new ModelCapabilityProfile { TelemetryRichness = 30, AuditReasoningFit = 30, MechanicalThroughput = 75, Cost = 15 } },
                // Mid-tier: opencode/kimi variants optimized for throughput; claude/gemini for telemetry and reasoning.
                { "opencode-go/kimi-k2.7-code", new ModelCapabilityProfile { TelemetryRichness = 25, AuditReasoningFit = 25, MechanicalThroughput = 85, Cost = 20 } },
                { "claude-sonnet-4-6", new ModelCapabilityProfile { TelemetryRichness = 80, AuditReasoningFit = 80, MechanicalThroughput = 60, Cost = 65 } },
                { "composer-2.5", new ModelCapabilityProfile { TelemetryRichness = 30, AuditReasoningFit = 30, MechanicalThroughput = 80, Cost = 25 } },
                { "gemini-3.5-pro", new ModelCapabilityProfile { TelemetryRichness = 60, AuditReasoningFit = 60, MechanicalThroughput = 60, Cost = 50 } },
                { "gpt-5.3-codex", new ModelCapabilityProfile { TelemetryRichness = 50, AuditReasoningFit = 55, MechanicalThroughput = 70, Cost = 55 } },
                { "claude-4.6-sonnet-medium", new ModelCapabilityProfile { TelemetryRichness = 75, AuditReasoningFit = 75, MechanicalThroughput = 55, Cost = 60 } },
                { "gemini-3.1-pro", new ModelCapabilityProfile { TelemetryRichness = 55, AuditReasoningFit = 55, MechanicalThroughput = 60, Cost = 45 } },
                // PROVISIONAL pending head-to-head evaluation vs claude-sonnet-4-6; free tier, so
                // Cost is near-zero but throughput is rate-limited in practice. Revise once measured.
                { "opencode/laguna-s-2.1-free", new ModelCapabilityProfile { TelemetryRichness = 50, AuditReasoningFit = 50, MechanicalThroughput = 60, Cost = 5 } },
                // High-tier: rich telemetry and strong audit reasoning, higher cost.
                { "claude-opus-4-7", new ModelCapabilityProfile { TelemetryRichness = 95, AuditReasoningFit = 95, MechanicalThroughput = 55, Cost = 95 } },
                { "gpt-5.5", new ModelCapabilityProfile { TelemetryRichness = 85, AuditReasoningFit = 85, MechanicalThroughput = 60, Cost = 90 } },
                { "gpt-5.6-sol", new ModelCapabilityProfile { TelemetryRichness = 85, AuditReasoningFit = 92, MechanicalThroughput = 62, Cost = 90 } },
                { "claude-4.6-opus-high", new ModelCapabilityProfile { TelemetryRichness = 90, AuditReasoningFit = 90, MechanicalThroughput = 55, Cost = 90 } }
            };
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
