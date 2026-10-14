namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Provider-neutral activity generation, replay, ownership and no-dispatch guarantees.</summary>
    public sealed class SyntheticActivityGeneratorSuite : IArmadaTestSuite
    {
        /// <inheritdoc />
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor("Services.SyntheticActivity", "Synthetic activity generation", new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor("Services.SyntheticActivity", "charts_and_replay", "Synthetic activity populates mission/API/Harbor histories, recent buckets, and replays without writes", async token =>
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        DateTime to = DateTime.UtcNow.AddMinutes(-1);
                        SyntheticActivityRequest profile = new SyntheticActivityRequest { BatchId = "charts", Days = 3, Density = 0.5, Entropy = 1, Seed = 42, ToUtc = to };
                        SyntheticActivityResult first = await SyntheticActivityGenerator.GenerateAsync(db.Driver, profile, "default", "default", token).ConfigureAwait(false);
                        foreach (string kind in new string[] { "missions", "requests", "events", "signals", "tokens", "harborJobs", "harborSamples", "harborEvents" }) AssertTrue(first.Created.GetValueOrDefault(kind) > 0, kind + " populated");
                        Harbor? harbor = await db.Driver.Harbors.ReadAsync(first.HarborId, token).ConfigureAwait(false);
                        AssertTrue(harbor != null && !harbor.Enabled, "screenshot Harbor cannot route work");
                        List<MissionHistoryPoint> missions = await db.Driver.Missions.EnumerateHistoryPointsAsync(new MissionHistoryQuery { FromUtc = to.AddDays(-4), ToUtc = to.AddSeconds(1), VesselId = first.VesselId }, token).ConfigureAwait(false);
                        AssertEqual(first.Created["missions"], missions.Count);
                        List<HarborJobRecord> jobs = await db.Driver.HarborJobs.EnumerateAsync(first.HarborId, to.AddDays(-4), to.AddSeconds(1), token).ConfigureAwait(false);
                        AssertTrue(jobs.All(j => j.EndedUtc != null && j.Outcome != HarborJobOutcomeEnum.Running), "no runnable jobs");
                        List<RequestHistoryEntry> requests = await db.Driver.RequestHistory.EnumerateForSummaryAsync(new RequestHistoryQuery { TenantId = "default", UserId = "default", FromUtc = to.AddHours(-2), ToUtc = to }, token).ConfigureAwait(false);
                        AssertTrue(requests.All(r => r.AuthMethod == "Synthetic" && r.CorrelationId == "synthetic:charts" && r.CreatedUtc <= to), "owned and tagged");
                        AssertEqual(12, requests.Select(r => (int)((to - r.CreatedUtc).TotalSeconds / 600)).Distinct().Count(), "every recent ten-minute interval populated");
                        List<TokenUsageRecord> tokens = await db.Driver.TokenUsage.EnumerateForSummaryAsync(new TokenUsageQuery { HarborId = first.HarborId, FromUtc = to.AddDays(-4), ToUtc = to }, token).ConfigureAwait(false);
                        AssertTrue(tokens.Count > 0 && tokens.All(r => r.Source == "synthetic" && r.TotalTokens == r.InputTokens + r.OutputTokens && r.CachedTokens <= r.InputTokens), "Harbor-scoped token data");
                        SyntheticActivityResult replay = await SyntheticActivityGenerator.GenerateAsync(db.Driver, profile, "default", "default", token).ConfigureAwait(false);
                        AssertEqual(0, replay.Created.Values.Sum(), "replay inserts nothing");
                        AssertEqual(first.Created.Values.Sum(), replay.Existing, "every record replayed");
                        List<RequestHistoryEntry> hidden = await db.Driver.RequestHistory.EnumerateForSummaryAsync(new RequestHistoryQuery { TenantId = "other", FromUtc = to.AddDays(-4), ToUtc = to }, token).ConfigureAwait(false);
                        AssertEqual(0, hidden.Count, "other tenant sees no synthetic requests");
                    }
                }),
                new TestCaseDescriptor("Services.SyntheticActivity", "validation", "Malformed and excessive profiles are rejected before entity writes", async token =>
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        foreach (SyntheticActivityRequest request in new SyntheticActivityRequest[]
                        {
                            new SyntheticActivityRequest { BatchId = "../bad", ToUtc = DateTime.UtcNow },
                            new SyntheticActivityRequest { BatchId = "bad", ToUtc = DateTime.UtcNow, Density = Double.NaN },
                            new SyntheticActivityRequest { BatchId = "bad", ToUtc = DateTime.UtcNow.AddHours(1) },
                            new SyntheticActivityRequest { BatchId = "bad", ToUtc = DateTime.UtcNow, Days = 91 },
                            new SyntheticActivityRequest { BatchId = "bad", ToUtc = DateTime.UtcNow, Entropy = -1 }
                        })
                        {
                            bool rejected = false;
                            try { await SyntheticActivityGenerator.GenerateAsync(db.Driver, request, "default", "default", token).ConfigureAwait(false); }
                            catch (ArgumentException) { rejected = true; }
                            AssertTrue(rejected, "invalid profile rejected");
                        }
                        AssertEqual(0, (await db.Driver.RequestHistory.EnumerateForSummaryAsync(new RequestHistoryQuery(), token).ConfigureAwait(false)).Count);
                    }
                })
            });
        }
    }
}
