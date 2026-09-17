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
    /// One added test method the D22 <c>test_covers</c> decision reads: its name and a bounded body.
    /// </summary>
    public sealed class TestCoversMethod
    {
        /// <summary>Create an added-test descriptor.</summary>
        /// <param name="name">The test method name.</param>
        /// <param name="body">A bounded excerpt of the test body (≤ 60 lines upstream).</param>
        public TestCoversMethod(string name, string body)
        {
            Name = name ?? String.Empty;
            Body = body ?? String.Empty;
        }

        /// <summary>The test method name.</summary>
        public string Name { get; }

        /// <summary>A bounded excerpt of the test body.</summary>
        public string Body { get; }
    }

    /// <summary>
    /// The D22 <c>test_covers</c> verdict. The rule verdict carries no Judge instructions
    /// (<see cref="NoInstructions"/>); a gated verdict carries one instruction per added test the model
    /// doubts actually covers the reported symptom ("verify test X fails without the change"). The
    /// verdict NEVER fails the stage by itself: the seam only prepends the instructions to the next
    /// stage's brief, so a false reading costs the Judge one extra check, never a rejection.
    /// </summary>
    public readonly struct TestCoversVerdict
    {
        /// <summary>The Judge instructions to add to the next brief, one per doubted test.</summary>
        public IReadOnlyList<string> JudgeInstructions { get; }

        private TestCoversVerdict(IReadOnlyList<string>? instructions)
        {
            JudgeInstructions = instructions ?? new List<string>();
        }

        /// <summary>Whether the verdict carries any Judge instructions.</summary>
        public bool HasInstructions => JudgeInstructions.Count > 0;

        /// <summary>A short label for the effective outcome.</summary>
        public string OutcomeLabel => HasInstructions
            ? "judge_instructions:" + JudgeInstructions.Count.ToString(CultureInfo.InvariantCulture)
            : "no_instructions";

        /// <summary>The rule verdict: no Judge instructions.</summary>
        /// <returns>An empty verdict.</returns>
        public static TestCoversVerdict NoInstructions()
        {
            return new TestCoversVerdict(null);
        }

        /// <summary>Build a verdict carrying Judge instructions.</summary>
        /// <param name="instructions">One instruction per doubted test.</param>
        /// <returns>A verdict with instructions.</returns>
        public static TestCoversVerdict WithInstructions(IReadOnlyList<string> instructions)
        {
            return new TestCoversVerdict(instructions);
        }
    }

    /// <summary>
    /// The D22 <c>test_covers</c> decision input: the TestEngineer's added test methods and the
    /// objective's symptom sentence.
    /// </summary>
    public sealed class TestCoversDecisionInput
    {
        /// <summary>The finished TestEngineer mission, for the event owner scope.</summary>
        public required Mission Mission { get; init; }

        /// <summary>The added test methods (name and bounded body), in order.</summary>
        public IReadOnlyList<TestCoversMethod> AddedTests { get; init; } = new List<TestCoversMethod>();

        /// <summary>The objective's reported-symptom sentence.</summary>
        public string Symptom { get; init; } = String.Empty;
    }

    /// <summary>
    /// The D22 reading. Per added test the model answers three Nouls: <c>covers_symptom</c>,
    /// <c>asserts_source_text</c>, and <c>would_fail_before_fix</c>. A test is a CONCERN when it likely
    /// would NOT fail before the fix, or does NOT cover the symptom, or merely asserts source text —
    /// each the memory discriminator that a green suite proves a fix only when a test covers the
    /// reported symptom and would fail before the change. The single gate confidence is the strongest
    /// concern; below threshold no instruction is added.
    /// </summary>
    public sealed class TestCoversReading : TypedModelReading
    {
        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>The names of the added tests the model doubts, each with the concern that drove it.</summary>
        public IReadOnlyList<string> DoubtedInstructions { get; }

        /// <summary>Create a reading.</summary>
        /// <param name="confidence">The strongest concern across the added tests.</param>
        /// <param name="doubtedInstructions">One Judge instruction per doubted test.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public TestCoversReading(double confidence, IReadOnlyList<string> doubtedInstructions, string label)
        {
            _Confidence = confidence;
            DoubtedInstructions = doubtedInstructions ?? new List<string>();
            _Label = label ?? String.Empty;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// D22 <c>test_covers</c> adapter. A green suite proves a fix only when a test covers the reported
    /// symptom, and a test that asserts source text proves one copy matches. This adapter sits on the
    /// TestEngineer handoff, over the added test methods and the objective's symptom sentence, and turns
    /// a doubted test into a Judge INSTRUCTION in the next brief ("verify test X fails without the
    /// change"). It NEVER fails the stage by itself — the seam only augments the next brief, and below
    /// the decision threshold it adds nothing. The adapter never throws into the caller.
    /// </summary>
    public sealed class TypedTestCoversAdapter : TypedDecisionAdapterBase<TestCoversDecisionInput, TestCoversVerdict, TestCoversReading>
    {
        #region Private-Members

        // The maximum number of added tests the model is asked about. Excess tests are treated as
        // adequate (no instruction), the conservative reading for the advisory brief note.
        private const int _MaxTests = 12;

        // The floor at or below which covers_symptom or would_fail_before_fix reads as a concern, and at
        // or above which asserts_source_text reads as a concern. The gate threshold decides whether the
        // instruction is added at all (the strongest concern is the gate confidence); this floor decides
        // which tests contribute an instruction.
        private const double _ConcernFloor = 0.5;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D22 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedTestCoversAdapter(
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
        protected override string DecisionPoint => "test_covers";

        /// <inheritdoc />
        protected override string _Header => "[TypedTestCoversAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(TestCoversDecisionInput input)
        {
            List<object> tests = new List<object>();
            int count = 0;
            foreach (TestCoversMethod method in input.AddedTests)
            {
                if (method == null) continue;
                if (count >= _MaxTests) break;
                tests.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = method.Name,
                    ["body"] = method.Body
                });
                count++;
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["symptom"] = input.Symptom,
                ["added_tests"] = tests
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            for (int i = 1; i <= _MaxTests; i++)
            {
                string slot = i.ToString(CultureInfo.InvariantCulture);
                string prefix = "Added test number " + slot + " (see added_tests in the state, in order). If the state lists fewer than "
                    + _MaxTests + " tests and this slot has none, answer at the pole that is NOT a concern (covers_symptom and "
                    + "would_fail_before_fix true; asserts_source_text false). ";
                questions["covers_symptom_" + slot] = new NoulQuestion(
                    prefix + "This test exercises the reported symptom (see symptom in the state), not an unrelated behaviour.",
                    TrueMeaning: "The test covers the reported symptom.",
                    FalseMeaning: "The test does not cover the reported symptom.");
                questions["asserts_source_text_" + slot] = new NoulQuestion(
                    prefix + "This test only asserts that some SOURCE TEXT matches a copy (it reads a rule's text), rather than asserting a BEHAVIOUR.",
                    TrueMeaning: "The test asserts source text, so it proves only that one copy matches.",
                    FalseMeaning: "The test asserts a behaviour, not source text.");
                questions["would_fail_before_fix_" + slot] = new NoulQuestion(
                    prefix + "This test would FAIL against the code BEFORE the change and PASS after it, so its example value distinguishes the fix from the defect.",
                    TrueMeaning: "The test would fail before the fix and pass after it.",
                    FalseMeaning: "The test would pass even before the fix, so it proves nothing about the change.");
            }

            return questions;
        }

        /// <inheritdoc />
        protected override TestCoversReading Interpret(TypedDecisionResult result)
        {
            List<string> instructions = new List<string>();
            double strongestConcern = 0.0;

            for (int i = 1; i <= _MaxTests; i++)
            {
                string slot = i.ToString(CultureInfo.InvariantCulture);
                if (!result.Answers.ContainsKey("covers_symptom_" + slot)
                    && !result.Answers.ContainsKey("would_fail_before_fix_" + slot)
                    && !result.Answers.ContainsKey("asserts_source_text_" + slot))
                {
                    continue;
                }

                double coversSymptom = TypedAnswerReader.ReadNoul(result, "covers_symptom_" + slot, 1.0);
                double assertsSourceText = TypedAnswerReader.ReadNoul(result, "asserts_source_text_" + slot, 0.0);
                double wouldFailBefore = TypedAnswerReader.ReadNoul(result, "would_fail_before_fix_" + slot, 1.0);

                // A concern is the strongest of: the test does not cover the symptom (1 - covers), it
                // only asserts source text, or it would not fail before the fix (1 - would_fail). Each is
                // a memory discriminator for a test that proves nothing about the change.
                double concern = Math.Max(1.0 - coversSymptom, Math.Max(assertsSourceText, 1.0 - wouldFailBefore));
                if (concern >= _ConcernFloor)
                {
                    if (concern > strongestConcern) strongestConcern = concern;
                    instructions.Add(BuildInstruction(i, coversSymptom, assertsSourceText, wouldFailBefore));
                }
            }

            string label = instructions.Count == 0
                ? "tests_cover_symptom"
                : "doubted_tests:" + instructions.Count.ToString(CultureInfo.InvariantCulture);
            return new TestCoversReading(strongestConcern, instructions, label);
        }

        /// <inheritdoc />
        protected override TestCoversVerdict Combine(TestCoversVerdict ruleVerdict, TestCoversReading model)
        {
            // Combine runs only when the model gated at or above threshold: at least one added test the
            // model doubts strongly. The gated action only ADDS Judge instructions to the next brief; it
            // never fails the stage, so this only ever makes the review more careful, never blocks it.
            if (model.DoubtedInstructions.Count == 0) return ruleVerdict;
            return TestCoversVerdict.WithInstructions(model.DoubtedInstructions);
        }

        /// <inheritdoc />
        protected override string RuleLabel(TestCoversVerdict ruleVerdict) => ruleVerdict.OutcomeLabel;

        /// <inheritdoc />
        protected override Mission? MissionOf(TestCoversDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static string BuildInstruction(int index, double coversSymptom, double assertsSourceText, double wouldFailBefore)
        {
            string slot = index.ToString(CultureInfo.InvariantCulture);
            List<string> reasons = new List<string>();
            if (wouldFailBefore < _ConcernFloor) reasons.Add("it may pass even without the change");
            if (coversSymptom < _ConcernFloor) reasons.Add("it may not cover the reported symptom");
            if (assertsSourceText >= _ConcernFloor) reasons.Add("it may only assert source text, proving one copy matches");
            string why = reasons.Count == 0 ? "its coverage is uncertain" : String.Join("; ", reasons);
            return "Verify added test number " + slot + " (see added_tests in the TestEngineer output) actually fails against the code "
                + "WITHOUT the change and passes with it: " + why + ". Treat a test that stays green without the change as a finding.";
        }

        #endregion
    }
}
