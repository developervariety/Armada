namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the search-only fast-pack context path, the repo-size fast-pack decision,
    /// and the related budget/threshold settings on <see cref="CodeIndexService"/>.
    /// </summary>
    public class CodeIndexFastPackTests : TestSuite
    {
        private static readonly JsonSerializerOptions _IndexJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Code Index Fast Pack";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("BuildContextPackAsync_FastPackOnly_SkipsGraphExpansionButStillStagesPack", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-fast-pack-true-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "Implement behavior near SearchKeyword",
                            TokenBudget = 1200,
                            MaxResults = 4,
                            FastPackOnly = true
                        }).ConfigureAwait(false);

                        AssertEqual(0, response.GraphIncludedFiles.Count, "Fast pack must not add graph-expansion files");
                        AssertFalse(response.Metrics.GraphExpansionUsed, "Fast pack must report no graph expansion");
                        AssertTrue(response.Metrics.FastPackFallbackUsed, "Fast pack must report the fast-pack fallback was used");
                        AssertTrue(File.Exists(response.MaterializedPath), "Fast pack should still be materialized");
                        AssertEqual(1, response.PrestagedFiles.Count, "Fast pack should still stage a prestaged file");
                        AssertEqual("_briefing/context-pack.md", response.PrestagedFiles[0].DestPath);
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_DefaultRequest_RunsGraphExpansionPathAndFlagsFastPackFalse", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-fast-pack-false-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "Implement behavior near SearchKeyword",
                            TokenBudget = 1200,
                            MaxResults = 4
                        }).ConfigureAwait(false);

                        // Default path must not flag the fast-pack fallback. The graph-expansion path still
                        // runs for small repos; this pins the "do not remove graph-expansion" constraint.
                        AssertFalse(response.Metrics.FastPackFallbackUsed, "Default request must not report the fast-pack fallback");
                        AssertTrue(File.Exists(response.MaterializedPath), "Default pack should be materialized");
                        AssertEqual(1, response.PrestagedFiles.Count, "Default pack should stage a prestaged file");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("ShouldUseFastPackAsync_BelowThresholdFalse_AboveThresholdTrue", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-fast-pack-threshold-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);

                        // Index once so persisted status has a non-zero indexed file count (the fixture has 2 files).
                        CodeIndexService indexer = CreateService(testDb, dataRoot);
                        CodeIndexStatus status = await indexer.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        AssertTrue(status.DocumentCount >= 2, "Fixture should index at least two files");

                        CodeIndexService highThreshold = CreateService(testDb, dataRoot, s => s.FastPackFileThreshold = 1000);
                        AssertFalse(
                            await highThreshold.ShouldUseFastPackAsync(vessel.Id).ConfigureAwait(false),
                            "File count below threshold should not use the fast pack");

                        CodeIndexService lowThreshold = CreateService(testDb, dataRoot, s => s.FastPackFileThreshold = 1);
                        AssertTrue(
                            await lowThreshold.ShouldUseFastPackAsync(vessel.Id).ConfigureAwait(false),
                            "File count above threshold should use the fast pack");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("CodeIndexSettings_ContextPackBudgetMs_And_FastPackFileThreshold_Clamp", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();

                settings.ContextPackBudgetMs = 1;
                AssertEqual(500, settings.ContextPackBudgetMs, "ContextPackBudgetMs should clamp up to the minimum");
                settings.ContextPackBudgetMs = 999999;
                AssertEqual(120000, settings.ContextPackBudgetMs, "ContextPackBudgetMs should clamp down to the maximum");
                settings.ContextPackBudgetMs = 8000;
                AssertEqual(8000, settings.ContextPackBudgetMs, "In-range ContextPackBudgetMs should be preserved");

                settings.FastPackFileThreshold = -5;
                AssertEqual(0, settings.FastPackFileThreshold, "FastPackFileThreshold should clamp up to zero");
                settings.FastPackFileThreshold = 2500;
                AssertEqual(2500, settings.FastPackFileThreshold, "In-range FastPackFileThreshold should be preserved");

                return Task.CompletedTask;
            });

            await RunTest("CodeIndexSettings_FastPack_DefaultsAndBoundaryValuesPreserved", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();

                // Defaults match the brief (8000 ms budget, 1500-file threshold).
                AssertEqual(8000, settings.ContextPackBudgetMs, "Default ContextPackBudgetMs should be 8000");
                AssertEqual(1500, settings.FastPackFileThreshold, "Default FastPackFileThreshold should be 1500");

                // Inclusive clamp boundaries must be preserved, not clamped further.
                settings.ContextPackBudgetMs = 500;
                AssertEqual(500, settings.ContextPackBudgetMs, "Minimum ContextPackBudgetMs boundary should be preserved");
                settings.ContextPackBudgetMs = 120000;
                AssertEqual(120000, settings.ContextPackBudgetMs, "Maximum ContextPackBudgetMs boundary should be preserved");

                // Zero is the inclusive floor for the threshold (>= 0), not clamped up to one.
                settings.FastPackFileThreshold = 0;
                AssertEqual(0, settings.FastPackFileThreshold, "Zero FastPackFileThreshold boundary should be preserved");

                return Task.CompletedTask;
            });

            await RunTest("ShouldUseFastPackAsync_BlankVesselId_ThrowsArgumentNull", async () =>
            {
                string dataRoot = NewTempDirectory("armada-fast-pack-blankid-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await AssertThrowsAsync<ArgumentNullException>(
                            () => service.ShouldUseFastPackAsync(null!),
                            "Null vesselId should throw").ConfigureAwait(false);
                        await AssertThrowsAsync<ArgumentNullException>(
                            () => service.ShouldUseFastPackAsync("   "),
                            "Whitespace vesselId should throw").ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("ShouldUseFastPackAsync_UnknownVessel_ThrowsInvalidOperation", async () =>
            {
                string dataRoot = NewTempDirectory("armada-fast-pack-unknown-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await AssertThrowsAsync<InvalidOperationException>(
                            () => service.ShouldUseFastPackAsync("vsl_does_not_exist"),
                            "Unknown vessel should throw").ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("ShouldUseFastPackAsync_NeverIndexedVessel_ReturnsFalse", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-fast-pack-noindex-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);

                        // No UpdateAsync call: there is no persisted status, so the indexed count is
                        // treated as zero. Even a zero threshold must not trigger the fast pack
                        // because the comparison is strictly greater-than.
                        CodeIndexService service = CreateService(testDb, dataRoot, s => s.FastPackFileThreshold = 0);

                        AssertFalse(
                            await service.ShouldUseFastPackAsync(vessel.Id).ConfigureAwait(false),
                            "A never-indexed vessel should not use the fast pack");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("ShouldUseFastPackAsync_CountEqualToThresholdIsFalse_OneBelowIsTrue", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-fast-pack-boundary-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);

                        CodeIndexService indexer = CreateService(testDb, dataRoot);
                        CodeIndexStatus status = await indexer.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        AssertTrue(status.DocumentCount >= 2, "Fixture should index at least two files");

                        // Strictly greater-than: a threshold equal to the indexed count must NOT trigger.
                        CodeIndexService atThreshold = CreateService(testDb, dataRoot, s => s.FastPackFileThreshold = status.DocumentCount);
                        AssertFalse(
                            await atThreshold.ShouldUseFastPackAsync(vessel.Id).ConfigureAwait(false),
                            "Count equal to threshold must not use the fast pack");

                        // One below the count is exceeded, so the fast pack engages.
                        CodeIndexService justBelow = CreateService(testDb, dataRoot, s => s.FastPackFileThreshold = status.DocumentCount - 1);
                        AssertTrue(
                            await justBelow.ShouldUseFastPackAsync(vessel.Id).ConfigureAwait(false),
                            "Count one above threshold must use the fast pack");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_InvalidRequests_ThrowArgumentNull", async () =>
            {
                string dataRoot = NewTempDirectory("armada-fast-pack-invalid-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await AssertThrowsAsync<ArgumentNullException>(
                            () => service.BuildContextPackAsync(null!),
                            "Null request should throw").ConfigureAwait(false);
                        await AssertThrowsAsync<ArgumentNullException>(
                            () => service.BuildContextPackAsync(new ContextPackRequest { VesselId = "   ", Goal = "g", FastPackOnly = true }),
                            "Blank VesselId should throw").ConfigureAwait(false);
                        await AssertThrowsAsync<ArgumentNullException>(
                            () => service.BuildContextPackAsync(new ContextPackRequest { VesselId = "vsl_x", Goal = "   ", FastPackOnly = true }),
                            "Blank Goal should throw").ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("RecordingCodeIndexService_ShouldUseFastPackAsync_ReturnsFalseAndRecordsVessel", async () =>
            {
                RecordingCodeIndexService recording = new RecordingCodeIndexService();

                bool result = await recording.ShouldUseFastPackAsync("vsl_recorded").ConfigureAwait(false);

                AssertFalse(result, "Recording double should opt out of the fast pack");
                AssertEqual(1, recording.ShouldUseFastPackVesselIds.Count, "Recording double should record one call");
                AssertEqual("vsl_recorded", recording.ShouldUseFastPackVesselIds[0], "Recording double should record the vessel id");
            });

            await RunTest("BuildContextPackAsync_LargeVessel_AutoFastPack_CompletesWithinBudgetAndPreservesTopResult", async () =>
            {
                string dataRoot = NewTempDirectory("armada-fast-pack-large-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, Path.Combine(dataRoot, "repo")).ConfigureAwait(false);
                        ArmadaSettings settings = BuildSettings(dataRoot);

                        int documentCount = 1600;
                        List<CodeIndexRecord> records = BuildLargeRecords(vessel.Id, documentCount, out string topPath);
                        await WritePersistedIndexAsync(settings, vessel, records, documentCount).ConfigureAwait(false);

                        CountingEmbeddingClient embeddingClient = new CountingEmbeddingClient(new float[] { 1.0f, 0.0f, 0.0f });
                        CodeIndexService service = CreateService(testDb, dataRoot, s =>
                        {
                            s.UseSemanticSearch = false;
                            s.ContextPackBudgetMs = 500;
                        }, embeddingClient);

                        Stopwatch stopwatch = Stopwatch.StartNew();
                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "ExactMatchKeyword",
                            TokenBudget = 1200,
                            MaxResults = 4
                        }).ConfigureAwait(false);
                        stopwatch.Stop();

                        AssertTrue(response.Metrics.FastPackFallbackUsed, "Large vessel should auto-enable fast-pack fallback");
                        AssertFalse(response.Metrics.GraphExpansionUsed, "Large vessel should skip graph expansion");
                        AssertTrue(File.Exists(response.MaterializedPath), "Pack should still be materialized");
                        AssertContains(topPath, response.Markdown, "Top search result must still be in the pack");
                        AssertEqual(0, embeddingClient.CallCount, "Lexical fast pack should not issue any embedding calls");
                        AssertTrue(stopwatch.ElapsedMilliseconds < 5000, "Large-vessel pack should complete well under budget (elapsed " + stopwatch.ElapsedMilliseconds + "ms)");
                        AssertTrue(response.Warnings.Any(w => w.Contains("fast_pack_threshold", StringComparison.OrdinalIgnoreCase)),
                            "Warning should explain the fast-pack fallback");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_LargeVessel_SemanticSearch_StillOneQueryEmbedding", async () =>
            {
                string dataRoot = NewTempDirectory("armada-fast-pack-large-semantic-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, Path.Combine(dataRoot, "repo")).ConfigureAwait(false);
                        ArmadaSettings settings = BuildSettings(dataRoot);

                        int documentCount = 1600;
                        List<CodeIndexRecord> records = BuildLargeRecords(vessel.Id, documentCount, out string topPath);
                        await WritePersistedIndexAsync(settings, vessel, records, documentCount).ConfigureAwait(false);

                        CountingEmbeddingClient embeddingClient = new CountingEmbeddingClient(new float[] { 1.0f, 0.0f, 0.0f });
                        CodeIndexService service = CreateService(testDb, dataRoot, s =>
                        {
                            s.UseSemanticSearch = true;
                            s.ContextPackBudgetMs = 8000;
                        }, embeddingClient);

                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "ExactMatchKeyword",
                            TokenBudget = 1200,
                            MaxResults = 4
                        }).ConfigureAwait(false);

                        AssertTrue(response.Metrics.FastPackFallbackUsed, "Large vessel should auto-enable fast-pack fallback");
                        AssertFalse(response.Metrics.GraphExpansionUsed, "Large vessel should skip graph expansion");
                        AssertContains(topPath, response.Markdown, "Top search result must still be in the pack");
                        AssertEqual(1, embeddingClient.CallCount, "Semantic fast pack should issue exactly one query embedding (persisted corpus vectors are reused, never re-embedded)");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_BudgetExpiresDuringSearch_FallsBackToSearchOnlyPack", async () =>
            {
                string dataRoot = NewTempDirectory("armada-fast-pack-budget-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, Path.Combine(dataRoot, "repo")).ConfigureAwait(false);
                        ArmadaSettings settings = BuildSettings(dataRoot);

                        List<CodeIndexRecord> records = new List<CodeIndexRecord>
                        {
                            new CodeIndexRecord
                            {
                                VesselId = vessel.Id,
                                Path = "src/Service.cs",
                                CommitSha = "abc",
                                ContentHash = "h1",
                                Language = "csharp",
                                StartLine = 1,
                                EndLine = 5,
                                IsReferenceOnly = false,
                                Content = "public class Service { public void ExactMatchKeyword() { } }"
                            }
                        };
                        await WritePersistedIndexAsync(settings, vessel, records, documentCount: 1).ConfigureAwait(false);

                        // A slow query embedding makes the search phase outlast the tiny context-pack budget.
                        // The budget token must cut the embedding short, fall back to lexical scoring (so the
                        // top lexical result still appears), skip graph expansion, and stage a search-only pack
                        // -- proving the hard budget now covers the initial search/embedding phase, not just the
                        // later graph/summarizer stages.
                        SlowEmbeddingClient embeddingClient = new SlowEmbeddingClient(new float[] { 1.0f, 0.0f, 0.0f }, delayMs: 1500);
                        CodeIndexService service = CreateService(testDb, dataRoot, s =>
                        {
                            s.UseSemanticSearch = true;
                            s.ContextPackBudgetMs = 300;
                            s.FastPackFileThreshold = 5000;
                        }, embeddingClient);

                        Stopwatch stopwatch = Stopwatch.StartNew();
                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "ExactMatchKeyword",
                            TokenBudget = 1200,
                            MaxResults = 4
                        }).ConfigureAwait(false);
                        stopwatch.Stop();

                        AssertTrue(File.Exists(response.MaterializedPath), "Budget fallback should still stage a pack");
                        AssertContains("src/Service.cs", response.Markdown, "Lexical search result must still be in the fallback pack");
                        AssertTrue(response.Metrics.FastPackFallbackUsed, "Budget-expired search should mark the pack as a fast-pack fallback");
                        AssertFalse(response.Metrics.GraphExpansionUsed, "Budget-expired search must skip graph expansion");
                        AssertTrue(response.Warnings.Any(w => w.Contains("context_pack_budget_expired", StringComparison.OrdinalIgnoreCase)),
                            "Warning should report the budget expiration (warnings: " + String.Join(" | ", response.Warnings) + ")");
                        AssertTrue(stopwatch.ElapsedMilliseconds < 5000, "Budget fallback should complete quickly (elapsed " + stopwatch.ElapsedMilliseconds + "ms)");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_CallerCancellation_PropagatesOperationCanceled", async () =>
            {
                string dataRoot = NewTempDirectory("armada-fast-pack-cancel-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, Path.Combine(dataRoot, "repo")).ConfigureAwait(false);
                        ArmadaSettings settings = BuildSettings(dataRoot);

                        List<CodeIndexRecord> records = BuildLargeRecords(vessel.Id, 3, out string topPath);
                        await WritePersistedIndexAsync(settings, vessel, records, documentCount: 3).ConfigureAwait(false);
                        WriteMinimalGraphSidecars(settings, vessel);

                        CodeIndexService service = CreateService(testDb, dataRoot, s =>
                        {
                            s.UseSemanticSearch = false;
                            s.FastPackFileThreshold = 5000;
                        });

                        using (CancellationTokenSource cts = new CancellationTokenSource())
                        {
                            cts.Cancel();

                            // Caller cancellation is distinct from budget expiry: a genuinely cancelled caller
                            // token must surface as OperationCanceledException, NOT be silently swallowed into a
                            // degraded fast-pack fallback. This pins the post-search caller-cancel check that
                            // separates caller-cancel from budget-cancel.
                            await AssertThrowsAsync<OperationCanceledException>(
                                () => service.BuildContextPackAsync(new ContextPackRequest
                                {
                                    VesselId = vessel.Id,
                                    Goal = "ExactMatchKeyword",
                                    TokenBudget = 1200,
                                    MaxResults = 4
                                }, cts.Token),
                                "Pre-cancelled caller token must propagate cancellation").ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_SummarizerExceedsBudget_LabelsBudgetExpiryNotSummarizerTimeout", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-fast-pack-summarizer-budget-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);

                        // The whole-build budget must also bound the LATE summarizer stage, not just the
                        // initial search. Give the summarizer a generous per-call timeout (600s) but a tiny
                        // overall budget (1000ms): the search/markdown phase clears the budget, then a slow
                        // summarizer outlives it. The diff links the summarizer delay task to the budget token
                        // and labels a budget-driven abort `context_pack_budget_expired` -- distinct from the
                        // per-call `summarizer_timeout`. FastPackOnly removes graph timing from the equation so
                        // only the fast lexical search precedes the summarizer.
                        DelayingInferenceClient inference = new DelayingInferenceClient(delayMs: 6000, summary: "summary that arrives after the budget");
                        CodeIndexService service = CreateService(testDb, dataRoot, s =>
                        {
                            s.UseSemanticSearch = false;
                            s.UseSummarizer = true;
                            s.SummarizerTimeoutSeconds = 600;
                            s.ContextPackBudgetMs = 1000;
                        }, inferenceClient: inference);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        Stopwatch stopwatch = Stopwatch.StartNew();
                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "SearchKeyword",
                            TokenBudget = 1200,
                            MaxResults = 4,
                            FastPackOnly = true
                        }).ConfigureAwait(false);
                        stopwatch.Stop();

                        AssertEqual(1, inference.CallCount, "Summarizer must have been entered exactly once (budget alive at the gate)");
                        AssertFalse(response.IsSummarized, "A budget-aborted summarizer must not mark the pack as summarized");
                        AssertTrue(response.SummarizedMarkdown == null, "SummarizedMarkdown must be null when the summarizer is cut by the budget");
                        AssertTrue(response.Warnings.Any(w => w.Contains("context_pack_budget_expired", StringComparison.OrdinalIgnoreCase) && w.Contains("summariz", StringComparison.OrdinalIgnoreCase)),
                            "Budget-driven summarizer abort must record a context_pack_budget_expired summarization warning (warnings: " + String.Join(" | ", response.Warnings) + ")");
                        AssertFalse(response.Warnings.Any(w => w.Contains("summarizer_timeout", StringComparison.OrdinalIgnoreCase)),
                            "A budget expiry must NOT be mislabeled as the per-call summarizer_timeout");

                        string materialized = await File.ReadAllTextAsync(response.MaterializedPath).ConfigureAwait(false);
                        AssertEqual(response.Markdown, materialized, "Budget-aborted summarizer must materialize the raw markdown fallback");
                        AssertTrue(stopwatch.ElapsedMilliseconds < 5000, "Budget must cut the slow summarizer short, not wait the full delay (elapsed " + stopwatch.ElapsedMilliseconds + "ms)");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });
        }

        #region Helpers

        private static CodeIndexService CreateService(
            TestDatabase testDb,
            string dataRoot,
            Action<CodeIndexSettings>? configureCodeIndex = null,
            IEmbeddingClient? embeddingClient = null,
            IInferenceClient? inferenceClient = null)
        {
            ArmadaSettings settings = BuildSettings(dataRoot, configureCodeIndex);
            LoggingModule logging = SilentLogging();
            return new CodeIndexService(logging, testDb.Driver, settings, new GitService(logging), embeddingClient, inferenceClient);
        }

        private static ArmadaSettings BuildSettings(string dataRoot, Action<CodeIndexSettings>? configureCodeIndex = null)
        {
            CodeIndexSettings codeIndex = new CodeIndexSettings
            {
                IndexDirectory = Path.Combine(dataRoot, "code-index"),
                MaxChunkLines = 20,
                MaxSearchResults = 10,
                MaxContextPackResults = 8,
                UseSemanticSearch = false
            };
            configureCodeIndex?.Invoke(codeIndex);

            ArmadaSettings settings = new ArmadaSettings
            {
                DataDirectory = Path.Combine(dataRoot, "data"),
                ReposDirectory = Path.Combine(dataRoot, "repos"),
                CodeIndex = codeIndex
            };
            settings.InitializeDirectories();
            return settings;
        }

        private static async Task WritePersistedIndexAsync(ArmadaSettings settings, Vessel vessel, IReadOnlyList<CodeIndexRecord> records, int documentCount)
        {
            string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
            Directory.CreateDirectory(indexDir);

            CodeIndexStatus status = new CodeIndexStatus
            {
                VesselId = vessel.Id,
                VesselName = vessel.Name,
                DefaultBranch = vessel.DefaultBranch,
                IndexedCommitSha = "deadbeef",
                CurrentCommitSha = "deadbeef",
                IndexedAtUtc = DateTime.UtcNow,
                Freshness = "Fresh",
                DocumentCount = documentCount,
                ChunkCount = records.Count,
                IndexDirectory = indexDir,
                LastError = null
            };

            string metadataPath = Path.Combine(indexDir, "metadata.json");
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(status, _IndexJsonOptions)).ConfigureAwait(false);

            string chunksPath = Path.Combine(indexDir, "chunks.jsonl");
            using (StreamWriter writer = new StreamWriter(chunksPath))
            {
                foreach (CodeIndexRecord record in records)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record, _IndexJsonOptions)).ConfigureAwait(false);
                }
            }
        }

        private static void WriteMinimalGraphSidecars(ArmadaSettings settings, Vessel vessel)
        {
            string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
            Directory.CreateDirectory(indexDir);

            List<CodeGraphSymbolRecord> symbols = new List<CodeGraphSymbolRecord>
            {
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    QualifiedName = "Service.ExactMatchKeyword",
                    SimpleName = "ExactMatchKeyword",
                    Kind = CodeGraphSymbolKindEnum.Method,
                    Path = "src/Service.cs",
                    StartLine = 1,
                    EndLine = 1,
                    CommitSha = "abc"
                },
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    QualifiedName = "Consumer.UseKeyword",
                    SimpleName = "UseKeyword",
                    Kind = CodeGraphSymbolKindEnum.Method,
                    Path = "src/Consumer.cs",
                    StartLine = 1,
                    EndLine = 1,
                    CommitSha = "abc"
                }
            };

            List<CodeGraphEdgeRecord> edges = new List<CodeGraphEdgeRecord>
            {
                new CodeGraphEdgeRecord
                {
                    VesselId = vessel.Id,
                    Kind = CodeGraphEdgeKindEnum.Calls,
                    SourceSymbol = "Consumer.UseKeyword",
                    SourcePath = "src/Consumer.cs",
                    TargetSymbol = "Service.ExactMatchKeyword",
                    CommitSha = "abc"
                }
            };

            WriteJsonl(Path.Combine(indexDir, "symbols.jsonl"), symbols);
            WriteJsonl(Path.Combine(indexDir, "edges.jsonl"), edges);
        }

        private static void WriteJsonl<T>(string path, IReadOnlyList<T> records)
        {
            using (StreamWriter writer = new StreamWriter(path))
            {
                foreach (T record in records)
                {
                    writer.WriteLine(JsonSerializer.Serialize(record, _IndexJsonOptions));
                }
            }
        }

        private static List<CodeIndexRecord> BuildLargeRecords(string vesselId, int documentCount, out string topPath)
        {
            topPath = "src/TopMatch.cs";
            List<CodeIndexRecord> records = new List<CodeIndexRecord>(documentCount);
            for (int i = 0; i < documentCount; i++)
            {
                string path = i == 0 ? topPath : "src/File" + i.ToString() + ".cs";
                string content = i == 0
                    ? "public class TopMatch { public void ExactMatchKeyword() { } }"
                    : "public class File" + i.ToString() + " { public void Work" + i.ToString() + "() { } }";

                records.Add(new CodeIndexRecord
                {
                    VesselId = vesselId,
                    Path = path,
                    CommitSha = "deadbeef",
                    ContentHash = "h" + i.ToString(),
                    Language = "csharp",
                    StartLine = 1,
                    EndLine = 5,
                    IsReferenceOnly = false,
                    Content = content
                });
            }

            return records;
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string repositoryPath)
        {
            Vessel vessel = new Vessel
            {
                Name = "fast-pack-vessel-" + Guid.NewGuid().ToString("N"),
                RepoUrl = repositoryPath,
                WorkingDirectory = repositoryPath,
                DefaultBranch = "main"
            };

            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<TestRepository> CreateRepositoryAsync()
        {
            string root = NewTempDirectory("armada-fast-pack-repo-");
            string repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);

            try
            {
                await RunGitAsync(repo, "init", "-b", "main").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                Directory.CreateDirectory(Path.Combine(repo, "src"));
                Directory.CreateDirectory(Path.Combine(repo, "docs"));

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "src", "CodeIndexTarget.cs"),
                    "namespace Sample\n{\n    public class CodeIndexTarget\n    {\n        public string SearchKeyword() => \"dispatch evidence\";\n    }\n}\n").ConfigureAwait(false);

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "docs", "usage.md"),
                    "# Usage\n\nThis document mentions context packs for mission briefs.\n").ConfigureAwait(false);

                await RunGitAsync(repo, "add", ".").ConfigureAwait(false);
                await RunGitAsync(repo, "commit", "-m", "Initial indexed fixture").ConfigureAwait(false);
                string commitSha = (await RunGitAsync(repo, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                return new TestRepository(root, repo, commitSha);
            }
            catch
            {
                TryDeleteDirectory(root);
                throw;
            }
        }

        private static string NewTempDirectory(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }

        private static LoggingModule SilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git failed (exit " + process.ExitCode + "): " + stderr.Trim());
                }

                return stdout;
            }
        }

        private sealed class TestRepository
        {
            public string Root { get; }

            public string Path { get; }

            public string CommitSha { get; }

            public TestRepository(string root, string path, string commitSha)
            {
                Root = root;
                Path = path;
                CommitSha = commitSha;
            }
        }

        private sealed class CountingEmbeddingClient : IEmbeddingClient
        {
            private readonly float[] _Vector;

            public int CallCount { get; private set; }

            public CountingEmbeddingClient(float[] vector)
            {
                _Vector = vector ?? throw new ArgumentNullException(nameof(vector));
            }

            public Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                CallCount++;
                return Task.FromResult(_Vector);
            }
        }

        private sealed class SlowEmbeddingClient : IEmbeddingClient
        {
            private readonly float[] _Vector;
            private readonly int _DelayMs;

            public SlowEmbeddingClient(float[] vector, int delayMs)
            {
                _Vector = vector ?? throw new ArgumentNullException(nameof(vector));
                _DelayMs = delayMs;
            }

            public async Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                await Task.Delay(_DelayMs, token).ConfigureAwait(false);
                return _Vector;
            }
        }

        private sealed class DelayingInferenceClient : IInferenceClient
        {
            private readonly int _DelayMs;
            private readonly string _Summary;

            public int CallCount { get; private set; }

            public DelayingInferenceClient(int delayMs, string summary)
            {
                _DelayMs = delayMs;
                _Summary = summary ?? throw new ArgumentNullException(nameof(summary));
            }

            public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken token = default)
            {
                CallCount++;
                // Honor only the caller token: the production summarizer passes the caller token here while
                // bounding completion via a budget-linked delay race, so a budget expiry must cut the wait
                // without this completion ever returning or faulting.
                await Task.Delay(_DelayMs, token).ConfigureAwait(false);
                return _Summary;
            }
        }

        #endregion
    }
}
