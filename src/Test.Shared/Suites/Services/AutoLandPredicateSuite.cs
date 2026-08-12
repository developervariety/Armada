namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="AutoLandPredicate"/>. Positive cases confirm a small in-scope change
    /// lands and a disabled policy imposes no hold; negative cases confirm the file/line thresholds, the
    /// deny globs, the allow-list, and an empty diff each hold for review.
    /// </summary>
    public sealed class AutoLandPredicateSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the auto-land-predicate suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("disabled_policy_lands", "A disabled policy imposes no hold", TestTags.Positive, () =>
            {
                AutoLandPolicy policy = new AutoLandPolicy { Enabled = false };
                AutoLandDecision d = AutoLandPredicate.Evaluate(999, 999999, new List<string> { "infra/prod.tf" }, policy);
                AssertTrue(d.Land, "disabled policy should land");
            }));

            cases.Add(Case("small_in_scope_change_lands", "Under-threshold, allowed-path change lands", TestTags.Positive, () =>
            {
                AutoLandPolicy policy = new AutoLandPolicy { Enabled = true, MaxFiles = 10, MaxLines = 400, PathAllowGlobs = new List<string> { "src/**" } };
                AutoLandDecision d = AutoLandPredicate.Evaluate(3, 120, new List<string> { "src/api/users.ts", "src/api/orders.ts" }, policy);
                AssertTrue(d.Land, "expected land");
                AssertNull(d.HoldReason);
            }));

            cases.Add(Case("over_max_files_holds", "Over the file limit holds", TestTags.Negative, () =>
            {
                AutoLandPolicy policy = new AutoLandPolicy { Enabled = true, MaxFiles = 5 };
                AutoLandDecision d = AutoLandPredicate.Evaluate(40, 100, new List<string> { "a.cs" }, policy);
                AssertFalse(d.Land, "expected hold");
                AssertNotNull(d.HoldReason);
            }));

            cases.Add(Case("over_max_lines_holds", "Over the line limit holds", TestTags.Negative, () =>
            {
                AutoLandPolicy policy = new AutoLandPolicy { Enabled = true, MaxLines = 200 };
                AutoLandDecision d = AutoLandPredicate.Evaluate(2, 5000, new List<string> { "a.cs" }, policy);
                AssertFalse(d.Land, "expected hold");
            }));

            cases.Add(Case("deny_glob_holds", "A deny-glob match holds even under thresholds", TestTags.Negative, () =>
            {
                AutoLandPolicy policy = new AutoLandPolicy { Enabled = true, MaxFiles = 10, MaxLines = 400, PathDenyGlobs = new List<string> { "infra/**", ".env*" } };
                AutoLandDecision d = AutoLandPredicate.Evaluate(1, 5, new List<string> { "infra/prod.tf" }, policy);
                AssertFalse(d.Land, "expected hold");
            }));

            cases.Add(Case("outside_allow_list_holds", "A path outside the allow-list holds", TestTags.Negative, () =>
            {
                AutoLandPolicy policy = new AutoLandPolicy { Enabled = true, PathAllowGlobs = new List<string> { "src/**" } };
                AutoLandDecision d = AutoLandPredicate.Evaluate(2, 10, new List<string> { "src/ok.ts", "docs/readme.md" }, policy);
                AssertFalse(d.Land, "expected hold");
            }));

            cases.Add(Case("empty_diff_holds", "An empty diff is a no-op that does not auto-land", TestTags.Negative, () =>
            {
                AutoLandPolicy policy = new AutoLandPolicy { Enabled = true, MaxFiles = 10 };
                AutoLandDecision d = AutoLandPredicate.Evaluate(0, 0, new List<string>(), policy);
                AssertFalse(d.Land, "empty diff should hold");
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.AutoLandPredicate",
                displayName: "AutoLand Predicate",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.AutoLandPredicate",
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
