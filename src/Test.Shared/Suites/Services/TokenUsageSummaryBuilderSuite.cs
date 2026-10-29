namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for summary windows that ask for more buckets than a response may fill.
    /// </summary>
    public sealed class TokenUsageSummaryBuilderSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.TokenUsageSummaryBuilder";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the token-usage summary builder suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("build_rejects_a_window_over_1000_buckets", "Build rejects a window that would fill more than 1000 buckets", TestTags.Negative, () =>
            {
                DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                TokenUsageQuery query = new TokenUsageQuery
                {
                    FromUtc = start,
                    ToUtc = start.AddMinutes(1000),
                    BucketMinutes = 1
                };

                CollectionLimitExceededException? refused = null;
                try
                {
                    TokenUsageSummaryBuilder.Build(new List<TokenUsageRecord>(), query);
                }
                catch (CollectionLimitExceededException ex)
                {
                    refused = ex;
                }

                AssertTrue(refused != null, "A window of more than 1000 buckets must be refused.");
                AssertEqual("Buckets", refused!.Field);
                AssertEqual(CollectionLimits.MaxSummaryBuckets, refused.Limit);
                AssertEqual(1001L, refused.Actual);
            }));

            cases.Add(Case("mission_history_rejects_a_window_over_1000_buckets", "Mission history rejects a window that would fill more than 1000 buckets", TestTags.Negative, () =>
            {
                DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                CollectionLimitExceededException? refused = null;
                try
                {
                    CollectionLimits.RequireMissionHistoryBuckets(start, start.AddMinutes(1001), 1);
                }
                catch (CollectionLimitExceededException ex)
                {
                    refused = ex;
                }

                AssertTrue(refused != null, "A mission-history window of more than 1000 buckets must be refused.");
                AssertEqual("Buckets", refused!.Field);
                AssertEqual(1001L, refused.Actual);
                CollectionLimits.RequireMissionHistoryBuckets(start, start.AddMinutes(1000), 1);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Token Usage Summary Builder",
                cases: cases);
        }

        #endregion

        #region Private-Methods

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
