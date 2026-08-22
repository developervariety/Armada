namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="RequestHistorySummaryBuilder"/> aggregation. Cases verify overall
    /// counts, success rate, and (critically) that activity buckets are floored on the absolute epoch
    /// grid so their boundaries match the dashboard's Math.floor(timeMs / bucketMs) * bucketMs alignment
    /// for every bucket width, including widths of an hour or more where minute-of-hour flooring would
    /// otherwise collapse or drop adjacent buckets.
    /// </summary>
    public sealed class RequestHistorySummaryBuilderSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.RequestHistorySummaryBuilder";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Request History Summary Builder suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("build_computes_totals_and_success_rate", "Build computes totals, failures, and success rate", TestTags.Positive, () =>
            {
                DateTime anchor = new DateTime(2026, 8, 10, 3, 0, 0, DateTimeKind.Utc);
                List<RequestHistoryEntry> entries = new List<RequestHistoryEntry>
                {
                    Entry(anchor, true, 100.0),
                    Entry(anchor.AddMinutes(1), true, 300.0),
                    Entry(anchor.AddMinutes(2), false, 200.0)
                };

                RequestHistoryQuery query = new RequestHistoryQuery
                {
                    FromUtc = anchor,
                    ToUtc = anchor.AddMinutes(10),
                    BucketMinutes = 15
                };

                RequestHistorySummaryResult result = RequestHistorySummaryBuilder.Build(entries, query);

                AssertEqual(3, result.TotalCount);
                AssertEqual(2, result.SuccessCount);
                AssertEqual(1, result.FailureCount);
                AssertEqual(66.67, result.SuccessRate);
                AssertEqual(200.0, result.AverageDurationMs);
            }));

            cases.Add(Case("build_buckets_align_to_15_minute_grid", "Build floors 15-minute buckets to the epoch grid", TestTags.Positive, () =>
            {
                // 03:07 and 03:12 both fall in the 03:00-03:15 slot; 03:20 falls in the 03:15-03:30 slot.
                DateTime baseTime = new DateTime(2026, 8, 10, 3, 0, 0, DateTimeKind.Utc);
                List<RequestHistoryEntry> entries = new List<RequestHistoryEntry>
                {
                    Entry(baseTime.AddMinutes(7), true, 10.0),
                    Entry(baseTime.AddMinutes(12), true, 10.0),
                    Entry(baseTime.AddMinutes(20), false, 10.0)
                };

                RequestHistoryQuery query = new RequestHistoryQuery
                {
                    FromUtc = baseTime,
                    ToUtc = baseTime.AddMinutes(30),
                    BucketMinutes = 15
                };

                RequestHistorySummaryResult result = RequestHistorySummaryBuilder.Build(entries, query);

                RequestHistorySummaryBucket first = Bucket(result, new DateTime(2026, 8, 10, 3, 0, 0, DateTimeKind.Utc));
                RequestHistorySummaryBucket second = Bucket(result, new DateTime(2026, 8, 10, 3, 15, 0, DateTimeKind.Utc));
                AssertEqual(2, first.TotalCount);
                AssertEqual(2, first.SuccessCount);
                AssertEqual(1, second.TotalCount);
                AssertEqual(1, second.FailureCount);
            }));

            cases.Add(Case("build_two_hour_buckets_do_not_collapse_to_hourly", "Build keeps two-hour buckets on the epoch grid without collapsing adjacent hours", TestTags.Positive, () =>
            {
                // Regression: minute-of-hour flooring floored 02:30 and 03:30 both to their own HH:00,
                // producing two hourly buckets that the client then re-bucketed into one 120-minute slot
                // (dropping a bucket). Epoch flooring must place 02:30 and 03:30 in the SAME 02:00-04:00
                // slot on the server so the client sees a single correctly-counted bucket.
                DateTime start = new DateTime(2026, 8, 10, 2, 0, 0, DateTimeKind.Utc);
                List<RequestHistoryEntry> entries = new List<RequestHistoryEntry>
                {
                    Entry(new DateTime(2026, 8, 10, 2, 30, 0, DateTimeKind.Utc), true, 10.0),
                    Entry(new DateTime(2026, 8, 10, 3, 30, 0, DateTimeKind.Utc), true, 10.0),
                    Entry(new DateTime(2026, 8, 10, 4, 15, 0, DateTimeKind.Utc), false, 10.0)
                };

                RequestHistoryQuery query = new RequestHistoryQuery
                {
                    FromUtc = start,
                    ToUtc = new DateTime(2026, 8, 10, 6, 0, 0, DateTimeKind.Utc),
                    BucketMinutes = 120
                };

                RequestHistorySummaryResult result = RequestHistorySummaryBuilder.Build(entries, query);

                // Every populated bucket must sit on a 120-minute boundary from midnight (even hour, zero minute).
                foreach (RequestHistorySummaryBucket bucket in result.Buckets.Where(b => b.TotalCount > 0))
                {
                    AssertEqual(0, bucket.BucketStartUtc.Minute);
                    AssertTrue(bucket.BucketStartUtc.Hour % 2 == 0);
                }

                RequestHistorySummaryBucket firstSlot = Bucket(result, new DateTime(2026, 8, 10, 2, 0, 0, DateTimeKind.Utc));
                RequestHistorySummaryBucket secondSlot = Bucket(result, new DateTime(2026, 8, 10, 4, 0, 0, DateTimeKind.Utc));
                AssertEqual(2, firstSlot.TotalCount);
                AssertEqual(1, secondSlot.TotalCount);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Request History Summary Builder",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static RequestHistoryEntry Entry(DateTime createdUtc, bool isSuccess, double durationMs)
        {
            return new RequestHistoryEntry
            {
                CreatedUtc = createdUtc,
                IsSuccess = isSuccess,
                DurationMs = durationMs
            };
        }

        private static RequestHistorySummaryBucket Bucket(RequestHistorySummaryResult result, DateTime bucketStartUtc)
        {
            RequestHistorySummaryBucket? match = result.Buckets.FirstOrDefault(b => b.BucketStartUtc == bucketStartUtc);
            AssertTrue(match != null, "Expected a bucket starting at " + bucketStartUtc.ToString("O"));
            return match!;
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
