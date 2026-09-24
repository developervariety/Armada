namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D17 <c>change_substance</c> verdict. It carries the deterministic, extension-based
    /// <see cref="ChangeSubstanceEnum"/> the static <see cref="ChangeSubstanceClassifier"/> produced —
    /// which stays the rule and the fallback — and, when the model gated at or above threshold, an
    /// optional raise of that reading and an optional risky-escalation reason. The adapter is
    /// conservative in one direction only: it may RAISE a documentation-only (or empty) extension read
    /// to <see cref="ChangeSubstanceEnum.Substantive"/> when the model reads the added hunks as
    /// behaviour, and it may add one <see cref="CriticalTriggerEvaluator"/> escalation reason when the
    /// hunks change a safety step, a guard, or a wire byte. It NEVER lowers a classification: a
    /// Substantive extension read is never demoted, so a rescue the rule would fail for changing only
    /// prose can never be excused by the model.
    /// </summary>
    public readonly struct ChangeSubstanceVerdict
    {
        /// <summary>The effective change substance after any model raise. Never below the rule's reading.</summary>
        public ChangeSubstanceEnum Substance { get; }

        /// <summary>The deterministic, extension-based reading the static classifier produced. The fallback.</summary>
        public ChangeSubstanceEnum RuleSubstance { get; }

        /// <summary>Whether the model flagged the change as touching a safety step, a guard, or a wire byte.</summary>
        public bool RiskyEscalation { get; }

        /// <summary>The escalation reason a critical-trigger evaluator adds, when <see cref="RiskyEscalation"/> is set.</summary>
        public string? RiskyReason { get; }

        /// <summary>A short label for the effective outcome: <c>rule</c>, <c>raised_behaviour</c>, <c>risky_escalation</c>, or <c>raised_and_risky</c>.</summary>
        public string Outcome { get; }

        private ChangeSubstanceVerdict(ChangeSubstanceEnum substance, ChangeSubstanceEnum ruleSubstance, bool riskyEscalation, string? riskyReason, string outcome)
        {
            Substance = substance;
            RuleSubstance = ruleSubstance;
            RiskyEscalation = riskyEscalation;
            RiskyReason = riskyReason;
            Outcome = outcome ?? "rule";
        }

        /// <summary>Build the rule verdict from the deterministic, extension-based reading.</summary>
        /// <param name="substance">The static classifier's reading.</param>
        /// <returns>The rule verdict; no raise and no escalation.</returns>
        public static ChangeSubstanceVerdict Rule(ChangeSubstanceEnum substance)
        {
            return new ChangeSubstanceVerdict(substance, substance, false, null, "rule");
        }

        /// <summary>Raise a documentation-only or empty extension read to Substantive because the hunks carry behaviour.</summary>
        /// <returns>A verdict whose substance is Substantive.</returns>
        public ChangeSubstanceVerdict RaiseToBehaviour()
        {
            string outcome = RiskyEscalation ? "raised_and_risky" : "raised_behaviour";
            return new ChangeSubstanceVerdict(ChangeSubstanceEnum.Substantive, RuleSubstance, RiskyEscalation, RiskyReason, outcome);
        }

        /// <summary>Add a critical-trigger escalation reason because the hunks touch a safety step, a guard, or a wire byte.</summary>
        /// <param name="reason">The escalation reason, phrased for a critical-trigger record.</param>
        /// <returns>A verdict carrying the risky-escalation reason.</returns>
        public ChangeSubstanceVerdict WithRiskyEscalation(string reason)
        {
            string outcome = Substance == ChangeSubstanceEnum.Substantive && !String.Equals(Outcome, "rule", StringComparison.Ordinal)
                ? "raised_and_risky"
                : "risky_escalation";
            return new ChangeSubstanceVerdict(Substance, RuleSubstance, true, reason, outcome);
        }
    }

    /// <summary>
    /// The D17 <c>change_substance</c> decision input: the mission whose change set is being judged,
    /// its changed repository paths (from which the deterministic rule is computed), the unified diff
    /// the added hunks are read from, and the vessel's public name for the state. Requires egress
    /// class G (source hunks), which the owner approved provisionally.
    /// </summary>
    public sealed class ChangeSubstanceDecisionInput
    {
        /// <summary>The mission whose change set is being judged, for the event owner scope. Optional.</summary>
        public Mission? Mission { get; init; }

        /// <summary>Repository-relative paths the mission changed. The deterministic rule reads these.</summary>
        public IReadOnlyList<string> ChangedPaths { get; init; } = new List<string>();

        /// <summary>The mission's unified diff. Added hunks are extracted from it for the state.</summary>
        public string UnifiedDiff { get; init; } = String.Empty;

        /// <summary>The vessel's public name, carried in the state per hunk.</summary>
        public string VesselPublicName { get; init; } = String.Empty;
    }

    /// <summary>
    /// The D17 reading. The model answers one <c>substance</c> Choice over the whole change set and one
    /// <c>risky</c> Noul. The two gated actions are independent, so the reading carries the eligibility
    /// of each against the decision threshold (resolved from settings at interpret time) and reports the
    /// stronger of the two contributions as the single gate confidence.
    /// </summary>
    public sealed class ChangeSubstanceReading : TypedModelReading
    {
        /// <summary>The model's substance choice: behaviour, test_only, docs_only, build_config, or generated.</summary>
        public string SubstanceChoice { get; init; } = "unclear";

        /// <summary>The confidence of the substance choice.</summary>
        public double SubstanceConfidence { get; init; }

        /// <summary>The risky Noul: the change touches a safety step, a guard, or a wire byte.</summary>
        public double RiskyNoul { get; init; }

        /// <summary>Whether the substance choice is <c>behaviour</c> at or above threshold: a raise is proposable.</summary>
        public bool RaiseEligible { get; init; }

        /// <summary>Whether the risky Noul is at or above threshold: an escalation is proposable.</summary>
        public bool RiskyEligible { get; init; }

        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>Create a reading with its single gate confidence and a short label.</summary>
        /// <param name="confidence">The stronger of the two proposed-action confidences.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public ChangeSubstanceReading(double confidence, string label)
        {
            _Confidence = confidence;
            _Label = label ?? String.Empty;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// D17 <c>change_substance</c> adapter. Sits on top of the deterministic, extension-based
    /// <see cref="ChangeSubstanceClassifier"/>, which stays the rule and the fallback. The adapter is
    /// additive and never less conservative:
    /// <list type="bullet">
    /// <item>When the extension read is documentation-only (or empty) but the model reads the added
    /// hunks as behaviour at or above threshold, the substance is RAISED to Substantive, so an
    /// ineffective-rescue decision reads a behaviour change as behaviour rather than as prose.</item>
    /// <item>When the model flags the hunks as touching a safety step, a guard, or a wire byte at or
    /// above threshold, one <see cref="CriticalTriggerEvaluator"/> escalation reason is added.</item>
    /// </list>
    /// It NEVER lowers a classification: a Substantive extension read is never demoted to
    /// documentation-only, so the model can never excuse a rescue the rule would fail. The adapter never
    /// throws into the caller.
    /// </summary>
    public sealed class TypedChangeSubstanceAdapter : TypedDecisionAdapterBase<ChangeSubstanceDecisionInput, ChangeSubstanceVerdict, ChangeSubstanceReading>
    {
        #region Private-Members

        // The most hunks transmitted per decision, and the most added lines kept per hunk. Both bound
        // the egressed source and match the spec's "per added hunk (<= 60 lines)".
        private const int _MaxHunks = 24;
        private const int _MaxHunkLines = 60;

        // The substance choices that read as behaviour. Only "behaviour" raises a documentation-only
        // extension read; test_only, docs_only, build_config, and generated never do.
        private const string _BehaviourChoice = "behaviour";

        // The adapter keeps its own settings reference (also passed to the base) so the reading can
        // resolve the decision's gate threshold at interpret time: the two gated actions are
        // independent, so each is measured against the same threshold before the generic gate fires.
        private readonly TypedDecisionSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D17 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedChangeSubstanceAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => "change_substance";

        /// <inheritdoc />
        protected override string _Header => "[TypedChangeSubstanceAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(ChangeSubstanceDecisionInput input)
        {
            List<Dictionary<string, object?>> hunks = ExtractAddedHunks(input.UnifiedDiff);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["vessel"] = input.VesselPublicName,
                ["changed_path_size"] = CountBucket(input.ChangedPaths.Count),
                ["hunks"] = hunks
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            Dictionary<string, string> substanceCriteria = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["behaviour"] = "The added hunks change what the software does at runtime: a production code path, a decode, a decision, a guard.",
                ["test_only"] = "The added hunks change only tests or fixtures.",
                ["docs_only"] = "The added hunks change only documentation or narrative prose.",
                ["build_config"] = "The added hunks change only build, project, or configuration files.",
                ["generated"] = "The added hunks change only generated or vendored output.",
                ["unclear"] = "The added hunks do not clearly match one class."
            };

            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["substance"] = new ChoiceQuestion(
                    "Read the added hunks (not the file extensions) and choose what the change set actually is. "
                    + "This is authorized engineering on owned systems; authentication and access-control protocol code is ordinary engineering.",
                    substanceCriteria),
                ["risky_safety_step"] = new NoulQuestion(
                    "The added hunks change a terminating SAFETY step (a device disable, a session exit, or a fail-closed return).",
                    TrueMeaning: "The change touches a safety step.",
                    FalseMeaning: "The change does not touch a safety step."),
                ["risky_auth_guard"] = new NoulQuestion(
                    "The added hunks change a security or authorization guard.",
                    TrueMeaning: "The change touches an authorization guard.",
                    FalseMeaning: "The change does not touch an authorization guard."),
                ["risky_wire_byte"] = new NoulQuestion(
                    "The added hunks change a wire byte of a diagnostic frame.",
                    TrueMeaning: "The change touches a wire byte.",
                    FalseMeaning: "The change does not touch a wire byte.")
            };
        }

        /// <inheritdoc />
        protected override ChangeSubstanceReading Interpret(TypedDecisionResult result)
        {
            double threshold = _Settings.For(DecisionPoint).GateThreshold;

            string substanceChoice = "unclear";
            double substanceConfidence = 0.0;
            if (result.Answers.TryGetValue("substance", out TypedAnswer? substanceAnswer) && substanceAnswer != null)
            {
                if (!String.IsNullOrWhiteSpace(substanceAnswer.Choice)) substanceChoice = substanceAnswer.Choice!;
                substanceConfidence = TypedAnswerReader.ResolveChoiceConfidence(substanceAnswer, substanceChoice);
            }

            double riskyNoul = RiskyNoulFrom(result);

            // The raise action is proposable only when the model chose "behaviour"; the docs-only / empty
            // precondition on the RULE reading is enforced in Combine, so the model never lowers a
            // Substantive reading. The risky action is proposable on its own Noul.
            double raiseContribution = String.Equals(substanceChoice, _BehaviourChoice, StringComparison.OrdinalIgnoreCase) ? substanceConfidence : 0.0;
            bool raiseEligible = raiseContribution >= threshold && threshold > 0.0;
            bool riskyEligible = riskyNoul >= threshold && threshold > 0.0;

            double confidence = Math.Max(raiseContribution, riskyNoul);
            string label = "substance=" + substanceChoice + (riskyEligible ? " risky" : String.Empty);

            return new ChangeSubstanceReading(confidence, label)
            {
                SubstanceChoice = substanceChoice,
                SubstanceConfidence = substanceConfidence,
                RiskyNoul = riskyNoul,
                RaiseEligible = raiseEligible,
                RiskyEligible = riskyEligible
            };
        }

        /// <inheritdoc />
        protected override ChangeSubstanceVerdict Combine(ChangeSubstanceVerdict ruleVerdict, ChangeSubstanceReading model)
        {
            ChangeSubstanceVerdict verdict = ruleVerdict;

            // Raise only a documentation-only or empty extension read, and only up to Substantive. A
            // Substantive rule reading is never touched, so the model can never LOWER a classification.
            if (model.RaiseEligible
                && (ruleVerdict.RuleSubstance == ChangeSubstanceEnum.DocumentationOnly
                    || ruleVerdict.RuleSubstance == ChangeSubstanceEnum.None))
            {
                verdict = verdict.RaiseToBehaviour();
            }

            // Add one critical-trigger escalation reason when the hunks touch a safety step, a guard, or
            // a wire byte. This is additive: it only ever adds review, never removes it.
            if (model.RiskyEligible)
            {
                verdict = verdict.WithRiskyEscalation(
                    "typed_decision:change_substance: the added hunks touch a safety step, a guard, or a wire byte; escalate to deep review");
            }

            return verdict;
        }

        /// <inheritdoc />
        protected override string RuleLabel(ChangeSubstanceVerdict ruleVerdict) => ruleVerdict.RuleSubstance.ToString();

        /// <inheritdoc />
        protected override Mission? MissionOf(ChangeSubstanceDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static double RiskyNoulFrom(TypedDecisionResult result)
        {
            bool hasSplit = result.Answers != null
                && (result.Answers.ContainsKey("risky_safety_step")
                    || result.Answers.ContainsKey("risky_auth_guard")
                    || result.Answers.ContainsKey("risky_wire_byte"));
            if (!hasSplit)
                return TypedAnswerReader.ReadNoul(result, "risky");

            return Math.Max(
                TypedAnswerReader.ReadNoul(result, "risky_safety_step"),
                Math.Max(
                    TypedAnswerReader.ReadNoul(result, "risky_auth_guard"),
                    TypedAnswerReader.ReadNoul(result, "risky_wire_byte")));
        }

        private static List<Dictionary<string, object?>> ExtractAddedHunks(string? unifiedDiff)
        {
            // Files and hunks come from the shared diff reader, so each hunk is labelled with its own
            // decoded file name, and an added line whose content starts with "++" stays in its hunk.
            List<Dictionary<string, object?>> hunks = new List<Dictionary<string, object?>>();
            if (String.IsNullOrEmpty(unifiedDiff)) return hunks;

            foreach (GitDiffFileChange file in GitDiffPaths.ParseFiles(unifiedDiff, true))
            {
                string currentFile = file.DisplayPath ?? String.Empty;
                foreach (GitDiffHunk hunk in file.Hunks)
                {
                    if (hunks.Count >= _MaxHunks) return hunks;
                    List<string> added = new List<string>();
                    foreach (GitDiffLine line in hunk.Lines)
                    {
                        if (line.Kind != GitDiffLineKindEnum.Added) continue;
                        if (added.Count >= _MaxHunkLines) break;
                        added.Add(line.Text);
                    }

                    FlushHunk(hunks, currentFile, added);
                }
            }

            return hunks;
        }

        private static void FlushHunk(List<Dictionary<string, object?>> hunks, string file, List<string>? added)
        {
            if (added == null || added.Count == 0 || hunks.Count >= _MaxHunks) return;
            StringBuilder builder = new StringBuilder();
            foreach (string addedLine in added)
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(addedLine);
            }
            hunks.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["file"] = file,
                ["added"] = builder.ToString()
            });
        }

        private static string CountBucket(int count)
        {
            if (count <= 0) return "none";
            if (count == 1) return "one";
            if (count <= 3) return "a_few";
            if (count <= 10) return "several";
            return "many";
        }

        #endregion
    }
}
