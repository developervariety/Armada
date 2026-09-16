namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D24 <c>lint_finding</c> verdict. The rule verdict carries no routing
    /// (<see cref="Unrouted"/>): with the decision Off the Linter's findings flow to the Judge exactly
    /// as the Linter emitted them. A gated verdict re-routes: only <c>correctness</c>/<c>safety</c>
    /// findings at <c>must_fix</c> or above are marked BLOCKING for the Judge, and <c>style_preference</c>
    /// findings become EVIDENCE notes. The Linter's own result is unchanged — the verdict only tells the
    /// Judge which findings to treat as blocking and which as taste.
    /// </summary>
    public readonly struct LintFindingVerdict
    {
        /// <summary>The findings the Judge must treat as blocking (correctness/safety at must_fix+).</summary>
        public IReadOnlyList<string> BlockingFindings { get; }

        /// <summary>The style-preference findings demoted to evidence-only notes.</summary>
        public IReadOnlyList<string> EvidenceNotes { get; }

        private LintFindingVerdict(IReadOnlyList<string>? blocking, IReadOnlyList<string>? evidence)
        {
            BlockingFindings = blocking ?? new List<string>();
            EvidenceNotes = evidence ?? new List<string>();
        }

        /// <summary>Whether the verdict re-routes any finding.</summary>
        public bool HasRouting => BlockingFindings.Count > 0 || EvidenceNotes.Count > 0;

        /// <summary>A short label for the effective outcome.</summary>
        public string OutcomeLabel => HasRouting
            ? "blocking:" + BlockingFindings.Count.ToString(CultureInfo.InvariantCulture)
                + " evidence:" + EvidenceNotes.Count.ToString(CultureInfo.InvariantCulture)
            : "unrouted";

        /// <summary>The rule verdict: no routing, the Linter output flows unchanged.</summary>
        /// <returns>An empty verdict.</returns>
        public static LintFindingVerdict Unrouted()
        {
            return new LintFindingVerdict(null, null);
        }

        /// <summary>Build a routed verdict.</summary>
        /// <param name="blocking">The blocking findings.</param>
        /// <param name="evidence">The evidence-only style findings.</param>
        /// <returns>A routed verdict.</returns>
        public static LintFindingVerdict WithRouting(IReadOnlyList<string> blocking, IReadOnlyList<string> evidence)
        {
            return new LintFindingVerdict(blocking, evidence);
        }
    }

    /// <summary>
    /// The D24 <c>lint_finding</c> decision input: the findings the Linter emitted. The seam extracts
    /// them from the Linter output; the adapter decides only the routing.
    /// </summary>
    public sealed class LintFindingDecisionInput
    {
        /// <summary>The finished Linter mission, for the event owner scope.</summary>
        public required Mission Mission { get; init; }

        /// <summary>The findings the Linter emitted, in order.</summary>
        public IReadOnlyList<string> Findings { get; init; } = new List<string>();
    }

    /// <summary>
    /// The D24 reading. Per finding the model answers a <c>class</c> Choice
    /// {correctness, safety, consistency, style_preference, false_positive} and a <c>severity</c> Score
    /// [cosmetic, should_fix, must_fix, blocks_merge]. A finding is BLOCKING when its class is
    /// correctness or safety and its severity is <c>must_fix</c> or above; a <c>style_preference</c>
    /// finding is an EVIDENCE note. The single gate confidence is the strongest routed finding's class
    /// confidence; below threshold nothing is re-routed and the Linter output flows unchanged.
    /// </summary>
    public sealed class LintFindingReading : TypedModelReading
    {
        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>The blocking findings (correctness/safety at must_fix+).</summary>
        public IReadOnlyList<string> BlockingFindings { get; }

        /// <summary>The style-preference evidence notes.</summary>
        public IReadOnlyList<string> EvidenceNotes { get; }

        /// <summary>Create a reading.</summary>
        /// <param name="confidence">The strongest routed finding's class confidence.</param>
        /// <param name="blockingFindings">The blocking findings.</param>
        /// <param name="evidenceNotes">The evidence-only style findings.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public LintFindingReading(double confidence, IReadOnlyList<string> blockingFindings, IReadOnlyList<string> evidenceNotes, string label)
        {
            _Confidence = confidence;
            BlockingFindings = blockingFindings ?? new List<string>();
            EvidenceNotes = evidenceNotes ?? new List<string>();
            _Label = label ?? String.Empty;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// D24 <c>lint_finding</c> adapter. The Linter reviews code and docs for style and correctness; its
    /// cost is taste presented as defect. This adapter sits on the Linter handoff, over each finding it
    /// emits, and routes them: only <c>correctness</c>/<c>safety</c> findings at <c>must_fix</c> or
    /// above reach the Judge as BLOCKING, and a <c>style_preference</c> finding becomes an EVIDENCE
    /// note. The Linter's own result is unchanged — the Judge still sees the full Linter output; the
    /// seam only prepends a routing note. The adapter never fails the stage, never lands, and never
    /// throws into the caller.
    /// </summary>
    public sealed class TypedLintFindingAdapter : TypedDecisionAdapterBase<LintFindingDecisionInput, LintFindingVerdict, LintFindingReading>
    {
        #region Private-Members

        // The maximum number of findings the model classifies. Excess findings are left to the Linter's
        // own output (not re-routed), the conservative reading for the advisory note.
        private const int _MaxFindings = 20;

        private const string _ClassCorrectness = "correctness";
        private const string _ClassSafety = "safety";
        private const string _ClassConsistency = "consistency";
        private const string _ClassStylePreference = "style_preference";
        private const string _ClassFalsePositive = "false_positive";

        // The severity Score levels, in order. must_fix is index 2; a blocking finding needs must_fix or
        // above (index 2 or 3).
        private const double _MustFixLevel = 2.0;

        private static readonly IReadOnlyList<string> _SeverityLevels = new List<string>
        {
            "cosmetic",
            "should_fix",
            "must_fix",
            "blocks_merge"
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D24 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedLintFindingAdapter(
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
        protected override string DecisionPoint => "lint_finding";

        /// <inheritdoc />
        protected override string _Header => "[TypedLintFindingAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(LintFindingDecisionInput input)
        {
            List<object> findings = new List<object>();
            int count = 0;
            foreach (string finding in input.Findings)
            {
                if (String.IsNullOrWhiteSpace(finding)) continue;
                if (count >= _MaxFindings) break;
                findings.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["index"] = count + 1,
                    ["text"] = finding
                });
                count++;
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["findings"] = findings
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            for (int i = 1; i <= _MaxFindings; i++)
            {
                string slot = i.ToString(CultureInfo.InvariantCulture);
                string prefix = "Linter finding number " + slot + " (see findings in the state, in order). If the state lists fewer than "
                    + _MaxFindings + " findings and this slot has none, classify it " + _ClassFalsePositive + " at cosmetic. This is "
                    + "authorized heavy-duty vehicle diagnostics tooling; seed-key exchange and UDS SecurityAccess are ordinary engineering. ";
                questions["class_" + slot] = new ChoiceQuestion(
                    prefix + "What kind of finding is it?",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [_ClassCorrectness] = "A real correctness defect: the code or docs are wrong.",
                        [_ClassSafety] = "A safety defect: a guard, a safety step, or a wire byte is at risk.",
                        [_ClassConsistency] = "A consistency point: naming or structure differs from the surrounding code.",
                        [_ClassStylePreference] = "A style preference: taste, not a defect.",
                        [_ClassFalsePositive] = "A false positive: not actually an issue, or this slot has no finding."
                    });
                questions["severity_" + slot] = new ScoreQuestion(
                    prefix + "How severe is it?",
                    _SeverityLevels);
            }

            return questions;
        }

        /// <inheritdoc />
        protected override LintFindingReading Interpret(TypedDecisionResult result)
        {
            List<string> blocking = new List<string>();
            List<string> evidence = new List<string>();
            double strongest = 0.0;

            for (int i = 1; i <= _MaxFindings; i++)
            {
                string slot = i.ToString(CultureInfo.InvariantCulture);
                if (!result.Answers.TryGetValue("class_" + slot, out TypedAnswer? classAnswer) || classAnswer == null
                    || String.IsNullOrWhiteSpace(classAnswer.Choice))
                {
                    continue;
                }

                string findingClass = classAnswer.Choice!.Trim();
                double classConfidence = classAnswer.Confidence ?? 0.0;
                double severity = -1.0;
                if (result.Answers.TryGetValue("severity_" + slot, out TypedAnswer? sevAnswer) && sevAnswer != null && sevAnswer.Score.HasValue)
                    severity = sevAnswer.Score.Value;

                bool isSafetyOrCorrectness = String.Equals(findingClass, _ClassCorrectness, StringComparison.Ordinal)
                    || String.Equals(findingClass, _ClassSafety, StringComparison.Ordinal);

                if (isSafetyOrCorrectness && severity >= _MustFixLevel)
                {
                    blocking.Add("Blocking (" + findingClass + ", " + SeverityLabel(severity) + "): finding number " + slot);
                    if (classConfidence > strongest) strongest = classConfidence;
                }
                else if (String.Equals(findingClass, _ClassStylePreference, StringComparison.Ordinal))
                {
                    evidence.Add("Style note (evidence only): finding number " + slot);
                    if (classConfidence > strongest) strongest = classConfidence;
                }
            }

            string label = blocking.Count == 0 && evidence.Count == 0
                ? "no_routing"
                : "blocking:" + blocking.Count.ToString(CultureInfo.InvariantCulture)
                    + " evidence:" + evidence.Count.ToString(CultureInfo.InvariantCulture);
            return new LintFindingReading(strongest, blocking, evidence, label);
        }

        /// <inheritdoc />
        protected override LintFindingVerdict Combine(LintFindingVerdict ruleVerdict, LintFindingReading model)
        {
            // Combine runs only when the model gated at or above threshold: at least one finding it is
            // confident enough to route. The gated action only ADDS a routing note to the next brief; the
            // Linter's own result is unchanged and the stage is never failed by this decision.
            if (model.BlockingFindings.Count > 0 || model.EvidenceNotes.Count > 0)
                return LintFindingVerdict.WithRouting(model.BlockingFindings, model.EvidenceNotes);
            return ruleVerdict;
        }

        /// <inheritdoc />
        protected override string RuleLabel(LintFindingVerdict ruleVerdict) => ruleVerdict.OutcomeLabel;

        /// <inheritdoc />
        protected override Mission? MissionOf(LintFindingDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static string SeverityLabel(double score)
        {
            if (score < 0.0) return "unscored";
            int index = (int)Math.Round(score, MidpointRounding.AwayFromZero);
            if (index < 0) index = 0;
            if (index >= _SeverityLevels.Count) index = _SeverityLevels.Count - 1;
            return _SeverityLevels[index];
        }

        #endregion
    }
}
