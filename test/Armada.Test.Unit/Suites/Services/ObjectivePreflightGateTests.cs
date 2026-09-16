namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Covers the dispatch-preflight blocking rule and the operator gate that decides when a force flag
    /// may override an incomplete preflight.
    /// </summary>
    public sealed class ObjectivePreflightGateTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Objective Preflight Gate";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("No recorded answer blocks every question", () =>
            {
                IReadOnlyList<int> blocking = ObjectivePreflightEvaluator.BlockingQuestions(new ObjectivePreflight());
                AssertEqual(ObjectivePreflight.QuestionCount, blocking.Count, "every unanswered question blocks");
                AssertFalse(ObjectivePreflightEvaluator.IsComplete(new ObjectivePreflight()), "no answers means incomplete");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A complete preflight blocks nothing", () =>
            {
                AssertTrue(ObjectivePreflightEvaluator.IsComplete(PreflightTestData.Complete()), "1-12 yes and 13 no is complete");
                AssertEqual(0, ObjectivePreflightEvaluator.BlockingQuestions(PreflightTestData.Complete()).Count, "a complete preflight blocks nothing");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A no on 1-12 and a yes on 13 each block", () =>
            {
                ObjectivePreflight preflight = PreflightTestData.Complete();
                preflight.Questions.First(question => question.Number == 7).Answer = ObjectivePreflightAnswerEnum.No;
                preflight.Questions.First(question => question.Number == ObjectivePreflight.OwnerQuestionNumber).Answer = ObjectivePreflightAnswerEnum.Yes;
                List<int> blocking = ObjectivePreflightEvaluator.BlockingQuestions(preflight).ToList();
                AssertTrue(blocking.Contains(7), "a no on question 7 blocks");
                AssertTrue(blocking.Contains(ObjectivePreflight.OwnerQuestionNumber), "a yes on the owner question blocks");
                AssertEqual(2, blocking.Count, "no other question blocks");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A yes on 1-12 and a no on 13 admit dispatch", () =>
            {
                ObjectivePreflight preflight = new ObjectivePreflight();
                for (int number = 1; number <= ObjectivePreflight.QuestionCount; number++)
                {
                    preflight.Questions.Add(new ObjectivePreflightAnswer
                    {
                        Number = number,
                        Answer = number == ObjectivePreflight.OwnerQuestionNumber
                            ? ObjectivePreflightAnswerEnum.No
                            : ObjectivePreflightAnswerEnum.Yes
                    });
                }
                AssertTrue(ObjectivePreflightEvaluator.IsComplete(preflight), "1-12 yes and 13 no admits dispatch");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The gate overrides only an incomplete preflight", () =>
            {
                ObjectiveDispatchPreview ready = new ObjectiveDispatchPreview { IsReady = true };
                AssertEqual(PreflightGateOutcomeEnum.Ready, ObjectivePreflightGate.Classify(ready, false), "a ready preview is ready");

                ObjectiveDispatchPreview preflightOnly = PreviewWith(ObjectivePreflightGate.IssueCode);
                AssertEqual(PreflightGateOutcomeEnum.BlockedByPreflight, ObjectivePreflightGate.Classify(preflightOnly, false),
                    "an incomplete preflight with no force is refused");
                AssertEqual(PreflightGateOutcomeEnum.OverriddenPreflight, ObjectivePreflightGate.Classify(preflightOnly, true),
                    "a force flag overrides an incomplete preflight");

                ObjectiveDispatchPreview otherOnly = PreviewWith("brief_acceptance_missing");
                AssertEqual(PreflightGateOutcomeEnum.BlockedByOther, ObjectivePreflightGate.Classify(otherOnly, true),
                    "a force flag does not override an unrelated blocking issue");

                ObjectiveDispatchPreview both = PreviewWith(ObjectivePreflightGate.IssueCode, "brief_acceptance_missing");
                AssertEqual(PreflightGateOutcomeEnum.BlockedByOther, ObjectivePreflightGate.Classify(both, true),
                    "a force flag cannot dispatch past a second blocking issue");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The gate treats a preflight model flag as preflight-class", () =>
            {
                ObjectiveDispatchPreview modelFlagOnly = PreviewWith(PreflightTextAdapter.ModelFlagIssueCode);
                AssertEqual(PreflightGateOutcomeEnum.BlockedByPreflight, ObjectivePreflightGate.Classify(modelFlagOnly, false),
                    "a model flag with no force is refused as a preflight block");
                AssertEqual(PreflightGateOutcomeEnum.OverriddenPreflight, ObjectivePreflightGate.Classify(modelFlagOnly, true),
                    "a force flag overrides a model flag");

                ObjectiveDispatchPreview flagAndIncomplete = PreviewWith(PreflightTextAdapter.ModelFlagIssueCode, ObjectivePreflightGate.IssueCode);
                AssertEqual(PreflightGateOutcomeEnum.OverriddenPreflight, ObjectivePreflightGate.Classify(flagAndIncomplete, true),
                    "a force flag overrides a model flag together with an incomplete preflight");

                ObjectiveDispatchPreview flagAndOther = PreviewWith(PreflightTextAdapter.ModelFlagIssueCode, "brief_acceptance_missing");
                AssertEqual(PreflightGateOutcomeEnum.BlockedByOther, ObjectivePreflightGate.Classify(flagAndOther, true),
                    "a force flag cannot dispatch past a non-preflight blocking issue beside a model flag");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        private static ObjectiveDispatchPreview PreviewWith(params string[] errorCodes)
        {
            ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview();
            foreach (string code in errorCodes)
            {
                preview.Issues.Add(new ObjectiveDispatchPreviewIssue
                {
                    Code = code,
                    Severity = ReadinessSeverityEnum.Error
                });
            }
            return preview;
        }
    }
}
