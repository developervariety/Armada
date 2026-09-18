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
        /// this is head/tail truncated. Default 60,000: redacted state measures about three bytes per
        /// provider token, so this is about 20,000 tokens, which leaves room for a decision's questions
        /// under the provider's 32,000-token request limit. The request guard in
        /// <see cref="Armada.Core.Services.TypedDecisionBatcher"/> keeps a batch under that limit.
        /// The old 8,000 default truncated the decisions that carry real content (a diff, a log tail,
        /// a Judge narrative) on most of their calls, so they judged a fragment.
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
        /// User-defined custom typed decisions, keyed by name. Operators create and edit these from
        /// the dashboard or the settings API; they hot-reload in place like the built-in decisions.
        /// A custom decision runs only at a generic surface and its binding is a fixed, non-approving
        /// action, so it can never break the safety contract. A name here must not collide with a
        /// shipped decision.
        /// </summary>
        public Dictionary<string, CustomTypedDecisionSettings> Custom
        {
            get => _Custom;
            set => _Custom = value ?? new Dictionary<string, CustomTypedDecisionSettings>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Whether a model version the provider reports for the first time starts a run of the synthetic
        /// evaluation set in the background. Default true.
        /// </summary>
        public bool EvalOnModelChange { get; set; } = true;

        /// <summary>
        /// Retention of redacted decision state on this host as training data. Off by default.
        /// </summary>
        public TypedDecisionRetentionSettings Retention { get; set; } = new TypedDecisionRetentionSettings();

        /// <summary>
        /// Vessels whose content must never leave this host, by vessel id. Every decision about a
        /// mission on one of them keeps its deterministic rule, sends nothing, and records an
        /// unavailable event whose reason is <c>egress_excluded_vessel</c>, so the exclusion is counted
        /// rather than silent. Ships empty: which vessels hold content that may not be sent is an
        /// operator's configuration, never a product default.
        /// </summary>
        public List<string> EgressExcludedVesselIds
        {
            get => _EgressExcludedVesselIds;
            set => _EgressExcludedVesselIds = value ?? new List<string>();
        }

        /// <summary>
        /// Text markers meaning a decision state carries content that must never leave this host, for example
        /// the name of a directory that holds decrypted vendor data. They apply to EVERY egress path - the
        /// adapter skeleton, custom decisions and the captain tools - unless a decision or a custom definition
        /// sets its own list. A state whose UNREDACTED text contains one, compared without case, is not sent:
        /// the rule stands and the decision records <c>egress_excluded_content</c>. Ships empty: which markers
        /// apply is an operator's configuration, never a product default.
        /// </summary>
        public List<string> EgressExcludedMarkers
        {
            get => _EgressExcludedMarkers;
            set => _EgressExcludedMarkers = value ?? new List<string>();
        }

        /// <summary>The markers that apply to a built-in decision: its own list when it sets one, else the global list.</summary>
        /// <param name="decisionPoint">Decision key; a key with no entry (a captain tool) takes the global list.</param>
        /// <returns>The markers.</returns>
        public IReadOnlyList<string> MarkersFor(string decisionPoint)
        {
            if (!String.IsNullOrWhiteSpace(decisionPoint)
                && _Decisions.TryGetValue(decisionPoint, out TypedDecisionRuleSettings? rule)
                && rule?.EgressExcludedMarkers != null)
                return rule.EgressExcludedMarkers;
            return _EgressExcludedMarkers;
        }

        /// <summary>The markers that apply to a custom decision: its own list when it sets one, else the global list.</summary>
        /// <param name="name">Custom decision name.</param>
        /// <returns>The markers.</returns>
        public IReadOnlyList<string> MarkersForCustom(string name)
        {
            if (!String.IsNullOrWhiteSpace(name)
                && _Custom.TryGetValue(name, out CustomTypedDecisionSettings? definition)
                && definition?.EgressExcludedMarkers != null)
                return definition.EgressExcludedMarkers;
            return _EgressExcludedMarkers;
        }

        /// <summary>The first built-in-decision marker found in a text, or null.</summary>
        /// <param name="decisionPoint">Decision key.</param>
        /// <param name="text">Unredacted text about to be considered for egress.</param>
        /// <returns>The matching marker, or null.</returns>
        public string? ExcludedMarkerIn(string decisionPoint, string? text) => FirstMarkerIn(MarkersFor(decisionPoint), text);

        /// <summary>
        /// The one matcher every egress path uses: the first marker the text contains, compared without case,
        /// or null. Unreadable state (see <see cref="Armada.Core.Services.TypedDecisionEgress"/>) counts as a match.
        /// </summary>
        /// <param name="markers">The markers that apply.</param>
        /// <param name="text">Unredacted text.</param>
        /// <returns>The matching marker, or null.</returns>
        public static string? FirstMarkerIn(IReadOnlyList<string> markers, string? text)
        {
            if (markers == null || markers.Count == 0 || String.IsNullOrEmpty(text)) return null;
            if (Armada.Core.Services.TypedDecisionEgress.IsUnreadable(text!)) return "(unreadable state)";
            foreach (string marker in markers)
            {
                if (String.IsNullOrWhiteSpace(marker)) continue;
                if (text!.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return marker;
            }
            return null;
        }

        /// <summary>
        /// Whether a mission on this vessel may send decision state off the host.
        /// </summary>
        /// <param name="vesselId">The mission's vessel id, or null when unknown.</param>
        /// <returns>False only when the vessel is on the exclusion list.</returns>
        public bool AllowsEgress(string? vesselId)
        {
            if (String.IsNullOrWhiteSpace(vesselId)) return true;
            foreach (string excluded in _EgressExcludedVesselIds)
                if (String.Equals(excluded, vesselId, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>
        /// Configuration for the captain-facing typed-decision tool.
        /// </summary>
        public TypedDecisionCaptainToolSettings CaptainTool
        {
            get => _CaptainTool;
            set => _CaptainTool = value ?? new TypedDecisionCaptainToolSettings();
        }

        private int _TimeoutSeconds = 10;
        private int _MaxStateChars = 60000;
        private List<string> _EgressExcludedVesselIds = new List<string>();
        private List<string> _EgressExcludedMarkers = new List<string>();
        private Dictionary<string, TypedDecisionRuleSettings> _Decisions = DefaultDecisions();
        private TypedDecisionCaptainToolSettings _CaptainTool = new TypedDecisionCaptainToolSettings();
        private Dictionary<string, CustomTypedDecisionSettings> _Custom = new Dictionary<string, CustomTypedDecisionSettings>(StringComparer.Ordinal);

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
        /// Resolve the effective configuration for a custom decision. The effective mode is the
        /// minimum of the global mode and the custom decision's own mode, so the global cap can never
        /// be exceeded. An unknown or missing custom decision is Off.
        /// </summary>
        /// <param name="name">Custom decision name.</param>
        /// <returns>The resolved effective mode and gate threshold; never null.</returns>
        public ResolvedTypedDecision ForCustom(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || !_Custom.TryGetValue(name, out CustomTypedDecisionSettings? custom) || custom == null)
                return new ResolvedTypedDecision(TypedDecisionModeEnum.Off, 0.0);
            TypedDecisionModeEnum global = EffectiveMode;
            TypedDecisionModeEnum effective = global < custom.Mode ? global : custom.Mode;
            return new ResolvedTypedDecision(effective, custom.GateThreshold);
        }

        private static Dictionary<string, CustomTypedDecisionSettings> CloneCustom(Dictionary<string, CustomTypedDecisionSettings>? source)
        {
            Dictionary<string, CustomTypedDecisionSettings> copy = new Dictionary<string, CustomTypedDecisionSettings>(StringComparer.Ordinal);
            if (source != null)
                foreach (KeyValuePair<string, CustomTypedDecisionSettings> pair in source)
                    if (!String.IsNullOrWhiteSpace(pair.Key) && pair.Value != null) copy[pair.Key] = pair.Value.Clone();
            return copy;
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
            // Copied, not shared: the live object is read by every adapter, so a reload must never leave
            // an adapter holding the previous file's list.
            EgressExcludedVesselIds = new List<string>(source.EgressExcludedVesselIds ?? new List<string>());
            EgressExcludedMarkers = new List<string>(source.EgressExcludedMarkers ?? new List<string>());
            Decisions = source.Decisions;
            CaptainTool = source.CaptainTool;
            Custom = CloneCustom(source.Custom);
            Retention = source.Retention;
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
                ["prior_art"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.90 },
                // 0.55, not the 0.90 default, on measured readings: across ten real candidates the live
                // model put settled outputs at 0.09-0.15 and load-bearing ones at 0.60-0.67, so it never
                // reaches 0.90 and a 0.90 gate would spare nothing at all. A gate that executes nothing
                // is worse than one that fails, because its mode still reads as enforced.
                ["context_compaction"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.55 }
            };
        }
    }

    /// <summary>
    /// Retention of redacted decision state on this host, as the training and evaluation set for a
    /// local classifier. Off by default. Enabling it retains nothing on its own: each decision opts
    /// in with <see cref="TypedDecisionRuleSettings.RetainState"/>.
    /// </summary>
    public class TypedDecisionRetentionSettings
    {
        /// <summary>
        /// Whether retention runs at all. The kill switch for the whole store. Default false.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Days of samples kept. Files older than this are deleted by the prune sweep. Default 90.
        /// </summary>
        public int RetentionDays
        {
            get => _RetentionDays;
            set => _RetentionDays = Math.Clamp(value, 1, 3650);
        }

        /// <summary>
        /// Samples one decision needs before it is reported as trainable. Default 200.
        /// </summary>
        public int MinimumSamplesPerDecision
        {
            get => _MinimumSamplesPerDecision;
            set => _MinimumSamplesPerDecision = Math.Max(1, value);
        }

        private int _RetentionDays = 90;
        private int _MinimumSamplesPerDecision = 200;
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
        /// Whether this decision's REDACTED state is retained on the host as training and evaluation
        /// data (owner ruling 2026-09-17). Opt-in per decision and additionally gated by
        /// <see cref="TypedDecisionRetentionSettings.Enabled"/>. Retained text never leaves the host
        /// and never enters an event payload. Default false.
        /// </summary>
        public bool RetainState { get; set; } = false;

        /// <summary>
        /// This decision's own excluded markers, replacing the global list for it; null (the default) inherits
        /// <see cref="TypedDecisionSettings.EgressExcludedMarkers"/>. An empty list opts the decision out, which
        /// fits a decision whose state is only brief or objective text: a brief names paths but does not carry
        /// their content, so matching it would stop the decision for no protection.
        /// </summary>
        public List<string>? EgressExcludedMarkers { get; set; } = null;

        /// <summary>
        /// Confidence at or above which the model may gate this decision. Below it, the rule
        /// stands. Zero means unset (the decision has no numeric gate).
        /// </summary>
        public double GateThreshold
        {
            get => _GateThreshold;
            set => _GateThreshold = Math.Max(0.0, Math.Min(1.0, value));
        }

        /// <summary>
        /// For a built-in decision that ships an embedded definition, an optional operator override of its
        /// wording only (a question's instructions and the meanings of its existing options and poles).
        /// The override cannot add, remove, or rename a question, change a question's kind, or change a
        /// finding direction — its type carries no field for any of those — so it can never flip the
        /// decision into an approving direction. Null means the embedded default is used unchanged.
        /// </summary>
        public BuiltinDecisionDefinitionOverride? Definition { get; set; }

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
        /// Maximum characters of state a captain call may transmit after redaction. Default 60,000, the
        /// same budget as the decisions the admiral asks itself.
        /// </summary>
        public int MaxStateChars
        {
            get => _MaxStateChars;
            set => _MaxStateChars = Math.Max(256, value);
        }

        private int _MaxStateChars = 60000;
    }

    /// <summary>
    /// The effective typed-decision configuration for one decision point after the global cap is
    /// applied. Immutable.
    /// </summary>
    /// <param name="Mode">The effective mode (minimum of global and decision mode).</param>
    /// <param name="GateThreshold">The decision's gate threshold.</param>
    public sealed record ResolvedTypedDecision(TypedDecisionModeEnum Mode, double GateThreshold);
}
