namespace Armada.Test.Unit.Suites.Recovery
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Test.Common;

    /// <summary>
    /// Establishes what MergeFailureClassifier actually does with the git output shapes the merge
    /// queue captures at fail time. Written because every Failed merge entry in the 2026-07-28 to
    /// 2026-08-04 window recorded MergeFailureClass Unknown, including entries whose TestOutput
    /// read "Merge conflict with main" -- which looked like the classifier ignoring its input.
    /// These tests separate the two candidate causes: a classifier that cannot recognise a real
    /// conflict, versus entries that were never content conflicts and were only labelled that way
    /// by the pre-723dbcf1 merge path. The conflicted-file list is deliberately empty throughout,
    /// because <c>merge --abort</c> resets the worktree before the list is collected.
    /// </summary>
    public class MergeFailureClassifierGitOutputTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Merge Failure Classifier Git Output";

        // Real `git merge --no-ff` output for a content conflict. Git writes both lines to stdout.
        private const string _ContentConflictStdout =
            "Auto-merging src/Feature/Thing.cs\n" +
            "CONFLICT (content): Merge conflict in src/Feature/Thing.cs\n" +
            "Automatic merge failed; fix conflicts and then commit the result.\n";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Real git conflict output classifies as TextConflict with no conflicted-file list", () =>
            {
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = 1,
                    GitStandardOutput = _ContentConflictStdout,
                    GitStandardError = string.Empty,
                    ConflictedFiles = new List<string>(),
                    DiffLineCount = 12
                };

                MergeFailureClassifier classifier = new MergeFailureClassifier();
                MergeFailureClassification classification = classifier.Classify(context);

                AssertEqual(MergeFailureClassEnum.TextConflict, classification.FailureClass, "FailureClass");
            }).ConfigureAwait(false);

            await RunTest("Non-fast-forward push rejection classifies as StaleBase", () =>
            {
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = 1,
                    GitStandardOutput = string.Empty,
                    GitStandardError =
                        " ! [rejected]        main -> main (non-fast-forward)\n" +
                        "error: failed to push some refs\n",
                    ConflictedFiles = new List<string>(),
                    DiffLineCount = 0
                };

                MergeFailureClassifier classifier = new MergeFailureClassifier();
                MergeFailureClassification classification = classifier.Classify(context);

                AssertEqual(MergeFailureClassEnum.StaleBase, classification.FailureClass, "FailureClass");
            }).ConfigureAwait(false);

            // The genuine remaining gap. The merge queue consumes the source branch at enqueue, so
            // an entry whose branch no longer resolves is a routine, well-understood outcome -- but
            // it lands in Unknown, which routes to Surface("classification_unknown") and gives
            // triage no signal. Pinned as current behaviour so a fix has something to change.
            await RunTest("Branch that resolves to no ref is currently Unknown", () =>
            {
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = 1,
                    GitStandardOutput = string.Empty,
                    GitStandardError = "branch armada/captain-1/msn_example resolves to no local or remote-tracking ref",
                    ConflictedFiles = new List<string>(),
                    DiffLineCount = 0
                };

                MergeFailureClassifier classifier = new MergeFailureClassifier();
                MergeFailureClassification classification = classifier.Classify(context);

                AssertEqual(MergeFailureClassEnum.Unknown, classification.FailureClass, "FailureClass");
            }).ConfigureAwait(false);
        }
    }
}
