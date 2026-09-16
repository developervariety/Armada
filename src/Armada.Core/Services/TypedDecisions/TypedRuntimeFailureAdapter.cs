namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D3 <c>runtime_failure</c> decision input: the runtime exit code and output tail the rule
    /// classified, plus the runtime and model id for context.
    /// </summary>
    public sealed class RuntimeFailureDecisionInput
    {
        /// <summary>The mission, for the event owner scope; may be null.</summary>
        public Mission? Mission { get; init; }

        /// <summary>The process exit code, when recorded.</summary>
        public int? ExitCode { get; init; }

        /// <summary>The last lines of the runtime output.</summary>
        public string Tail { get; init; } = String.Empty;

        /// <summary>The runtime name (ClaudeCode, Codex, OpenCode, and so on).</summary>
        public string Runtime { get; init; } = String.Empty;

        /// <summary>The model id the captain ran.</summary>
        public string ModelId { get; init; } = String.Empty;
    }

    /// <summary>
    /// The D3 reading: the model's kind choice, how strongly it reads the fault as fleet-wide, and
    /// the confidence of the single upgrade proposed.
    /// </summary>
    public sealed class RuntimeFailureReading : TypedModelReading
    {
        /// <summary>The model's kind choice, or <c>unclear</c>.</summary>
        public string Kind { get; init; } = "unclear";

        /// <summary>How strongly the model reads the fault as affecting every captain on the key.</summary>
        public double FleetWide { get; init; }

        private readonly double _Confidence;

        /// <summary>Create a reading with its upgrade confidence.</summary>
        /// <param name="confidence">The proposed-upgrade confidence.</param>
        public RuntimeFailureReading(double confidence)
        {
            _Confidence = confidence;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => Kind;
    }

    /// <summary>
    /// D3 <c>runtime_failure</c> adapter. The deterministic signature classifier is authoritative for
    /// every recognised outcome: a Clean exit, a UsageLimit, or an AuthFailure the rule already read
    /// are returned unchanged. Only a <see cref="RuntimeFailureKindEnum.Crash"/> — a non-zero exit
    /// with no recognised provider signature — may be upgraded, and only ever to the more
    /// conservative UsageLimit or AuthFailure so a masked provider fault stops being treated as a
    /// crash loop. The model never downgrades a fault to Clean and never benches a captain by itself.
    /// </summary>
    public sealed class TypedRuntimeFailureAdapter : TypedDecisionAdapterBase<RuntimeFailureDecisionInput, RuntimeFailureKindEnum, RuntimeFailureReading>
    {
        #region Private-Members

        // Kinds that upgrade a bare Crash to the recoverable provider outcome the crash-loop path must
        // not treat as a code crash.
        private static readonly HashSet<string> _UsageLimitKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "usage_limit",
            "billing_hold",
            "capacity"
        };

        private const string _AuthKind = "auth";

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D3 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedRuntimeFailureAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => "runtime_failure";

        /// <inheritdoc />
        protected override string _Header => "[TypedRuntimeFailureAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(RuntimeFailureDecisionInput input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["exit_code"] = input.ExitCode,
                ["tail"] = input.Tail,
                ["runtime"] = input.Runtime,
                ["model_id"] = input.ModelId
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["kind"] = new ChoiceQuestion(
                    "Classify why this captain runtime process ended, from the exit code and the output tail.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["usage_limit"] = "The provider throttled or rate-limited the request.",
                        ["billing_hold"] = "The provider held the account for billing or awaiting pricing confirmation.",
                        ["auth"] = "The provider rejected the credentials.",
                        ["capacity"] = "The provider was overloaded or out of capacity.",
                        ["crash"] = "A genuine process crash with no provider cause.",
                        ["clean"] = "The process ended cleanly with no fault.",
                        ["unclear"] = "The cause cannot be determined from the output."
                    }),
                ["fleet_wide"] = new NoulQuestion(
                    "Would this fault affect every captain sharing the same provider key, not just this one?",
                    TrueMeaning: "An account-level fault that fails every captain on the key.",
                    FalseMeaning: "A fault local to this captain or run.")
            };
        }

        /// <inheritdoc />
        protected override RuntimeFailureReading Interpret(TypedDecisionResult result)
        {
            string kind = "unclear";
            double kindConfidence = 0.0;
            if (result.Answers.TryGetValue("kind", out TypedAnswer? kindAnswer) && kindAnswer != null)
            {
                if (!String.IsNullOrWhiteSpace(kindAnswer.Choice)) kind = kindAnswer.Choice!;
                kindConfidence = ResolveChoiceConfidence(kindAnswer, kind);
            }

            double fleetWide = ReadNoul(result, "fleet_wide");

            // Only an upgradeable kind proposes an action; a crash/clean/unclear reading contributes no
            // confidence, so the generic gate records a shadow and the rule's classification stands.
            bool upgradeable = _UsageLimitKinds.Contains(kind) || String.Equals(kind, _AuthKind, StringComparison.Ordinal);
            double actionConfidence = upgradeable ? kindConfidence : 0.0;

            return new RuntimeFailureReading(actionConfidence)
            {
                Kind = kind,
                FleetWide = fleetWide
            };
        }

        /// <inheritdoc />
        protected override RuntimeFailureKindEnum Combine(RuntimeFailureKindEnum ruleVerdict, RuntimeFailureReading model)
        {
            // Rule hard-block wins: every recognised signature (Clean, UsageLimit, AuthFailure) is
            // authoritative. Only a bare Crash may be upgraded, and only ever to a more conservative,
            // recoverable provider outcome. The model never downgrades a fault to Clean.
            if (ruleVerdict != RuntimeFailureKindEnum.Crash) return ruleVerdict;

            if (_UsageLimitKinds.Contains(model.Kind)) return RuntimeFailureKindEnum.UsageLimit;
            if (String.Equals(model.Kind, _AuthKind, StringComparison.Ordinal)) return RuntimeFailureKindEnum.AuthFailure;

            return ruleVerdict;
        }

        /// <inheritdoc />
        protected override string RuleLabel(RuntimeFailureKindEnum ruleVerdict) => ruleVerdict.ToString();

        /// <inheritdoc />
        protected override Mission? MissionOf(RuntimeFailureDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static double ResolveChoiceConfidence(TypedAnswer answer, string choice)
        {
            if (answer.Confidence.HasValue) return answer.Confidence.Value;
            if (answer.Probabilities != null && answer.Probabilities.TryGetValue(choice, out double probability)) return probability;
            return 0.0;
        }

        private static double ReadNoul(TypedDecisionResult result, string key)
        {
            if (result.Answers.TryGetValue(key, out TypedAnswer? answer) && answer != null && answer.Noul.HasValue)
                return answer.Noul.Value;
            return 0.0;
        }

        #endregion
    }
}
