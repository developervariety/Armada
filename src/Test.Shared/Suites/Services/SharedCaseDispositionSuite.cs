namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Behaviour of <see cref="SharedCaseDispositions.Apply"/>: a recorded case becomes a named skip with its
    /// reason, and a stale record fails discovery instead of hiding or silently dropping a case.
    /// </summary>
    public sealed class SharedCaseDispositionSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.SharedCaseDisposition";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the shared case disposition suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("apply_marks_recorded_case_skipped_with_reason", "Apply marks a recorded case skipped with its reason and leaves others unchanged", TestTags.Positive, () =>
            {
                IReadOnlyList<TestSuiteDescriptor> applied = SharedCaseDispositions.Apply(
                    Suites(),
                    new List<SharedCaseDisposition> { SharedCaseDisposition.AwaitingOwner("Fixture.Suite.recorded", "the behaviour is not implemented") },
                    null);

                TestCaseDescriptor recorded = applied[0].Cases.Single(c => c.CaseId == "recorded");
                TestCaseDescriptor other = applied[0].Cases.Single(c => c.CaseId == "other");
                AssertTrue(recorded.Skip, "recorded case should be skipped");
                AssertContains("Awaiting owner decision: the behaviour is not implemented", recorded.SkipReason ?? String.Empty);
                AssertFalse(other.Skip, "unrecorded case should still execute");
                AssertEqual(2, applied[0].Cases.Count, "no case should be dropped");
            }));

            cases.Add(Case("apply_rejects_record_naming_no_case", "Apply rejects a record that names no discovered case", TestTags.Negative, () =>
            {
                string message = CaptureInvalidOperation(() => SharedCaseDispositions.Apply(
                    Suites(),
                    new List<SharedCaseDisposition> { SharedCaseDisposition.AwaitingOwner("Fixture.Suite.renamed", "the behaviour is not implemented") },
                    null));
                AssertContains("Fixture.Suite.renamed names no discovered case", message);
            }));

            cases.Add(Case("apply_reports_fork_difference_distinctly_from_owner_decision", "Apply reports an intentional fork difference with its own prefix, not as an owner decision", TestTags.Positive, () =>
            {
                IReadOnlyList<TestSuiteDescriptor> applied = SharedCaseDispositions.Apply(
                    Suites(),
                    new List<SharedCaseDisposition> { SharedCaseDisposition.ForkDifference("Fixture.Suite.recorded", "the fork keeps its own behaviour") },
                    null);

                TestCaseDescriptor recorded = applied[0].Cases.Single(c => c.CaseId == "recorded");
                AssertTrue(recorded.Skip, "fork difference should be skipped");
                AssertContains("Intentional fork difference: the fork keeps its own behaviour", recorded.SkipReason ?? String.Empty);
                AssertFalse((recorded.SkipReason ?? String.Empty).Contains("Awaiting owner decision"), "a decided fork difference must not read as pending");
            }));

            cases.Add(Case("apply_rejects_fork_difference_naming_no_case", "Apply rejects an intentional fork difference that names no discovered case", TestTags.Negative, () =>
            {
                string message = CaptureInvalidOperation(() => SharedCaseDispositions.Apply(
                    Suites(),
                    new List<SharedCaseDisposition> { SharedCaseDisposition.ForkDifference("Fixture.Suite.renamed", "the fork keeps its own behaviour") },
                    null));
                AssertContains("Fixture.Suite.renamed names no discovered case", message);
            }));

            cases.Add(Case("apply_rejects_duplicate_whose_legacy_owner_is_missing", "Apply rejects a duplicate whose legacy owner case is not registered", TestTags.Negative, () =>
            {
                string root = CreateOwnerRoot("await RunTest(\"Some Other Case\", async () => { });");
                try
                {
                    string message = CaptureInvalidOperation(() => SharedCaseDispositions.Apply(
                        Suites(),
                        new List<SharedCaseDisposition> { SharedCaseDisposition.DuplicateOf("Fixture.Suite.recorded", "legacy/OwnerTests.cs", "Owner Case", "the legacy case carries the fix") },
                        root));
                    AssertContains("names legacy owner case 'Owner Case'", message);
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            }));

            cases.Add(Case("apply_accepts_duplicate_whose_legacy_owner_exists", "Apply accepts a duplicate whose legacy owner case is registered and names it in the skip reason", TestTags.Positive, () =>
            {
                string root = CreateOwnerRoot("await RunTest(\"Owner Case\", async () => { });");
                try
                {
                    IReadOnlyList<TestSuiteDescriptor> applied = SharedCaseDispositions.Apply(
                        Suites(),
                        new List<SharedCaseDisposition> { SharedCaseDisposition.DuplicateOf("Fixture.Suite.recorded", "legacy/OwnerTests.cs", "Owner Case", "the legacy case carries the fix") },
                        root);
                    TestCaseDescriptor recorded = applied[0].Cases.Single(c => c.CaseId == "recorded");
                    AssertTrue(recorded.Skip, "duplicate should be skipped");
                    AssertContains("Duplicate of executed legacy case 'Owner Case' (legacy/OwnerTests.cs)", recorded.SkipReason ?? String.Empty);
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Shared Case Dispositions",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static IReadOnlyList<TestSuiteDescriptor> Suites()
        {
            List<TestCaseDescriptor> fixtureCases = new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor(suiteId: "Fixture.Suite", caseId: "recorded", displayName: "Recorded", executeAsync: (CancellationToken ct) => Task.CompletedTask, tags: new List<string> { TestTags.Positive }),
                new TestCaseDescriptor(suiteId: "Fixture.Suite", caseId: "other", displayName: "Other", executeAsync: (CancellationToken ct) => Task.CompletedTask, tags: new List<string> { TestTags.Positive })
            };
            return new List<TestSuiteDescriptor> { new TestSuiteDescriptor(suiteId: "Fixture.Suite", displayName: "Fixture", cases: fixtureCases) };
        }

        private static string CreateOwnerRoot(string ownerSource)
        {
            string root = TestTemp.NewDirectory("dispositions");
            Directory.CreateDirectory(Path.Combine(root, "legacy"));
            File.WriteAllText(Path.Combine(root, "legacy", "OwnerTests.cs"), ownerSource);
            return root;
        }

        private static string CaptureInvalidOperation(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }

            throw new AssertionException("Expected InvalidOperationException but no exception was thrown");
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
