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
    /// The D15 <c>flake_score</c> verdict. The deterministic rule is "a red check fails the mission";
    /// this adapter never overturns that. Its one possible action is to RECOMMEND an isolated,
    /// class-filtered re-run whose real result becomes the truth. The verdict carries only that
    /// recommendation and the reason; it never carries a "pass", so the model can never mark a red check
    /// green. Only a genuine passing isolated re-run — real execution, not the model — can clear the red.
    /// </summary>
    public readonly struct FlakeScoreVerdict
    {
        /// <summary>Whether an isolated class-filtered re-run is recommended for this red result.</summary>
        public bool RerunRecommended { get; }

        /// <summary>The reason the re-run is recommended, for the record. Null when none is recommended.</summary>
        public string? Reason { get; }

        /// <summary>The model's flake-likelihood label, or <c>rule</c> when the rule stands.</summary>
        public string Outcome { get; }

        private FlakeScoreVerdict(bool rerunRecommended, string? reason, string outcome)
        {
            RerunRecommended = rerunRecommended;
            Reason = reason;
            Outcome = outcome ?? "rule";
        }

        /// <summary>The rule verdict: the red stands, no re-run is recommended.</summary>
        /// <returns>A verdict that recommends nothing.</returns>
        public static FlakeScoreVerdict NoRerun() => new FlakeScoreVerdict(false, null, "rule");

        /// <summary>Recommend an isolated class-filtered re-run because the failure reads as load or a known flaky family.</summary>
        /// <param name="reason">The reason, for the record.</param>
        /// <param name="likelihood">The flake-likelihood label.</param>
        /// <returns>A verdict recommending the re-run.</returns>
        public FlakeScoreVerdict RecommendRerun(string reason, string likelihood)
        {
            return new FlakeScoreVerdict(true, reason, likelihood);
        }
    }

    /// <summary>
    /// The D15 <c>flake_score</c> decision input: the failing test names, the assertion lines
    /// (expected/actual), the files the change touched, whether the same tests failed on another branch
    /// in the last 24 hours, and the deterministic failure class the classifier assigned.
    /// </summary>
    public sealed class FlakeScoreDecisionInput
    {
        /// <summary>The mission whose red check is being scored, for the event owner scope. Optional.</summary>
        public Mission? Mission { get; init; }

        /// <summary>The names of the tests the runner reported as failed.</summary>
        public IReadOnlyList<string> FailingTestNames { get; init; } = new List<string>();

        /// <summary>The assertion lines (expected/actual) from the runner output.</summary>
        public string AssertionLines { get; init; } = String.Empty;

        /// <summary>The repository-relative files the change touched.</summary>
        public IReadOnlyList<string> TouchedFiles { get; init; } = new List<string>();

        /// <summary>Whether the same failing test names failed on another branch in the last 24 hours.</summary>
        public bool SameTestFailedElsewhere24h { get; init; }

        /// <summary>The deterministic failure class the classifier assigned.</summary>
        public DefinitionOfDoneFailureClassEnum RuleClass { get; init; }
    }

    /// <summary>
    /// The D15 reading. The model answers a <c>flake_likelihood</c> Score over the ordered levels
    /// [deterministic, likely real, likely load, known flaky family] and an <c>outside_diff</c> Noul.
    /// The single gated action — recommend a re-run — is proposable only at the two flake levels
    /// ("likely load", "known flaky family"), so the reading reports the Score answer's confidence at
    /// those levels and zero otherwise, leaving the rule standing when the failure reads as
    /// deterministic or a likely-real defect.
    /// </summary>
    public sealed class FlakeScoreReading : TypedModelReading
    {
        /// <summary>The flake-likelihood level index, or -1 when absent.</summary>
        public double LikelihoodIndex { get; init; } = -1.0;

        /// <summary>The <c>outside_diff</c> Noul: the failing tests live in files the change did not touch.</summary>
        public double OutsideDiffNoul { get; init; }

        /// <summary>Whether the level is at or above "likely load" (index 2): a re-run is proposable.</summary>
        public bool RerunEligible { get; init; }

        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>Create a reading with its single gate confidence and a short label.</summary>
        /// <param name="confidence">The re-run action confidence.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public FlakeScoreReading(double confidence, string label)
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
    /// D15 <c>flake_score</c> adapter. Sits on top of the deterministic rule that a red check fails the
    /// mission; the rule stays and is the fallback. The adapter's only action is additive: when the
    /// failure reads as "likely load" or a "known flaky family" at or above threshold, it RECOMMENDS an
    /// isolated class-filtered re-run. The gate that consumes the recommendation runs that re-run and
    /// records both results; the re-run's real result is the truth. The adapter never returns a passing
    /// verdict, so it never marks a red check green — only a genuine passing isolated re-run can. The
    /// adapter never throws into the caller.
    /// </summary>
    public sealed class TypedFlakeScoreAdapter : TypedDecisionAdapterBase<FlakeScoreDecisionInput, FlakeScoreVerdict, FlakeScoreReading>
    {
        #region Private-Members

        // flake_likelihood levels, in order. A re-run is proposable at or above "likely load" (index 2).
        private const double _LikelyLoadLevel = 2.0;

        private static readonly IReadOnlyList<string> _LikelihoodLevels = new List<string>
        {
            "deterministic",
            "likely real",
            "likely load",
            "known flaky family"
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D15 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedFlakeScoreAdapter(
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
        protected override string DecisionPoint => "flake_score";

        /// <inheritdoc />
        protected override string _Header => "[TypedFlakeScoreAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(FlakeScoreDecisionInput input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["failing_tests"] = new List<string>(input.FailingTestNames),
                ["assertion_lines"] = input.AssertionLines,
                ["touched_files"] = new List<string>(input.TouchedFiles),
                ["same_test_failed_elsewhere_24h"] = input.SameTestFailedElsewhere24h,
                ["classifier_class"] = input.RuleClass.ToString()
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["flake_likelihood"] = new ScoreQuestion(
                    "How likely is this failure a flake rather than a real defect? A load flake fails under a concurrent "
                    + "suite and passes alone, often advertised as a WRONG VALUE rather than a timeout; a known flaky family "
                    + "is a class documented as load-sensitive. This is authorized heavy-duty vehicle diagnostics tooling.",
                    _LikelihoodLevels),
                ["outside_diff"] = new NoulQuestion(
                    "The failing tests live in files the change did not touch.",
                    TrueMeaning: "The failing tests are outside the change's own files.",
                    FalseMeaning: "The failing tests are in files the change touched.")
            };
        }

        /// <inheritdoc />
        protected override FlakeScoreReading Interpret(TypedDecisionResult result)
        {
            double index = -1.0;
            double confidence = 0.0;
            if (result.Answers.TryGetValue("flake_likelihood", out TypedAnswer? scoreAnswer) && scoreAnswer != null && scoreAnswer.Score.HasValue)
            {
                index = scoreAnswer.Score.Value;
                confidence = scoreAnswer.Confidence ?? 0.0;
            }

            double outsideDiff = ReadNoul(result, "outside_diff");
            bool rerunEligible = index >= _LikelyLoadLevel;

            // The re-run is proposed only at a flake level; a deterministic or likely-real failure
            // proposes nothing and the rule (the red) stands.
            double actionConfidence = rerunEligible ? confidence : 0.0;
            string label = index < 0.0 ? "unscored" : LevelLabel(index);

            return new FlakeScoreReading(actionConfidence, label)
            {
                LikelihoodIndex = index,
                OutsideDiffNoul = outsideDiff,
                RerunEligible = rerunEligible
            };
        }

        /// <inheritdoc />
        protected override FlakeScoreVerdict Combine(FlakeScoreVerdict ruleVerdict, FlakeScoreReading model)
        {
            // The rule is "the red stands". The model may only ESCALATE to recommending an isolated
            // re-run whose real result is the truth; it never returns a pass, so it never marks a red
            // green. A re-run is recommended only at a flake level (guaranteed at threshold by the gate).
            if (!model.RerunEligible) return ruleVerdict;

            return ruleVerdict.RecommendRerun(
                "typed_decision:flake_score: the failure reads as " + LevelLabel(model.LikelihoodIndex)
                + "; recommend an isolated class-filtered re-run whose result is the truth",
                LevelLabel(model.LikelihoodIndex));
        }

        /// <inheritdoc />
        protected override string RuleLabel(FlakeScoreVerdict ruleVerdict) => ruleVerdict.RerunRecommended ? "rerun" : "red_stands";

        /// <inheritdoc />
        protected override Mission? MissionOf(FlakeScoreDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static string LevelLabel(double index)
        {
            if (index < 0.0) return "unscored";
            int i = (int)Math.Round(index, MidpointRounding.AwayFromZero);
            if (i < 0) i = 0;
            if (i >= _LikelihoodLevels.Count) i = _LikelihoodLevels.Count - 1;
            return _LikelihoodLevels[i];
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
