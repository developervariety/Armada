namespace Armada.Test.Unit.Suites.Recovery
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for the pure <see cref="MergeFailureClassifier"/>: covers every
    /// <see cref="MergeFailureClassEnum"/> value plus the Unknown fallback,
    /// conflict-file deduplication, and the 200-character summary cap.
    /// </summary>
    public class MergeFailureClassifierTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Merge Failure Classifier";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Classify_GitConflictMarker_ReturnsTextConflict", async () =>
            {
                MergeFailureClassifier classifier = new MergeFailureClassifier();
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = 1,
                    GitStandardOutput = "Auto-merging src/Foo.cs\nCONFLICT (content): Merge conflict in src/Foo.cs",
                    GitStandardError = "Automatic merge failed; fix conflicts and then commit the result.",
                    ConflictedFiles = new List<string> { "src/Foo.cs" },
                    DiffLineCount = 17
                };

                MergeFailureClassification result = classifier.Classify(context);

                AssertEqual(MergeFailureClassEnum.TextConflict, result.FailureClass, "should classify as TextConflict");
                AssertEqual(1, result.ConflictedFiles.Count, "single conflicted file expected");
                AssertEqual("src/Foo.cs", result.ConflictedFiles[0], "conflicted file path should be preserved");
                AssertContains("1 file", result.Summary, "summary should mention 1 file");
                await Task.CompletedTask;
            });

            await RunTest("Classify_NonFastForwardError_ReturnsStaleBase", async () =>
            {
                MergeFailureClassifier classifier = new MergeFailureClassifier();
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = 1,
                    GitStandardOutput = "",
                    GitStandardError = "! [rejected]        feature -> feature (non-fast-forward)\nhint: Updates were rejected because the tip of your current branch is behind",
                    ConflictedFiles = new List<string>(),
                    DiffLineCount = 5
                };

                MergeFailureClassification result = classifier.Classify(context);

                AssertEqual(MergeFailureClassEnum.StaleBase, result.FailureClass, "should classify as StaleBase");
                AssertEqual(0, result.ConflictedFiles.Count, "no conflicted files expected");
                AssertContains("Stale base", result.Summary, "summary should reference stale base");
                await Task.CompletedTask;
            });

            await RunTest("Classify_TestFailureWithSuccessfulMerge_ReturnsTestFailureAfterMerge", async () =>
            {
                MergeFailureClassifier classifier = new MergeFailureClassifier();
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = 0,
                    GitStandardOutput = "Merge made by the 'recursive' strategy.",
                    GitStandardError = "",
                    TestExitCode = 1,
                    TestOutput = "FAIL: 3 tests failed",
                    ConflictedFiles = new List<string>(),
                    DiffLineCount = 42
                };

                MergeFailureClassification result = classifier.Classify(context);

                AssertEqual(MergeFailureClassEnum.TestFailureAfterMerge, result.FailureClass,
                    "should classify as TestFailureAfterMerge");
                AssertContains("after merge", result.Summary, "summary should reference after-merge phase");
                await Task.CompletedTask;
            });

            await RunTest("Classify_TestFailureWithoutMergeAttempt_ReturnsTestFailureBeforeMerge", async () =>
            {
                MergeFailureClassifier classifier = new MergeFailureClassifier();
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = null,
                    GitStandardOutput = null,
                    GitStandardError = null,
                    TestExitCode = 2,
                    TestOutput = "Compilation failed",
                    ConflictedFiles = new List<string>(),
                    DiffLineCount = 0
                };

                MergeFailureClassification result = classifier.Classify(context);

                AssertEqual(MergeFailureClassEnum.TestFailureBeforeMerge, result.FailureClass,
                    "should classify as TestFailureBeforeMerge");
                AssertContains("before merge", result.Summary, "summary should reference before-merge phase");
                await Task.CompletedTask;
            });

            // The merge queue asks the definition-of-done classifier what host trouble looks like, so a
            // host fault is surfaced instead of spending a recovery captain that would fail the same way.
            await RunTest("Classify_HostFaultTestOutput_AgreesWithTheGateClassifierAndSurfaces", async () =>
            {
                string[] outputs =
                {
                    "error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.",
                    "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?\nFailed: 12, Passed: 300",
                    "The active test run was aborted. Reason: Test host process crashed",
                    "dotnet: command not found",
                    "FAIL: 3 tests failed",
                    "  Failed Api.Routes.DeleteVessel_AsViewer [12 ms]\nFailed: 1, Passed: 812",
                    "Parser.cs(12,5): error CS0103: The name 'x' does not exist in the current context"
                };

                MergeFailureClassifier classifier = new MergeFailureClassifier();
                Armada.Core.Services.DefinitionOfDoneFailureClassifier gate = new Armada.Core.Services.DefinitionOfDoneFailureClassifier();
                RecoveryRouter router = new RecoveryRouter();
                foreach (int? gitExit in new int?[] { 0, null })
                {
                    foreach (string output in outputs)
                    {
                        MergeFailureContext context = new MergeFailureContext
                        {
                            GitExitCode = gitExit,
                            TestExitCode = 1,
                            TestOutput = output,
                            ConflictedFiles = new List<string>()
                        };

                        MergeFailureClassification result = classifier.Classify(context);
                        bool gateSaysHost = gate.Classify("unit-test", 1, output) == DefinitionOfDoneFailureClassEnum.Infra;
                        AssertEqual(gateSaysHost, result.FailureClass == MergeFailureClassEnum.InfraTestFailure,
                            "the merge queue and the gate classifier must agree on host trouble (git exit " + gitExit + "): " + output);

                        RecoveryAction action = router.Route(result.FailureClass, false, 0);
                        if (gateSaysHost)
                        {
                            AssertTrue(action is RecoveryAction.Surface, "a host fault must be surfaced, not sent to a captain: " + output);
                        }
                    }
                }
                await Task.CompletedTask;
            });

            await RunTest("Classify_NoSignals_ReturnsUnknown", async () =>
            {
                MergeFailureClassifier classifier = new MergeFailureClassifier();
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = null,
                    GitStandardOutput = null,
                    GitStandardError = null,
                    TestExitCode = null,
                    TestOutput = null,
                    ConflictedFiles = new List<string>(),
                    DiffLineCount = 0
                };

                MergeFailureClassification result = classifier.Classify(context);

                AssertEqual(MergeFailureClassEnum.Unknown, result.FailureClass, "should fall back to Unknown");
                AssertContains("Unclassified", result.Summary, "summary should mark as unclassified");
                await Task.CompletedTask;
            });

            await RunTest("Classify_UnparseableGitOutput_ReturnsUnknown", async () =>
            {
                MergeFailureClassifier classifier = new MergeFailureClassifier();
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = 128,
                    GitStandardOutput = "fatal: some unexpected git error",
                    GitStandardError = "fatal: lorem ipsum",
                    TestExitCode = null,
                    ConflictedFiles = new List<string>(),
                    DiffLineCount = 12
                };

                MergeFailureClassification result = classifier.Classify(context);

                AssertEqual(MergeFailureClassEnum.Unknown, result.FailureClass,
                    "unparseable git output without conflict markers should be Unknown");
                AssertContains("git exit=128", result.Summary, "summary should expose git exit code");
                await Task.CompletedTask;
            });

            await RunTest("Classify_ConflictedFilesProvidedWithoutMarker_StillTextConflict", async () =>
            {
                MergeFailureClassifier classifier = new MergeFailureClassifier();
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = 1,
                    GitStandardOutput = "",
                    GitStandardError = "",
                    ConflictedFiles = new List<string> { "a.cs", "b.cs", "a.cs" },
                    DiffLineCount = 99
                };

                MergeFailureClassification result = classifier.Classify(context);

                AssertEqual(MergeFailureClassEnum.TextConflict, result.FailureClass,
                    "non-empty conflicted-files list implies TextConflict");
                AssertEqual(2, result.ConflictedFiles.Count, "duplicates should be removed");
                AssertContains("2 files", result.Summary, "summary should mention 2 files");
                await Task.CompletedTask;
            });

            await RunTest("Classify_LongInputs_SummaryRespects200CharCap", async () =>
            {
                MergeFailureClassifier classifier = new MergeFailureClassifier();
                List<string> manyFiles = new List<string>();
                for (int i = 0; i < 500; i++) manyFiles.Add("src/File" + i + ".cs");
                MergeFailureContext context = new MergeFailureContext
                {
                    GitExitCode = 1,
                    GitStandardOutput = new string('x', 8192) + " CONFLICT (content): collision",
                    GitStandardError = new string('y', 8192),
                    ConflictedFiles = manyFiles,
                    DiffLineCount = 7
                };

                MergeFailureClassification result = classifier.Classify(context);

                AssertTrue(result.Summary.Length <= 200, "summary must be capped at 200 chars (was " + result.Summary.Length + ")");
                AssertEqual(500, result.ConflictedFiles.Count, "all unique files must be preserved");
                await Task.CompletedTask;
            });
        }
    }
}
