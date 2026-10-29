namespace Test.Shared.Suites.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="MonotonicDeadline"/>. The deadline passes only after its timeout of
    /// monotonic time, and a negative timeout is already passed.
    /// </summary>
    public sealed class MonotonicDeadlineSuite : IArmadaTestSuite
    {
        /// <summary>Build the suite.</summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();
            cases.Add(Case("deadline_passes_after_its_timeout", "A deadline stays open until its timeout elapses", TestTags.Positive, () =>
            {
                MonotonicDeadline open = MonotonicDeadline.After(TimeSpan.FromMilliseconds(200));
                AssertFalse(open.Passed, "a fresh deadline has not passed");
                AssertTrue(open.Remaining > TimeSpan.Zero, "a fresh deadline has time left");
                Thread.Sleep(250);
                AssertTrue(open.Passed, "the deadline passes after the timeout");
                AssertEqual(TimeSpan.Zero, open.Remaining, "no time remains after the deadline");
            }));
            cases.Add(Case("negative_timeout_is_already_passed", "A negative timeout is treated as zero", TestTags.Negative, () =>
            {
                MonotonicDeadline passed = MonotonicDeadline.After(TimeSpan.FromMilliseconds(-1));
                AssertTrue(passed.Passed, "a negative timeout has already passed");
                AssertEqual(TimeSpan.Zero, passed.Remaining, "a passed deadline reports no time left");
            }));
            return new TestSuiteDescriptor(
                suiteId: "Infrastructure.MonotonicDeadline",
                displayName: "Monotonic Deadline",
                cases: cases);
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Infrastructure.MonotonicDeadline",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken _) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }
    }
}
