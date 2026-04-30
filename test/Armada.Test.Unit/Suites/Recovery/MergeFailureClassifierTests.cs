namespace Armada.Test.Unit.Suites.Recovery
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Recovery;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for <see cref="MergeFailureClassifier"/>. Pure-function coverage
    /// across every <see cref="MergeFailureClass"/> value plus the conservative
    /// <c>Unknown</c> fallback. No I/O, no fixtures on disk; each test composes
    /// the (git, tests, conflicts) snapshot inline.
    /// </summary>
    public class MergeFailureClassifierTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Merge Failure Classifier";

        private static readonly MergeFailureClassifier Classifier = new MergeFailureClassifier();

        /// <summary>Run all classifier cases.</summary>
        protected override async Task RunTestsAsync()
        {
            await Classify_MergeFailedNoConflictFiles_ReturnsStaleBase();
            await Classify_MergeFailedNoConflictsButTestsRan_DoesNotReturnStaleBase();
            await Classify_MergeFailedWithConflicts_ReturnsTextConflict();
            await Classify_TextConflict_PreservesConflictedFilesList();
            await Classify_TextConflict_SummaryIncludesFilePreview();
            await Classify_CleanMergeAndTestsFailed_ReturnsTestFailureAfterMerge();
            await Classify_FailedMergeAndTestsFailed_ReturnsTestFailureBeforeMerge();
            await Classify_MergeNotAttempted_ReturnsUnknown();
            await Classify_NoFailureSignals_ReturnsUnknown();
            await Classify_ConflictsPresentEvenWithMergeSucceeded_FollowsTextConflictRule();
            await Classify_NullConflictedFiles_TreatedAsEmpty();
            await Classify_TestsPassedExitZero_ReturnsUnknownNotTestFailure();
            await Classify_NullGitOutcome_ThrowsArgumentNullException();
            await Classify_LargeConflictList_SummaryStaysUnder512Chars();
        }

        private async Task Classify_MergeFailedNoConflictFiles_ReturnsStaleBase()
        {
            await RunTest("Classify_MergeFailedNoConflictFiles_ReturnsStaleBase", () =>
            {
                GitMergeOutcome git = new GitMergeOutcome(
                    MergeAttempted: true,
                    MergeSucceeded: false,
                    ConflictedFiles: Array.Empty<string>(),
                    MergeBaseSha: "abc123",
                    TargetTipSha: "def456",
                    MergeOutput: "fatal: Not possible to fast-forward, aborting.");

                MergeFailureSignal signal = Classifier.Classify(git, null, Array.Empty<string>());

                AssertEqual((int)MergeFailureClass.StaleBase, (int)signal.Class, "should classify as StaleBase");
                AssertEqual(0, signal.ConflictedFiles.Count);
                AssertContains("stale base", signal.Summary.ToLowerInvariant());
                return Task.CompletedTask;
            });
        }

        private async Task Classify_MergeFailedNoConflictsButTestsRan_DoesNotReturnStaleBase()
        {
            await RunTest("Classify_MergeFailedNoConflictsButTestsRan_DoesNotReturnStaleBase", () =>
            {
                GitMergeOutcome git = new GitMergeOutcome(
                    MergeAttempted: true,
                    MergeSucceeded: false,
                    ConflictedFiles: Array.Empty<string>(),
                    MergeBaseSha: null,
                    TargetTipSha: null,
                    MergeOutput: "");
                TestRunOutcome tests = new TestRunOutcome(1, "tests blew up", false);

                MergeFailureSignal signal = Classifier.Classify(git, tests, Array.Empty<string>());

                AssertFalse(signal.Class == MergeFailureClass.StaleBase, "tests present should not match stale-base shape");
                AssertEqual((int)MergeFailureClass.TestFailureBeforeMerge, (int)signal.Class);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_MergeFailedWithConflicts_ReturnsTextConflict()
        {
            await RunTest("Classify_MergeFailedWithConflicts_ReturnsTextConflict", () =>
            {
                List<string> conflicts = new List<string> { "src/Foo.cs", "src/Bar.cs" };
                GitMergeOutcome git = new GitMergeOutcome(
                    MergeAttempted: true,
                    MergeSucceeded: false,
                    ConflictedFiles: conflicts,
                    MergeBaseSha: "abc",
                    TargetTipSha: "def",
                    MergeOutput: "Auto-merging src/Foo.cs\nCONFLICT (content): Merge conflict in src/Foo.cs");

                MergeFailureSignal signal = Classifier.Classify(git, null, conflicts);

                AssertEqual((int)MergeFailureClass.TextConflict, (int)signal.Class);
                AssertEqual(2, signal.ConflictedFiles.Count);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_TextConflict_PreservesConflictedFilesList()
        {
            await RunTest("Classify_TextConflict_PreservesConflictedFilesList", () =>
            {
                List<string> conflicts = new List<string> { "a.cs", "b.cs", "c.cs" };
                GitMergeOutcome git = new GitMergeOutcome(true, false, conflicts, null, null, "");

                MergeFailureSignal signal = Classifier.Classify(git, null, conflicts);

                AssertEqual(3, signal.ConflictedFiles.Count);
                AssertEqual("a.cs", signal.ConflictedFiles[0]);
                AssertEqual("b.cs", signal.ConflictedFiles[1]);
                AssertEqual("c.cs", signal.ConflictedFiles[2]);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_TextConflict_SummaryIncludesFilePreview()
        {
            await RunTest("Classify_TextConflict_SummaryIncludesFilePreview", () =>
            {
                List<string> conflicts = new List<string> { "src/Alpha.cs", "src/Beta.cs" };
                GitMergeOutcome git = new GitMergeOutcome(true, false, conflicts, null, null, "");

                MergeFailureSignal signal = Classifier.Classify(git, null, conflicts);

                AssertContains("Alpha.cs", signal.Summary);
                AssertContains("Beta.cs", signal.Summary);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_CleanMergeAndTestsFailed_ReturnsTestFailureAfterMerge()
        {
            await RunTest("Classify_CleanMergeAndTestsFailed_ReturnsTestFailureAfterMerge", () =>
            {
                GitMergeOutcome git = new GitMergeOutcome(
                    MergeAttempted: true,
                    MergeSucceeded: true,
                    ConflictedFiles: Array.Empty<string>(),
                    MergeBaseSha: "abc",
                    TargetTipSha: "def",
                    MergeOutput: "Merge made by the 'recursive' strategy.");
                TestRunOutcome tests = new TestRunOutcome(1, "FAIL: tests/Foo.Tests.cs", true);

                MergeFailureSignal signal = Classifier.Classify(git, tests, Array.Empty<string>());

                AssertEqual((int)MergeFailureClass.TestFailureAfterMerge, (int)signal.Class);
                AssertContains("after clean merge", signal.Summary);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_FailedMergeAndTestsFailed_ReturnsTestFailureBeforeMerge()
        {
            await RunTest("Classify_FailedMergeAndTestsFailed_ReturnsTestFailureBeforeMerge", () =>
            {
                GitMergeOutcome git = new GitMergeOutcome(
                    MergeAttempted: true,
                    MergeSucceeded: false,
                    ConflictedFiles: new List<string> { "src/X.cs" },
                    MergeBaseSha: "abc",
                    TargetTipSha: "def",
                    MergeOutput: "");
                TestRunOutcome tests = new TestRunOutcome(2, "captain produced broken work", false);

                MergeFailureSignal signal = Classifier.Classify(git, tests, new List<string> { "src/X.cs" });

                AssertEqual((int)MergeFailureClass.TestFailureBeforeMerge, (int)signal.Class);
                AssertContains("without a clean merge", signal.Summary);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_MergeNotAttempted_ReturnsUnknown()
        {
            await RunTest("Classify_MergeNotAttempted_ReturnsUnknown", () =>
            {
                GitMergeOutcome git = new GitMergeOutcome(
                    MergeAttempted: false,
                    MergeSucceeded: false,
                    ConflictedFiles: Array.Empty<string>(),
                    MergeBaseSha: null,
                    TargetTipSha: null,
                    MergeOutput: "fetch failed");

                MergeFailureSignal signal = Classifier.Classify(git, null, Array.Empty<string>());

                AssertEqual((int)MergeFailureClass.Unknown, (int)signal.Class);
                AssertContains("merge_not_attempted", signal.Summary);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_NoFailureSignals_ReturnsUnknown()
        {
            await RunTest("Classify_NoFailureSignals_ReturnsUnknown", () =>
            {
                GitMergeOutcome git = new GitMergeOutcome(true, true, Array.Empty<string>(), null, null, "");

                MergeFailureSignal signal = Classifier.Classify(git, null, Array.Empty<string>());

                AssertEqual((int)MergeFailureClass.Unknown, (int)signal.Class);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_ConflictsPresentEvenWithMergeSucceeded_FollowsTextConflictRule()
        {
            // Per the brief's rule order, conflictedFiles.Count > 0 with tests==null
            // routes to TextConflict regardless of GitMergeOutcome.MergeSucceeded.
            // The conflictedFiles list is the authoritative signal for the conflict
            // shape; this case verifies we obey the brief rather than second-guessing
            // a contradictory snapshot.
            await RunTest("Classify_ConflictsPresentEvenWithMergeSucceeded_FollowsTextConflictRule", () =>
            {
                List<string> conflicts = new List<string> { "src/Foo.cs" };
                GitMergeOutcome git = new GitMergeOutcome(true, true, conflicts, null, null, "");

                MergeFailureSignal signal = Classifier.Classify(git, null, conflicts);

                AssertEqual((int)MergeFailureClass.TextConflict, (int)signal.Class);
                AssertEqual(1, signal.ConflictedFiles.Count);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_NullConflictedFiles_TreatedAsEmpty()
        {
            await RunTest("Classify_NullConflictedFiles_TreatedAsEmpty", () =>
            {
                GitMergeOutcome git = new GitMergeOutcome(true, false, Array.Empty<string>(), null, null, "");

                MergeFailureSignal signal = Classifier.Classify(git, null, null!);

                AssertEqual((int)MergeFailureClass.StaleBase, (int)signal.Class);
                AssertEqual(0, signal.ConflictedFiles.Count);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_TestsPassedExitZero_ReturnsUnknownNotTestFailure()
        {
            await RunTest("Classify_TestsPassedExitZero_ReturnsUnknownNotTestFailure", () =>
            {
                GitMergeOutcome git = new GitMergeOutcome(true, true, Array.Empty<string>(), null, null, "");
                TestRunOutcome tests = new TestRunOutcome(0, "all green", true);

                MergeFailureSignal signal = Classifier.Classify(git, tests, Array.Empty<string>());

                AssertEqual((int)MergeFailureClass.Unknown, (int)signal.Class);
                return Task.CompletedTask;
            });
        }

        private async Task Classify_NullGitOutcome_ThrowsArgumentNullException()
        {
            await RunTest("Classify_NullGitOutcome_ThrowsArgumentNullException", () =>
            {
                bool threw = false;
                try
                {
                    Classifier.Classify(null!, null, Array.Empty<string>());
                }
                catch (ArgumentNullException)
                {
                    threw = true;
                }
                AssertTrue(threw, "should throw ArgumentNullException for null GitMergeOutcome");
                return Task.CompletedTask;
            });
        }

        private async Task Classify_LargeConflictList_SummaryStaysUnder512Chars()
        {
            await RunTest("Classify_LargeConflictList_SummaryStaysUnder512Chars", () =>
            {
                List<string> conflicts = new List<string>();
                for (int i = 0; i < 200; i++) conflicts.Add("src/Folder/VeryLongFileName_" + i + ".cs");
                GitMergeOutcome git = new GitMergeOutcome(true, false, conflicts, null, null, "");

                MergeFailureSignal signal = Classifier.Classify(git, null, conflicts);

                AssertEqual((int)MergeFailureClass.TextConflict, (int)signal.Class);
                AssertTrue(signal.Summary.Length <= 512, "summary should be capped at 512 chars; was " + signal.Summary.Length);
                AssertEqual(200, signal.ConflictedFiles.Count);
                return Task.CompletedTask;
            });
        }
    }
}
