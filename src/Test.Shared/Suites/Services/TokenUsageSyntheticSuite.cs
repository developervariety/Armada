namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Tui.Widgets;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Historical simulation, idempotency, scope/filter preservation, validation, and long-valued heatmap data.</summary>
    public sealed class TokenUsageSyntheticSuite : IArmadaTestSuite
    {
        /// <inheritdoc />
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor("Services.TokenUsageSynthetic", "Synthetic token usage and calendar", new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor("Services.TokenUsageSynthetic", "import_replay_scope", "Simulation is tagged, backdated, scoped, replayable, and isolated from real records", async token =>
                {
                    using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    DateTime now = DateTime.UtcNow;
                    await db.Driver.TokenUsage.CreateAsync(new TokenUsageRecord { TenantId = "demo", UserId = "owner", Source = "mission", Model = "real", TotalTokens = 5, CreatedUtc = now }, token).ConfigureAwait(false);
                    TokenUsageSyntheticRequest request = new TokenUsageSyntheticRequest { BatchId = "test-batch", Records = new List<TokenUsageSyntheticRecord>
                    {
                        new TokenUsageSyntheticRecord { Model = "local", Runtime = "mux", CreatedUtc = now.AddDays(-40), InputTokens = 400, OutputTokens = 100, CachedTokens = 300 },
                        new TokenUsageSyntheticRecord { Model = "frontier", CreatedUtc = now.AddDays(-1), InputTokens = 500, OutputTokens = 200 }
                    } };
                    TokenUsageSyntheticResult first = await TokenUsageSyntheticImporter.ImportAsync(db.Driver.TokenUsage, request, "demo", "owner", now, token).ConfigureAwait(false);
                    AssertEqual(2, first.Created); AssertEqual(1200L, first.TotalTokens);
                    TokenUsageSyntheticResult replay = await TokenUsageSyntheticImporter.ImportAsync(db.Driver.TokenUsage, request, "demo", "owner", now, token).ConfigureAwait(false);
                    AssertEqual(0, replay.Created); AssertEqual(2, replay.Existing);
                    TokenUsageQuery scoped = new TokenUsageQuery { TenantId = "demo", UserId = "owner", SourceId = first.SourceId, FromUtc = now.AddDays(-42), ToUtc = now, BucketMinutes = 1440 };
                    List<TokenUsageRecord> records = await db.Driver.TokenUsage.EnumerateForSummaryAsync(scoped, token).ConfigureAwait(false);
                    AssertEqual(2, records.Count);
                    AssertTrue(records.TrueForAll(r => r.Source == "synthetic" && r.Estimated && r.CaptainId == null && r.VesselId == null), "No attribution to real missions/captains");
                    TokenUsageSummaryResult summary = TokenUsageSummaryBuilder.Build(records, scoped);
                    AssertEqual(2, summary.SyntheticCount); AssertEqual(1200L, summary.TotalTokens);
                    scoped.UserId = "other";
                    AssertEqual(0, (await db.Driver.TokenUsage.EnumerateForSummaryAsync(scoped, token).ConfigureAwait(false)).Count, "owner scope enforced");
                    request.Records[0].InputTokens = 401;
                    bool conflict = false;
                    try { await TokenUsageSyntheticImporter.ImportAsync(db.Driver.TokenUsage, request, "demo", "owner", now, token).ConfigureAwait(false); }
                    catch (InvalidOperationException) { conflict = true; }
                    AssertTrue(conflict, "different batch content rejected");
                    AssertEqual(3L, (await db.Driver.TokenUsage.EnumerateAsync(new TokenUsageQuery { TenantId = "demo" }, token).ConfigureAwait(false)).TotalRecords, "real record untouched and no duplicate imports");
                }),
                new TestCaseDescriptor("Services.TokenUsageSynthetic", "invalid_batch_no_writes", "Bad record, future timestamp, old timestamp, excessive counts, and batch size fail before writing", async token =>
                {
                    using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    DateTime now = DateTime.UtcNow;
                    foreach (TokenUsageSyntheticRecord invalid in new List<TokenUsageSyntheticRecord>
                    {
                        new TokenUsageSyntheticRecord { Model = "", CreatedUtc = now },
                        new TokenUsageSyntheticRecord { Model = "m", CreatedUtc = now.AddDays(1) },
                        new TokenUsageSyntheticRecord { Model = "m", CreatedUtc = now.AddDays(-367) },
                        new TokenUsageSyntheticRecord { Model = "m", CreatedUtc = now, InputTokens = -1 },
                        new TokenUsageSyntheticRecord { Model = "m", CreatedUtc = now, OutputTokens = 1000000001 },
                        new TokenUsageSyntheticRecord { Model = new string('m', 129), CreatedUtc = now }
                    })
                    {
                        TokenUsageSyntheticRequest request = new TokenUsageSyntheticRequest { BatchId = "invalid", Records = new List<TokenUsageSyntheticRecord> { new TokenUsageSyntheticRecord { Model = "valid", CreatedUtc = now }, invalid } };
                        bool rejected = false;
                        try { await TokenUsageSyntheticImporter.ImportAsync(db.Driver.TokenUsage, request, "demo", "owner", now, token).ConfigureAwait(false); }
                        catch (ArgumentException) { rejected = true; }
                        AssertTrue(rejected, "invalid input rejected");
                        AssertEqual(0L, (await db.Driver.TokenUsage.EnumerateAsync(new TokenUsageQuery(), token).ConfigureAwait(false)).TotalRecords, "validation precedes all writes");
                    }
                }),
                new TestCaseDescriptor("Services.TokenUsageSynthetic", "calendar_long_values", "UTC token heatmap keeps billions of tokens and per-type selection without truncating to int", token =>
                {
                    DateTime day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    TokenActivityHeatmap heatmap = new TokenActivityHeatmap();
                    TokenUsageSummaryResult data = new TokenUsageSummaryResult { Buckets = new List<TokenUsageBucket> { new TokenUsageBucket { BucketStartUtc = day, TotalTokens = 5000000000, CachedTokens = 4000000000 }, new TokenUsageBucket { BucketStartUtc = day.AddDays(1), TotalTokens = 1, CachedTokens = 1 } } };
                    heatmap.SetUsage(data, day, day.AddDays(2));
                    AssertEqual(5000000000L, heatmap.TokensOn(day));
                    AssertEqual(0L, heatmap.TokensOn(day.AddDays(2)));
                    AssertEqual(4, heatmap.CountOn(day), "visual intensity only is quantized");
                    AssertEqual(1, heatmap.CountOn(day.AddDays(1)), "nonzero small counts remain visible");
                    heatmap.SetUsage(data, day, day.AddDays(2), "cached");
                    AssertEqual(4000000000L, heatmap.TokensOn(day));
                    AssertContains("Tokens", heatmap.DayDescription!(day));
                    return Task.CompletedTask;
                })
            });
        }
    }
}
