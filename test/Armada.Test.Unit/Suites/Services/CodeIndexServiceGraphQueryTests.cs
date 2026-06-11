namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Coverage for graph query APIs on top of symbols/edges sidecars.
    /// </summary>
    public class CodeIndexServiceGraphQueryTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <inheritdoc />
        public override string Name => "Code Index Service Graph Queries";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("GetCallersAsync and GetCalleesAsync return deterministic direct neighbors", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neighbors-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphNeighborsResponse callers = await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            Limit = 10
                        }).ConfigureAwait(false);

                        CodeGraphNeighborsResponse callersAgain = await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertEqual(3, callers.Results.Count);
                        AssertEqual("Armada.App.Api.Handler.Invoke", callers.Results[0].Symbol.QualifiedName);
                        AssertEqual("Armada.App.Worker.Helper", callers.Results[1].Symbol.QualifiedName);
                        AssertEqual("Armada.Tests.WorkerTests.Run_should_dispatch", callers.Results[2].Symbol.QualifiedName);
                        AssertEqual(callers.Results[0].Symbol.QualifiedName, callersAgain.Results[0].Symbol.QualifiedName);
                        AssertEqual(callers.Results[1].Symbol.QualifiedName, callersAgain.Results[1].Symbol.QualifiedName);
                        AssertEqual(callers.Results[2].Symbol.QualifiedName, callersAgain.Results[2].Symbol.QualifiedName);

                        CodeGraphNeighborsResponse callees = await service.GetCalleesAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertEqual(1, callees.Results.Count);
                        AssertEqual("Armada.App.Worker.Helper", callees.Results[0].Symbol.QualifiedName);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetImpactAsync bounds traversal with cycle protection", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-impact-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphImpactResponse impact = await service.GetImpactAsync(new CodeGraphImpactRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            Direction = CodeGraphTraversalDirectionEnum.Both,
                            MaxDepth = 2,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        AssertTrue(impact.Results.Count > 0, "impact traversal should return neighbors");
                        AssertFalse(impact.Results.Any(r => r.Symbol.QualifiedName == "Armada.App.Service.Execute"),
                            "seed symbol should not be returned as impacted due to cycle");
                        AssertTrue(impact.Results.All(r => r.MinDepth <= 2), "all impact rows should respect max depth");
                        AssertEqual(
                            impact.Results.Select(r => r.Symbol.QualifiedName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                            impact.Results.Count,
                            "impact results should be de-duplicated by symbol");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SuggestAffectedTestsAsync ranks explicit test signals before conventions", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-tests-explicit-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphAffectedTestsResponse response = await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        AssertTrue(response.Candidates.Count >= 2, "expected explicit and convention candidates");
                        AssertTrue(response.Candidates[0].IsExplicitSignal, "first candidate should be explicit");
                        AssertContains("test/WorkerTests.cs", response.Candidates[0].TestPath);
                        AssertFalse(response.Candidates[1].IsExplicitSignal, "second candidate should be convention fallback");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SuggestAffectedTestsAsync uses filename convention fallback when explicit signals absent", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-tests-fallback-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: false);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphAffectedTestsResponse response = await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        AssertTrue(response.Candidates.Count >= 1, "fallback should still produce candidates");
                        AssertContains("WorkerSpec.cs", response.Candidates[0].TestPath);
                        AssertFalse(response.Candidates[0].IsExplicitSignal, "fallback candidate should be convention based");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchSymbolsAsync warns when graph sidecars are missing", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-missing-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: false, includeExplicitTest: false);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphSymbolSearchResponse response = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Execute",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertEqual(0, response.Results.Count);
                        AssertTrue(response.Warnings.Any(w => w.Contains("symbols.jsonl", StringComparison.OrdinalIgnoreCase)),
                            "missing symbols sidecar warning expected");
                        AssertTrue(response.Warnings.Any(w => w.Contains("edges.jsonl", StringComparison.OrdinalIgnoreCase)),
                            "missing edges sidecar warning expected");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchSymbolsAsync warns on stale metadata freshness", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-stale-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Stale", includeGraphFiles: true, includeExplicitTest: false);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphSymbolSearchResponse response = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Worker",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertTrue(response.Results.Count > 0, "stale index should still be queryable");
                        AssertTrue(response.Warnings.Any(w => w.Contains("freshness is Stale", StringComparison.OrdinalIgnoreCase)),
                            "stale warning expected");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GraphQueryContext is cached on repeated same-vessel same-commit queries", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-cache-reuse-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);

                        // First query loads and caches the context.
                        CodeGraphNeighborsResponse first = await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            Limit = 10
                        }).ConfigureAwait(false);

                        // Delete sidecar files to prove the second query cannot re-read them.
                        string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
                        File.Delete(Path.Combine(indexDir, "symbols.jsonl"));
                        File.Delete(Path.Combine(indexDir, "edges.jsonl"));

                        // Second query for the same vessel and commit SHA must use the cached context.
                        CodeGraphNeighborsResponse second = await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertEqual(first.Results.Count, second.Results.Count, "cached context should return same result count");
                        for (int i = 0; i < first.Results.Count; i++)
                        {
                            AssertEqual(first.Results[i].Symbol.QualifiedName, second.Results[i].Symbol.QualifiedName,
                                "cached context should return same symbol at index " + i);
                        }
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GraphQueryContext cache is invalidated when IndexedCommitSha changes", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-cache-invalidate-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);

                        // First fixture: SHA "aaa111", symbol "Execute" present, no "UniqueNewSymbol".
                        List<CodeGraphSymbolRecord> firstSymbols = new List<CodeGraphSymbolRecord>
                        {
                            new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "aaa111", Path = "src/Service.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Execute", QualifiedName = "Armada.App.Service.Execute", StartLine = 1, EndLine = 5, ContentHash = "h1" }
                        };
                        List<CodeGraphEdgeRecord> firstEdges = new List<CodeGraphEdgeRecord>();
                        WriteGraphFixtureCustom(settings, vessel, "aaa111", "Fresh", firstSymbols, firstEdges);

                        CodeIndexService service = CreateService(db, settings);

                        CodeGraphSymbolSearchResponse first = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Execute",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(first.Results.Count > 0, "first query should find Execute");

                        // Second fixture: SHA "bbb222" -- cache miss must rebuild and expose the new symbol.
                        List<CodeGraphSymbolRecord> secondSymbols = new List<CodeGraphSymbolRecord>
                        {
                            new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "bbb222", Path = "src/New.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "UniqueNewSymbol", QualifiedName = "Armada.App.New.UniqueNewSymbol", StartLine = 1, EndLine = 5, ContentHash = "h2" }
                        };
                        List<CodeGraphEdgeRecord> secondEdges = new List<CodeGraphEdgeRecord>();
                        WriteGraphFixtureCustom(settings, vessel, "bbb222", "Fresh", secondSymbols, secondEdges);

                        CodeGraphSymbolSearchResponse second = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "UniqueNewSymbol",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(second.Results.Count > 0, "query after SHA change should find UniqueNewSymbol from rebuilt context");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("Concurrent same-vessel same-commit graph loads are thread-safe and coalesce", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-cache-concurrent-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: false);

                        CodeIndexService service = CreateService(db, settings);

                        const int concurrency = 8;
                        Task<CodeGraphNeighborsResponse>[] tasks = new Task<CodeGraphNeighborsResponse>[concurrency];
                        for (int i = 0; i < concurrency; i++)
                        {
                            tasks[i] = service.GetCallersAsync(new CodeGraphNeighborsRequest
                            {
                                VesselId = vessel.Id,
                                Symbol = "Armada.App.Service.Execute",
                                Limit = 10
                            });
                        }
                        CodeGraphNeighborsResponse[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

                        // All concurrent callers must succeed with consistent results.
                        int expectedCount = results[0].Results.Count;
                        foreach (CodeGraphNeighborsResponse result in results)
                        {
                            AssertEqual(expectedCount, result.Results.Count, "all concurrent callers should return the same result count");
                        }

                        // Deleting sidecars now verifies the context is cached: a subsequent
                        // query must return from cache rather than attempting a file read.
                        string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
                        File.Delete(Path.Combine(indexDir, "symbols.jsonl"));
                        File.Delete(Path.Combine(indexDir, "edges.jsonl"));

                        CodeGraphNeighborsResponse afterDelete = await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertEqual(expectedCount, afterDelete.Results.Count,
                            "post-concurrent cache should serve same result count as concurrent loads");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("Graph context cache entries are isolated per vessel", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-cache-isolation-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vesselA = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        Vessel vesselB = await CreateFixtureVesselAsync(db).ConfigureAwait(false);

                        List<CodeGraphSymbolRecord> symbolsA = new List<CodeGraphSymbolRecord>
                        {
                            new CodeGraphSymbolRecord { VesselId = vesselA.Id, CommitSha = "sha-aaa", Path = "src/A.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "AlphaUniqueProcOne", QualifiedName = "Armada.IsoA.Alpha.AlphaUniqueProcOne", StartLine = 1, EndLine = 5, ContentHash = "ha" }
                        };
                        List<CodeGraphSymbolRecord> symbolsB = new List<CodeGraphSymbolRecord>
                        {
                            new CodeGraphSymbolRecord { VesselId = vesselB.Id, CommitSha = "sha-bbb", Path = "src/B.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "BetaDistinctRunner", QualifiedName = "Armada.IsoB.Beta.BetaDistinctRunner", StartLine = 1, EndLine = 5, ContentHash = "hb" }
                        };
                        WriteGraphFixtureCustom(settings, vesselA, "sha-aaa", "Fresh", symbolsA, new List<CodeGraphEdgeRecord>());
                        WriteGraphFixtureCustom(settings, vesselB, "sha-bbb", "Fresh", symbolsB, new List<CodeGraphEdgeRecord>());

                        CodeIndexService service = CreateService(db, settings);

                        CodeGraphSymbolSearchResponse firstA = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vesselA.Id,
                            Query = "AlphaUniqueProcOne",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(firstA.Results.Count > 0, "vessel A query should find vessel A symbol");

                        // Vessel B must build its own context, not be served vessel A's cached entry.
                        CodeGraphSymbolSearchResponse crossB = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vesselB.Id,
                            Query = "AlphaUniqueProcOne",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertEqual(0, crossB.Results.Count, "vessel B context should not contain vessel A symbols");

                        CodeGraphSymbolSearchResponse firstB = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vesselB.Id,
                            Query = "BetaDistinctRunner",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(firstB.Results.Count > 0, "vessel B query should find vessel B symbol");

                        // Deleting vessel A sidecars proves both vessels are served from their own cache entries.
                        string indexDirA = Path.Combine(settings.CodeIndex.IndexDirectory, vesselA.Id);
                        File.Delete(Path.Combine(indexDirA, "symbols.jsonl"));
                        File.Delete(Path.Combine(indexDirA, "edges.jsonl"));

                        CodeGraphSymbolSearchResponse secondA = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vesselA.Id,
                            Query = "AlphaUniqueProcOne",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(secondA.Results.Count > 0, "vessel A should be served from its own cache entry after sidecar deletion");

                        CodeGraphSymbolSearchResponse secondB = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vesselB.Id,
                            Query = "BetaDistinctRunner",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(secondB.Results.Count > 0, "vessel B cache entry should be unaffected by vessel A activity");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("Cache invalidation replaces the prior context instead of merging", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-cache-replace-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);

                        List<CodeGraphSymbolRecord> oldSymbols = new List<CodeGraphSymbolRecord>
                        {
                            new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "cafe01", Path = "src/Old.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "OldOnlySymbol", QualifiedName = "Armada.App.Old.OldOnlySymbol", StartLine = 1, EndLine = 5, ContentHash = "h1" }
                        };
                        WriteGraphFixtureCustom(settings, vessel, "cafe01", "Fresh", oldSymbols, new List<CodeGraphEdgeRecord>());

                        CodeIndexService service = CreateService(db, settings);

                        CodeGraphSymbolSearchResponse beforeInvalidation = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "OldOnlySymbol",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(beforeInvalidation.Results.Count > 0, "old symbol should be found before invalidation");

                        List<CodeGraphSymbolRecord> newSymbols = new List<CodeGraphSymbolRecord>
                        {
                            new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "cafe02", Path = "src/New.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "NewOnlySymbol", QualifiedName = "Armada.App.New.NewOnlySymbol", StartLine = 1, EndLine = 5, ContentHash = "h2" }
                        };
                        WriteGraphFixtureCustom(settings, vessel, "cafe02", "Fresh", newSymbols, new List<CodeGraphEdgeRecord>());

                        // Rebuilt context must fully replace the old one, not merge with it.
                        CodeGraphSymbolSearchResponse oldAfterInvalidation = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "OldOnlySymbol",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertEqual(0, oldAfterInvalidation.Results.Count, "old symbol should be gone after SHA change rebuild");

                        CodeGraphSymbolSearchResponse newAfterInvalidation = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "NewOnlySymbol",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(newAfterInvalidation.Results.Count > 0, "new symbol should be found after SHA change rebuild");

                        // The replacement entry itself must be cached.
                        string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
                        File.Delete(Path.Combine(indexDir, "symbols.jsonl"));
                        File.Delete(Path.Combine(indexDir, "edges.jsonl"));

                        CodeGraphSymbolSearchResponse cachedNew = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "NewOnlySymbol",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(cachedNew.Results.Count > 0, "replacement entry should be served from cache after sidecar deletion");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("Cache hit compares IndexedCommitSha case-insensitively", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-cache-shacase-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);

                        List<CodeGraphSymbolRecord> symbols = new List<CodeGraphSymbolRecord>
                        {
                            new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "abc123", Path = "src/Probe.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "CaseProbeSymbol", QualifiedName = "Armada.App.Probe.CaseProbeSymbol", StartLine = 1, EndLine = 5, ContentHash = "h1" }
                        };
                        WriteGraphFixtureCustom(settings, vessel, "abc123", "Fresh", symbols, new List<CodeGraphEdgeRecord>());

                        CodeIndexService service = CreateService(db, settings);

                        CodeGraphSymbolSearchResponse first = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "CaseProbeSymbol",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(first.Results.Count > 0, "first query should find the probe symbol");

                        // Re-case the SHA in metadata and delete sidecars: a case-sensitive compare
                        // would rebuild from the missing files and return zero results with warnings.
                        WriteMetadataOnly(settings, vessel, "ABC123", "Fresh");
                        string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
                        File.Delete(Path.Combine(indexDir, "symbols.jsonl"));
                        File.Delete(Path.Combine(indexDir, "edges.jsonl"));

                        CodeGraphSymbolSearchResponse second = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "CaseProbeSymbol",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertEqual(first.Results.Count, second.Results.Count, "case-differing SHA should still be a cache hit");
                        AssertFalse(second.Warnings.Any(w => w.Contains("sidecar missing", StringComparison.OrdinalIgnoreCase)),
                            "cache hit should not have re-read the deleted sidecars");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("Empty IndexedCommitSha is normalized and cached", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-cache-emptysha-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);

                        List<CodeGraphSymbolRecord> symbols = new List<CodeGraphSymbolRecord>
                        {
                            new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "", Path = "src/Empty.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "EmptyShaSymbol", QualifiedName = "Armada.App.Empty.EmptyShaSymbol", StartLine = 1, EndLine = 5, ContentHash = "h1" }
                        };
                        WriteGraphFixtureCustom(settings, vessel, "", "Fresh", symbols, new List<CodeGraphEdgeRecord>());

                        CodeIndexService service = CreateService(db, settings);

                        CodeGraphSymbolSearchResponse first = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "EmptyShaSymbol",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(first.Results.Count > 0, "query with empty indexed SHA should still work");

                        // The empty SHA must be a stable cache key, not a permanent cache miss.
                        string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
                        File.Delete(Path.Combine(indexDir, "symbols.jsonl"));
                        File.Delete(Path.Combine(indexDir, "edges.jsonl"));

                        CodeGraphSymbolSearchResponse second = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "EmptyShaSymbol",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(second.Results.Count > 0, "empty-SHA entry should be served from cache after sidecar deletion");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("Pre-cancelled token aborts graph load and does not poison the cache", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-cache-cancel-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: false);

                        CodeIndexService service = CreateService(db, settings);

                        using (CancellationTokenSource cts = new CancellationTokenSource())
                        {
                            cts.Cancel();
                            bool threw = false;
                            try
                            {
                                await service.GetCallersAsync(new CodeGraphNeighborsRequest
                                {
                                    VesselId = vessel.Id,
                                    Symbol = "Armada.App.Service.Execute",
                                    Limit = 10
                                }, cts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                threw = true;
                            }
                            AssertTrue(threw, "pre-cancelled token should abort the cold-cache graph load");
                        }

                        // The aborted load must not leave the per-vessel lock held or a torn entry behind.
                        CodeGraphNeighborsResponse recovered = await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(recovered.Results.Count > 0, "subsequent query should succeed after a cancelled load");

                        // And the successful build must have populated the cache.
                        string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
                        File.Delete(Path.Combine(indexDir, "symbols.jsonl"));
                        File.Delete(Path.Combine(indexDir, "edges.jsonl"));

                        CodeGraphNeighborsResponse cached = await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertEqual(recovered.Results.Count, cached.Results.Count, "recovered build should be cached normally");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("Cached context preserves build warnings without re-reading sidecars", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-cache-warnings-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Stale", includeGraphFiles: true, includeExplicitTest: false);

                        CodeIndexService service = CreateService(db, settings);

                        CodeGraphSymbolSearchResponse first = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Worker",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertTrue(first.Results.Count > 0, "stale index should still be queryable");
                        AssertTrue(first.Warnings.Any(w => w.Contains("freshness is Stale", StringComparison.OrdinalIgnoreCase)),
                            "stale warning expected on first build");

                        string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
                        File.Delete(Path.Combine(indexDir, "symbols.jsonl"));
                        File.Delete(Path.Combine(indexDir, "edges.jsonl"));

                        // A cached context keeps its build-time warnings; a rebuild would add
                        // missing-sidecar warnings and lose the symbols.
                        CodeGraphSymbolSearchResponse second = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Worker",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertEqual(first.Results.Count, second.Results.Count, "cached stale context should serve same results");
                        AssertTrue(second.Warnings.Any(w => w.Contains("freshness is Stale", StringComparison.OrdinalIgnoreCase)),
                            "stale warning should be preserved in the cached context");
                        AssertFalse(second.Warnings.Any(w => w.Contains("sidecar missing", StringComparison.OrdinalIgnoreCase)),
                            "cache hit should not have re-read the deleted sidecars");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });
        }

        private static CodeIndexService CreateService(TestDatabase db, ArmadaSettings settings)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new CodeIndexService(logging, db.Driver, settings, new GitService(logging), null, null);
        }

        private static ArmadaSettings BuildSettings(string dataRoot)
        {
            CodeIndexSettings codeIndex = new CodeIndexSettings
            {
                IndexDirectory = Path.Combine(dataRoot, "code-index"),
                MaxChunkLines = 50,
                MaxSearchResults = 10,
                MaxContextPackResults = 8,
                UseSemanticSearch = false
            };

            ArmadaSettings settings = new ArmadaSettings
            {
                DataDirectory = Path.Combine(dataRoot, "data"),
                ReposDirectory = Path.Combine(dataRoot, "repos"),
                CodeIndex = codeIndex
            };
            settings.InitializeDirectories();
            return settings;
        }

        private static async Task<Vessel> CreateFixtureVesselAsync(TestDatabase db)
        {
            Vessel vessel = new Vessel
            {
                Name = "graph-query-vessel-" + Guid.NewGuid().ToString("N"),
                RepoUrl = "",
                WorkingDirectory = "",
                DefaultBranch = "main"
            };
            return await db.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static void WriteGraphFixture(
            ArmadaSettings settings,
            Vessel vessel,
            string freshness,
            bool includeGraphFiles,
            bool includeExplicitTest)
        {
            string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
            Directory.CreateDirectory(indexDir);

            CodeIndexStatus status = new CodeIndexStatus
            {
                VesselId = vessel.Id,
                VesselName = vessel.Name,
                DefaultBranch = vessel.DefaultBranch,
                IndexedCommitSha = "deadbeef",
                CurrentCommitSha = "",
                IndexedAtUtc = DateTime.UtcNow,
                Freshness = freshness,
                DocumentCount = 4,
                ChunkCount = 8,
                IndexDirectory = indexDir,
                LastError = null
            };

            File.WriteAllText(Path.Combine(indexDir, "metadata.json"), JsonSerializer.Serialize(status, _JsonOptions), Encoding.UTF8);

            if (!includeGraphFiles)
            {
                return;
            }

            List<CodeGraphSymbolRecord> symbols = new List<CodeGraphSymbolRecord>
            {
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Service.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Execute", QualifiedName = "Armada.App.Service.Execute", StartLine = 10, EndLine = 15, ContentHash = "h1" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Service.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Orchestrate", QualifiedName = "Armada.App.Service.Orchestrate", StartLine = 18, EndLine = 30, ContentHash = "h1" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Worker.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Run", QualifiedName = "Armada.App.Worker.Run", StartLine = 4, EndLine = 10, ContentHash = "h2" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Worker.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Helper", QualifiedName = "Armada.App.Worker.Helper", StartLine = 12, EndLine = 20, ContentHash = "h2" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/ApiHandler.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Invoke", QualifiedName = "Armada.App.Api.Handler.Invoke", StartLine = 8, EndLine = 14, ContentHash = "h3" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "spec/WorkerSpec.cs", Kind = CodeGraphSymbolKindEnum.Class, SimpleName = "WorkerSpec", QualifiedName = "Armada.Spec.WorkerSpec", StartLine = 1, EndLine = 20, ContentHash = "h4" }
            };

            if (includeExplicitTest)
            {
                symbols.Add(new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "test/WorkerTests.cs",
                    Kind = CodeGraphSymbolKindEnum.Method,
                    SimpleName = "Run_should_dispatch",
                    QualifiedName = "Armada.Tests.WorkerTests.Run_should_dispatch",
                    StartLine = 11,
                    EndLine = 25,
                    ContentHash = "h5"
                });
            }

            List<CodeGraphEdgeRecord> edges = new List<CodeGraphEdgeRecord>
            {
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Service.Execute", TargetSymbol = "Armada.App.Worker.Run", SourcePath = "src/Service.cs", SourceLine = 12 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Worker.Run", TargetSymbol = "Armada.App.Worker.Helper", SourcePath = "src/Worker.cs", SourceLine = 7 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Worker.Helper", TargetSymbol = "Armada.App.Service.Execute", SourcePath = "src/Worker.cs", SourceLine = 14 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Worker.Helper", TargetSymbol = "Armada.Spec.WorkerSpec", SourcePath = "src/Worker.cs", SourceLine = 15 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Api.Handler.Invoke", TargetSymbol = "Armada.App.Service.Execute", SourcePath = "src/ApiHandler.cs", SourceLine = 10 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Service.Orchestrate", TargetSymbol = "Armada.Spec.WorkerSpec", SourcePath = "src/Service.cs", SourceLine = 20 }
            };

            if (includeExplicitTest)
            {
                edges.Add(new CodeGraphEdgeRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Kind = CodeGraphEdgeKindEnum.Calls,
                    SourceSymbol = "Armada.Tests.WorkerTests.Run_should_dispatch",
                    TargetSymbol = "Armada.App.Service.Execute",
                    SourcePath = "test/WorkerTests.cs",
                    SourceLine = 14
                });
            }

            WriteJsonl(Path.Combine(indexDir, "symbols.jsonl"), symbols);
            WriteJsonl(Path.Combine(indexDir, "edges.jsonl"), edges);
        }

        private static void WriteGraphFixtureCustom(
            ArmadaSettings settings,
            Vessel vessel,
            string commitSha,
            string freshness,
            List<CodeGraphSymbolRecord> symbols,
            List<CodeGraphEdgeRecord> edges)
        {
            string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
            Directory.CreateDirectory(indexDir);

            CodeIndexStatus status = new CodeIndexStatus
            {
                VesselId = vessel.Id,
                VesselName = vessel.Name,
                DefaultBranch = vessel.DefaultBranch,
                IndexedCommitSha = commitSha,
                CurrentCommitSha = "",
                IndexedAtUtc = DateTime.UtcNow,
                Freshness = freshness,
                DocumentCount = symbols.Count,
                ChunkCount = symbols.Count * 2,
                IndexDirectory = indexDir,
                LastError = null
            };

            File.WriteAllText(Path.Combine(indexDir, "metadata.json"), JsonSerializer.Serialize(status, _JsonOptions), Encoding.UTF8);
            WriteJsonl(Path.Combine(indexDir, "symbols.jsonl"), symbols);
            WriteJsonl(Path.Combine(indexDir, "edges.jsonl"), edges);
        }

        private static void WriteMetadataOnly(
            ArmadaSettings settings,
            Vessel vessel,
            string commitSha,
            string freshness)
        {
            string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
            Directory.CreateDirectory(indexDir);

            CodeIndexStatus status = new CodeIndexStatus
            {
                VesselId = vessel.Id,
                VesselName = vessel.Name,
                DefaultBranch = vessel.DefaultBranch,
                IndexedCommitSha = commitSha,
                CurrentCommitSha = "",
                IndexedAtUtc = DateTime.UtcNow,
                Freshness = freshness,
                DocumentCount = 1,
                ChunkCount = 2,
                IndexDirectory = indexDir,
                LastError = null
            };

            File.WriteAllText(Path.Combine(indexDir, "metadata.json"), JsonSerializer.Serialize(status, _JsonOptions), Encoding.UTF8);
        }

        private static void WriteJsonl<T>(string path, List<T> items)
        {
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (T item in items)
                {
                    writer.WriteLine(JsonSerializer.Serialize(item, _JsonOptions));
                }
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
    }
}
