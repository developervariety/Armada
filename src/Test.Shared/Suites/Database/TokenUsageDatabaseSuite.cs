namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for token-usage database operations: create/read round-tripping of every field
    /// (including exact UTC timestamp and 64-bit token counts), model/source filtering, delete-by-filter,
    /// and the summary aggregation (grand totals, whole-window per-model aggregate ordered most-used
    /// first, and per-bucket per-model breakdown). Runs unchanged against SQLite, PostgreSQL, MySQL, and
    /// SQL Server so every provider's token_usage schema and mapping is validated.
    /// </summary>
    public sealed class TokenUsageDatabaseSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.TokenUsage";
        private static readonly DateTime _BaseTime = new DateTime(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Token Usage Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_and_read_round_trips_all_fields", "CreateAsync and ReadAsync round-trip all fields", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    TokenUsageRecord record = new TokenUsageRecord
                    {
                        Id = "tku_read_test",
                        TenantId = "ten_a",
                        UserId = "usr_a",
                        Model = "claude-sonnet-4",
                        Runtime = "claudecode",
                        Source = "mission",
                        SourceId = "msn_a",
                        VesselId = "vsl_a",
                        CaptainId = "cpt_a",
                        InputTokens = 1200,
                        OutputTokens = 3400,
                        CachedTokens = 500,
                        TotalTokens = 5100,
                        Estimated = true,
                        CreatedUtc = _BaseTime.AddMinutes(5)
                    };

                    await db.TokenUsage.CreateAsync(record);
                    TokenUsageRecord? read = await db.TokenUsage.ReadAsync(record.Id);

                    AssertNotNull(read, "Token-usage record should be readable");
                    AssertEqual("tku_read_test", read!.Id);
                    AssertEqual("ten_a", read.TenantId, "Tenant should persist");
                    AssertEqual("usr_a", read.UserId, "User should persist");
                    AssertEqual("claude-sonnet-4", read.Model, "Model should persist");
                    AssertEqual("claudecode", read.Runtime, "Runtime should persist");
                    AssertEqual("mission", read.Source, "Source should persist");
                    AssertEqual("msn_a", read.SourceId, "Source id should persist");
                    AssertEqual("vsl_a", read.VesselId, "Vessel should persist");
                    AssertEqual("cpt_a", read.CaptainId, "Captain should persist");
                    AssertEqual(1200L, read.InputTokens, "Input tokens should persist");
                    AssertEqual(3400L, read.OutputTokens, "Output tokens should persist");
                    AssertEqual(500L, read.CachedTokens, "Cached tokens should persist");
                    AssertEqual(5100L, read.TotalTokens, "Total tokens should persist");
                    AssertTrue(read.Estimated, "Estimated flag should persist");
                    AssertEqual(_BaseTime.AddMinutes(5), read.CreatedUtc, "Created timestamp should persist as UTC");
                }
            }));

            cases.Add(CaseAsync("summary_aggregates_totals_by_model_and_buckets", "Summary aggregates totals, per-model, and buckets", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    await db.TokenUsage.CreateAsync(Record("tku_1", "claude-sonnet-4", "mission", 100, 50, 20, 170, false, _BaseTime.AddMinutes(1)));
                    await db.TokenUsage.CreateAsync(Record("tku_2", "claude-sonnet-4", "mission", 200, 100, 0, 300, false, _BaseTime.AddMinutes(3)));
                    await db.TokenUsage.CreateAsync(Record("tku_3", "gpt-5", "chat", 40, 60, 0, 100, true, _BaseTime.AddMinutes(7)));

                    TokenUsageQuery query = new TokenUsageQuery
                    {
                        FromUtc = _BaseTime,
                        ToUtc = _BaseTime.AddMinutes(15),
                        BucketMinutes = 5
                    };

                    List<TokenUsageRecord> records = await db.TokenUsage.EnumerateForSummaryAsync(query);
                    TokenUsageSummaryResult summary = TokenUsageSummaryBuilder.Build(records, query);

                    AssertEqual(3, summary.RecordCount, "All three records should be aggregated");
                    AssertEqual(1, summary.EstimatedCount, "One record is estimated");
                    AssertEqual(340L, summary.InputTokens, "Grand input total");
                    AssertEqual(210L, summary.OutputTokens, "Grand output total");
                    AssertEqual(20L, summary.CachedTokens, "Grand cached total");
                    AssertEqual(570L, summary.TotalTokens, "Grand total");

                    // Per-model aggregate, most-used first.
                    AssertEqual(2, summary.ByModel.Count, "Two distinct models");
                    AssertEqual("claude-sonnet-4", summary.ByModel[0].Model, "Most-used model should be first");
                    AssertEqual(470L, summary.ByModel[0].TotalTokens, "claude total");
                    AssertEqual(300L, summary.ByModel[0].InputTokens, "claude input total");
                    AssertEqual("gpt-5", summary.ByModel[1].Model, "Second model");
                    AssertEqual(100L, summary.ByModel[1].TotalTokens, "gpt-5 total");

                    // Buckets are gap-filled across the window (5-minute buckets over 15 minutes -> 4 buckets).
                    AssertTrue(summary.Buckets.Count >= 3, "Window should be bucketed");
                    long bucketSum = 0;
                    foreach (TokenUsageBucket bucket in summary.Buckets) bucketSum += bucket.TotalTokens;
                    AssertEqual(570L, bucketSum, "Bucket totals should sum to the grand total");
                }
            }));

            cases.Add(CaseAsync("enumerate_filters_by_model_and_source", "EnumerateAsync filters by model and source", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    await db.TokenUsage.CreateAsync(Record("tku_a", "claude-sonnet-4", "mission", 10, 10, 0, 20, false, _BaseTime));
                    await db.TokenUsage.CreateAsync(Record("tku_b", "gpt-5", "chat", 10, 10, 0, 20, false, _BaseTime));
                    await db.TokenUsage.CreateAsync(Record("tku_c", "gpt-5", "mission", 10, 10, 0, 20, false, _BaseTime));

                    EnumerationResult<TokenUsageRecord> byModel = await db.TokenUsage.EnumerateAsync(new TokenUsageQuery { Model = "gpt-5" });
                    AssertEqual(2, byModel.Objects.Count, "Two gpt-5 records");

                    EnumerationResult<TokenUsageRecord> bySource = await db.TokenUsage.EnumerateAsync(new TokenUsageQuery { Source = "mission" });
                    AssertEqual(2, bySource.Objects.Count, "Two mission records");
                }
            }));

            cases.Add(CaseAsync("delete_by_filter_removes_matching_records", "DeleteByFilterAsync removes matching records", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    await db.TokenUsage.CreateAsync(Record("tku_x", "claude-sonnet-4", "mission", 10, 10, 0, 20, false, _BaseTime));
                    await db.TokenUsage.CreateAsync(Record("tku_y", "gpt-5", "chat", 10, 10, 0, 20, false, _BaseTime));

                    int deleted = await db.TokenUsage.DeleteByFilterAsync(new TokenUsageQuery { Model = "gpt-5" });
                    AssertEqual(1, deleted, "One gpt-5 record deleted");

                    EnumerationResult<TokenUsageRecord> remaining = await db.TokenUsage.EnumerateAsync(new TokenUsageQuery());
                    AssertEqual(1, remaining.Objects.Count, "One record remains");
                    AssertEqual("claude-sonnet-4", remaining.Objects[0].Model, "The claude record remains");
                }
            }));

            cases.Add(CaseAsync("read_non_existent_returns_null", "ReadAsync NonExistent ReturnsNull", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    TokenUsageRecord? read = await db.TokenUsage.ReadAsync("tku_missing");
                    AssertNull(read, "Missing record should read as null");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Token Usage Database",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TokenUsageRecord Record(string id, string model, string source, long input, long output, long cached, long total, bool estimated, DateTime createdUtc)
        {
            return new TokenUsageRecord
            {
                Id = id,
                TenantId = "ten_a",
                Model = model,
                Runtime = "claudecode",
                Source = source,
                InputTokens = input,
                OutputTokens = output,
                CachedTokens = cached,
                TotalTokens = total,
                Estimated = estimated,
                CreatedUtc = createdUtc
            };
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
