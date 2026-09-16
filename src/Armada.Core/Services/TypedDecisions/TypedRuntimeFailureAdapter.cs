namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
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

        /// <summary>
        /// The captain's provider key family (see <see cref="TypedRuntimeFailureAdapter.KeyFamilyOf"/>):
        /// the runtime plus where its credential comes from, never the key itself. Not sent to the model.
        /// </summary>
        public string KeyFamily { get; init; } = String.Empty;
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

        #region Public-Members

        /// <summary>
        /// Event type emitted when the model reads a runtime fault as fleet-wide at or above
        /// <see cref="FleetWideThreshold"/>. The event names the captain key family, never the key.
        /// </summary>
        public const string AccountFaultSuspectedEventType = "provider.account_fault_suspected";

        /// <summary>The fleet-wide reading at or above which an account fault is reported.</summary>
        public const double FleetWideThreshold = 0.9;

        /// <summary>
        /// Optional broadcast board-note poster for a suspected account fault. When null the event is
        /// still recorded and no note is posted.
        /// </summary>
        public IBoardNotePoster? NotePoster { get; set; }

        /// <summary>
        /// The provider key family of a captain: its runtime plus where its credential comes from
        /// (a model endpoint, a captain-specific key, or the runtime's own login). It identifies which
        /// captains share one account without ever carrying key material.
        /// </summary>
        /// <param name="captain">The captain; null yields <c>unknown</c>.</param>
        /// <returns>The key family label.</returns>
        public static string KeyFamilyOf(Captain? captain)
        {
            if (captain == null) return "unknown";
            string source;
            if (!String.IsNullOrWhiteSpace(captain.ModelEndpointId)) source = "model-endpoint";
            else if (!String.IsNullOrWhiteSpace(captain.ApiKey)) source = "captain-key";
            else source = "runtime-login";
            return captain.Runtime.ToString() + "/" + source;
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
        protected override async Task OnModelReadingAsync(RuntimeFailureDecisionInput input, RuntimeFailureReading model, ResolvedTypedDecision cfg, CancellationToken token)
        {
            // A fleet-wide reading is reported, never acted on: it names the key family on an event and
            // a broadcast board note so an operator checks the provider account. This path never benches,
            // quarantines, or stops a captain, and it runs only when the decision is gated.
            if (cfg.Mode != TypedDecisionModeEnum.Gate) return;
            if (model.FleetWide < FleetWideThreshold) return;

            string family = String.IsNullOrWhiteSpace(input.KeyFamily) ? "unknown" : input.KeyFamily;
            string fleetWide = model.FleetWide.ToString("0.00", CultureInfo.InvariantCulture);
            string message = "key_family=" + family + " kind=" + model.Kind + " fleet_wide=" + fleetWide;
            string payload = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["decision"] = DecisionPoint,
                ["key_family"] = family,
                ["runtime"] = input.Runtime,
                ["kind"] = model.Kind,
                ["fleet_wide"] = model.FleetWide,
                ["benched"] = false
            });

            await Recorder.RecordDomainEventAsync(AccountFaultSuspectedEventType, message, payload, input.Mission, token).ConfigureAwait(false);

            IBoardNotePoster? poster = NotePoster;
            if (poster == null) return;
            string note = "Provider account fault suspected for captain key family " + family
                + ": the runtime_failure decision reads a '" + model.Kind + "' exit as affecting every captain on that key"
                + " (fleet_wide " + fleetWide + "). No captain was benched. Check the provider account before dispatching more work on this key family.";
            try
            {
                await poster.PostBroadcastAsync(note, input.Mission?.VesselId, input.Mission?.Id, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logging.Warn(_Header + "account-fault board note failed: " + ex.Message);
            }
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
