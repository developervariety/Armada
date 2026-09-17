namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;

    /// <summary>
    /// Settings for the typed-decision system (TypeSafe Jev). The system is Off until a provider key
    /// resolves (the environment variable named by <see cref="ApiKeyEnv"/>, or the key file in the data
    /// directory). With a key, every decision ships in Gate; the stored global <see cref="Mode"/> is a cap
    /// and kill switch, each decision has its own mode, and the effective mode is the minimum of the two.
    /// </summary>
    public class TypedDecisionSettings
    {
        /// <summary>
        /// Stored global operating mode. A cap over every decision and the single kill switch. Ships as
        /// <see cref="TypedDecisionModeEnum.Gate"/>; it applies only while a key resolves, and
        /// <see cref="EffectiveMode"/> is Off otherwise.
        /// </summary>
        public TypedDecisionModeEnum Mode { get; set; } = TypedDecisionModeEnum.Gate;

        /// <summary>
        /// Base URL of the typed-decision provider. The client POSTs to <c>{BaseUrl}/v1/systemone</c>.
        /// </summary>
        public string BaseUrl { get; set; } = "https://api.typesafe.ai";

        /// <summary>
        /// Model identifier sent with each request.
        /// </summary>
        public string Model { get; set; } = "jev-latest";

        /// <summary>
        /// Name of the environment variable holding the Bearer key. When it is not set, the key file
        /// <c>&lt;data directory&gt;/secrets/typesafe-api-key</c> is read. The key is never stored in settings.
        /// </summary>
        public string ApiKeyEnv { get; set; } = "ARMADA_TYPESAFE_KEY";

        /// <summary>
        /// Per-request timeout in seconds. A decision slower than this is unavailable, not late.
        /// </summary>
        public int TimeoutSeconds
        {
            get => _TimeoutSeconds;
            set => _TimeoutSeconds = Math.Max(1, Math.Min(120, value));
        }

        /// <summary>
        /// Maximum characters of decision state transmitted after redaction. State longer than
        /// this is head/tail truncated.
        /// </summary>
        public int MaxStateChars
        {
            get => _MaxStateChars;
            set => _MaxStateChars = Math.Max(256, value);
        }

        /// <summary>
        /// Per-decision-point configuration keyed by decision name. A shipped decision absent from a
        /// supplied map takes its shipped default, so a settings file written before the decision
        /// existed still runs it at its shipped mode; set its mode to Off to switch it off. An unknown
        /// name (for example a retired decision) is kept and never consulted.
        /// </summary>
        public Dictionary<string, TypedDecisionRuleSettings> Decisions
        {
            get => _Decisions;
            set => _Decisions = WithShippedDefaults(value);
        }

        /// <summary>
        /// Whether a model version the provider reports for the first time starts a run of the synthetic
        /// evaluation set in the background. Default true.
        /// </summary>
        public bool EvalOnModelChange { get; set; } = true;

        /// <summary>
        /// Configuration for the captain-facing typed-decision tool.
        /// </summary>
        public TypedDecisionCaptainToolSettings CaptainTool
        {
            get => _CaptainTool;
            set => _CaptainTool = value ?? new TypedDecisionCaptainToolSettings();
        }

        private int _TimeoutSeconds = 10;
        private int _MaxStateChars = 8000;
        private Dictionary<string, TypedDecisionRuleSettings> _Decisions = DefaultDecisions();
        private TypedDecisionCaptainToolSettings _CaptainTool = new TypedDecisionCaptainToolSettings();

        /// <summary>
        /// Reports whether a provider key resolves. Set by the Admiral; never serialized. Null means the
        /// caller does not gate on a key and the stored mode applies.
        /// </summary>
        [JsonIgnore]
        public Func<bool>? KeyAvailable { get; set; }

        /// <summary>
        /// The global mode in effect: Off while no key resolves, otherwise the stored <see cref="Mode"/>.
        /// </summary>
        [JsonIgnore]
        public TypedDecisionModeEnum EffectiveMode
        {
            get
            {
                Func<bool>? keyAvailable = KeyAvailable;
                return keyAvailable != null && !keyAvailable() ? TypedDecisionModeEnum.Off : Mode;
            }
        }

        /// <summary>The shipped decision names, in catalogue order.</summary>
        public static IReadOnlyList<string> ShippedDecisionNames { get; } = DefaultDecisions().Keys.ToList();

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public TypedDecisionSettings()
        {
        }

        /// <summary>
        /// Resolve the effective configuration for a decision point. The effective mode is the
        /// minimum of the global mode and the decision's own mode (Off &lt; Gate), so the global
        /// cap can never be exceeded by a per-decision setting. An unknown decision is Off.
        /// </summary>
        /// <param name="decisionPoint">Decision-point name.</param>
        /// <returns>The resolved effective mode and gate threshold; never null.</returns>
        public ResolvedTypedDecision For(string decisionPoint)
        {
            if (String.IsNullOrWhiteSpace(decisionPoint) || !_Decisions.TryGetValue(decisionPoint, out TypedDecisionRuleSettings? rule) || rule == null)
                return new ResolvedTypedDecision(TypedDecisionModeEnum.Off, 0.0);

            TypedDecisionModeEnum global = EffectiveMode;
            TypedDecisionModeEnum effective = global < rule.Mode ? global : rule.Mode;
            return new ResolvedTypedDecision(effective, rule.GateThreshold);
        }

        /// <summary>
        /// Copy every stored value from another instance into this one, in place, so decision points that
        /// hold this instance see a hot reload. <see cref="KeyAvailable"/> is kept.
        /// </summary>
        /// <param name="source">Instance to copy from. Null is ignored.</param>
        public void CopyFrom(TypedDecisionSettings source)
        {
            if (source == null || ReferenceEquals(source, this)) return;
            Mode = source.Mode;
            BaseUrl = source.BaseUrl;
            Model = source.Model;
            ApiKeyEnv = source.ApiKeyEnv;
            TimeoutSeconds = source.TimeoutSeconds;
            MaxStateChars = source.MaxStateChars;
            Decisions = source.Decisions;
            CaptainTool = source.CaptainTool;
        }

        private static Dictionary<string, TypedDecisionRuleSettings> WithShippedDefaults(Dictionary<string, TypedDecisionRuleSettings>? supplied)
        {
            Dictionary<string, TypedDecisionRuleSettings> merged = new Dictionary<string, TypedDecisionRuleSettings>(StringComparer.Ordinal);
            if (supplied != null)
                foreach (KeyValuePair<string, TypedDecisionRuleSettings> pair in supplied)
                    if (!String.IsNullOrWhiteSpace(pair.Key)) merged[pair.Key] = pair.Value ?? new TypedDecisionRuleSettings();
            foreach (KeyValuePair<string, TypedDecisionRuleSettings> pair in DefaultDecisions())
                if (!merged.ContainsKey(pair.Key)) merged[pair.Key] = pair.Value;
            return merged;
        }

        /// <summary>
        /// The default decision map. Every decision ships in Gate at its threshold (0.90 unless its
        /// design states otherwise) and records the rule's verdict and the model's on every call, so a
        /// post-gate review can move a threshold or switch a decision off. The per-decision mode and the
        /// global cap stop one decision, or all of them, without a deploy.
        /// </summary>
        /// <returns>The default per-decision configuration.</returns>
        private static Dictionary<string, TypedDecisionRuleSettings> DefaultDecisions()
        {
            return new Dictionary<string, TypedDecisionRuleSettings>(StringComparer.Ordinal)
            {
                ["failure_cause"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["change_quality"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["refusal"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["runtime_failure"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["review_substance"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.85 },
                ["preflight"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.80 },
                ["papercut_merge"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["leak_hunk"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["log_watch"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["premise_check"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["criteria_lint"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["inbox_triage"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["followup_routing"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["owner_digest"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["corpus_prelabel"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["flake_score"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["capacity_escalation"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["change_substance"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["memory_candidate"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["stage_necessity"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["handoff_outcome"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["revision_kind"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["test_covers"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["memory_record"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["memory_review"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["lint_finding"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                ["prior_art"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 }
            };
        }
    }

    /// <summary>
    /// Per-decision-point configuration. Off by default with no threshold.
    /// </summary>
    public class TypedDecisionRuleSettings
    {
        /// <summary>
        /// The decision's own mode. Capped by the global mode. Default Off.
        /// </summary>
        public TypedDecisionModeEnum Mode { get; set; } = TypedDecisionModeEnum.Off;

        /// <summary>
        /// Confidence at or above which the model may gate this decision. Below it, the rule
        /// stands. Zero means unset (the decision has no numeric gate).
        /// </summary>
        public double GateThreshold
        {
            get => _GateThreshold;
            set => _GateThreshold = Math.Max(0.0, Math.Min(1.0, value));
        }

        private double _GateThreshold = 0.0;
    }

    /// <summary>
    /// Configuration for the captain-facing typed-decision tool. Enabled by default.
    /// </summary>
    public class TypedDecisionCaptainToolSettings
    {
        /// <summary>
        /// Whether captains may call the typed-decision tool. Default true; the tool stays informative,
        /// mission-scoped, and redacted. There is no per-mission call cap.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Maximum characters of state a captain call may transmit after redaction.
        /// </summary>
        public int MaxStateChars
        {
            get => _MaxStateChars;
            set => _MaxStateChars = Math.Max(256, value);
        }

        private int _MaxStateChars = 8000;
    }

    /// <summary>
    /// The effective typed-decision configuration for one decision point after the global cap is
    /// applied. Immutable.
    /// </summary>
    /// <param name="Mode">The effective mode (minimum of global and decision mode).</param>
    /// <param name="GateThreshold">The decision's gate threshold.</param>
    public sealed record ResolvedTypedDecision(TypedDecisionModeEnum Mode, double GateThreshold);
}
