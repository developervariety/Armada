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
    /// Descriptors for <see cref="GitAnchorsFormatter"/>. Positive cases confirm the anchors render into a
    /// readable section; negative cases confirm missing anchors yield an empty string and empty recent
    /// entries are skipped rather than emitted blank.
    /// </summary>
    public sealed class GitAnchorsFormatterSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the git-anchors-formatter suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("renders_anchors", "Anchors render into a Starting Point section", TestTags.Positive, () =>
            {
                string section = GitAnchorsFormatter.Render("abc1234", "main", "armada/worker/msn_x", null);
                AssertTrue(section.Contains("Starting Point"), "expected heading");
                AssertTrue(section.Contains("abc1234"), "expected start commit");
                AssertTrue(section.Contains("main"), "expected target branch");
                AssertTrue(section.Contains("armada/worker/msn_x"), "expected working branch");
            }));

            cases.Add(Case("renders_recent_commits", "Recent path commits render as sub-bullets", TestTags.Positive, () =>
            {
                string section = GitAnchorsFormatter.Render("abc1234", "main", "b", new List<string> { "src/api: added users route", "src/api: fixed paging" });
                AssertTrue(section.Contains("added users route"), "expected first entry");
                AssertTrue(section.Contains("fixed paging"), "expected second entry");
            }));

            cases.Add(Case("empty_when_nothing", "No anchors yields an empty string", TestTags.Negative, () =>
            {
                AssertEqual(String.Empty, GitAnchorsFormatter.Render(null, null, null, null));
                AssertEqual(String.Empty, GitAnchorsFormatter.Render("  ", "  ", "  ", new List<string>()));
            }));

            cases.Add(Case("skips_blank_recent_entries", "Blank recent entries are skipped", TestTags.Negative, () =>
            {
                string section = GitAnchorsFormatter.Render("abc", null, null, new List<string> { "", "   ", "real entry" });
                AssertTrue(section.Contains("real entry"), "expected the real entry");
                AssertFalse(section.Contains("- \n  - \n"), "should not emit blank bullets");
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.GitAnchorsFormatter",
                displayName: "Git Anchors Formatter",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.GitAnchorsFormatter",
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
