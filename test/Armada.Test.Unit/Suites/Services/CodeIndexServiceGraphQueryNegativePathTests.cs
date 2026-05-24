namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Negative-path and edge-case coverage for the symbol graph query APIs on
    /// <see cref="CodeIndexService"/>. Complements the Worker-authored happy-path
    /// suite <c>CodeIndexServiceGraphQueryTests</c>; focuses on argument validation,
    /// filtering, direction-specific traversal, vessel-id scoping, commit-mismatch
    /// warnings, depth/limit clamping, and unknown-symbol behavior.
    /// </summary>
    public class CodeIndexServiceGraphQueryNegativePathTests : TestSuite
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        #endregion

        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Code Index Service Graph Query Negative Paths";

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("SearchSymbolsAsync_NullRequest_Throws", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-null-search-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        CodeIndexService service = CreateService(db, settings);
                        await AssertThrowsAsync<ArgumentNullException>(async () =>
                            await service.SearchSymbolsAsync(null!).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchSymbolsAsync_EmptyVesselId_Throws", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-empty-vessel-search-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        CodeIndexService service = CreateService(db, settings);
                        await AssertThrowsAsync<ArgumentNullException>(async () =>
                            await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                            {
                                VesselId = "",
                                Query = "Foo"
                            }).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchSymbolsAsync_EmptyQuery_Throws", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-empty-query-search-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        CodeIndexService service = CreateService(db, settings);
                        await AssertThrowsAsync<ArgumentNullException>(async () =>
                            await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                            {
                                VesselId = vessel.Id,
                                Query = "   "
                            }).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetCallersAsync_EmptySymbol_Throws", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-empty-symbol-callers-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        CodeIndexService service = CreateService(db, settings);
                        await AssertThrowsAsync<ArgumentNullException>(async () =>
                            await service.GetCallersAsync(new CodeGraphNeighborsRequest
                            {
                                VesselId = vessel.Id,
                                Symbol = ""
                            }).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetImpactAsync_NullRequest_Throws", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-null-impact-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        CodeIndexService service = CreateService(db, settings);
                        await AssertThrowsAsync<ArgumentNullException>(async () =>
                            await service.GetImpactAsync(null!).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SuggestAffectedTestsAsync_EmptyVesselId_Throws", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-empty-vessel-affected-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        CodeIndexService service = CreateService(db, settings);
                        await AssertThrowsAsync<ArgumentNullException>(async () =>
                            await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                            {
                                VesselId = "",
                                Symbol = "Foo"
                            }).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchSymbolsAsync_PathPrefix_FiltersToMatchingPathsOnly", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-path-prefix-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);

                        CodeGraphSymbolSearchResponse response = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Worker",
                            Limit = 20,
                            PathPrefix = "test/"
                        }).ConfigureAwait(false);

                        AssertTrue(response.Results.Count > 0, "path-prefix filter should retain matching symbols");
                        AssertTrue(response.Results.All(r => r.Symbol.Path.StartsWith("test/", StringComparison.OrdinalIgnoreCase)),
                            "all results must live under the requested path prefix");
                        AssertFalse(response.Results.Any(r => r.Symbol.Path.StartsWith("src/", StringComparison.OrdinalIgnoreCase)),
                            "src/ paths must be filtered out by path prefix");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchSymbolsAsync_KindFilter_RestrictsToRequestedKind", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-kind-filter-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);

                        CodeGraphSymbolSearchResponse classOnly = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Worker",
                            Limit = 20,
                            Kind = CodeGraphSymbolKindEnum.Class
                        }).ConfigureAwait(false);

                        AssertTrue(classOnly.Results.Count > 0, "kind=Class filter should still match the WorkerSpec class");
                        AssertTrue(classOnly.Results.All(r => r.Symbol.Kind == CodeGraphSymbolKindEnum.Class),
                            "all results must be classes when kind=Class");
                        AssertFalse(classOnly.Results.Any(r => r.Symbol.Kind == CodeGraphSymbolKindEnum.Method),
                            "method symbols must be excluded when kind=Class");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchSymbolsAsync_RanksExactQualifiedAboveSimpleAndPrefixMatches", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-ranking-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);

                        CodeGraphSymbolSearchResponse response = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Armada.App.Service.Execute",
                            Limit = 5
                        }).ConfigureAwait(false);

                        AssertTrue(response.Results.Count > 0, "exact qualified query should produce at least one match");
                        AssertEqual("Armada.App.Service.Execute", response.Results[0].Symbol.QualifiedName);
                        AssertContains("exact qualified", response.Results[0].MatchReason);
                        for (int i = 1; i < response.Results.Count; i++)
                        {
                            AssertTrue(response.Results[0].Score >= response.Results[i].Score,
                                "exact qualified match must outrank all other matches");
                        }
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchSymbolsAsync_UnknownQuery_ReturnsEmptyResultsWithoutErrors", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-no-match-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphSymbolSearchResponse response = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "zzzzz_no_such_symbol_zzzzz",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertEqual(0, response.Results.Count);
                        AssertNotNull(response.Status, "status must still be populated for empty matches");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("LoadGraphQueryContext_FiltersForeignVesselIdRecords", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-vessel-scope-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        AppendForeignVesselRecords(settings, vessel);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphSymbolSearchResponse response = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Foreign",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertEqual(0, response.Results.Count);
                        AssertFalse(response.Results.Any(r =>
                            String.Equals(r.Symbol.QualifiedName, "Foreign.Vessel.Symbol", StringComparison.OrdinalIgnoreCase)),
                            "foreign-vessel symbol must not surface in this vessel's results");

                        CodeGraphNeighborsResponse callers = await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Foreign.Vessel.Symbol",
                            Limit = 10
                        }).ConfigureAwait(false);
                        AssertEqual(0, callers.Results.Count);
                        AssertEqual(0, callers.ResolvedSeedSymbols.Count);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("LoadGraphQueryContext_WarnsWhenSidecarCommitDoesNotMatchMetadata", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-commit-mismatch-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: false, sidecarCommitSha: "feedface");

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphSymbolSearchResponse response = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Worker",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertTrue(response.Warnings.Any(w =>
                                w.Contains("symbols sidecar commit", StringComparison.OrdinalIgnoreCase)),
                            "symbols-side commit-mismatch warning expected");
                        AssertTrue(response.Warnings.Any(w =>
                                w.Contains("edges sidecar commit", StringComparison.OrdinalIgnoreCase)),
                            "edges-side commit-mismatch warning expected");
                        AssertTrue(response.Results.Count > 0,
                            "stale-commit warning must not suppress query results");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("LoadGraphQueryContext_WarnsWhenSidecarsAreEmpty", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-empty-sidecars-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteEmptySidecarFixture(settings, vessel);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphSymbolSearchResponse response = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "anything",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertEqual(0, response.Results.Count);
                        AssertTrue(response.Warnings.Any(w => w.Contains("symbols sidecar is empty", StringComparison.OrdinalIgnoreCase)),
                            "empty-symbols warning expected");
                        AssertTrue(response.Warnings.Any(w => w.Contains("edges sidecar is empty", StringComparison.OrdinalIgnoreCase)),
                            "empty-edges warning expected");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetImpactAsync_DirectionCallersOnly_ExcludesCalleeSide", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-callers-only-");
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
                            Symbol = "Armada.App.Worker.Run",
                            Direction = CodeGraphTraversalDirectionEnum.Callers,
                            MaxDepth = 3,
                            MaxResults = 20
                        }).ConfigureAwait(false);

                        AssertTrue(impact.Results.Count > 0, "callers-only traversal should still surface upstream symbols");
                        AssertTrue(impact.Results.Any(r => r.Symbol.QualifiedName == "Armada.App.Service.Execute"),
                            "Service.Execute calls Worker.Run and must appear as a caller");
                        AssertTrue(impact.Results.Any(r => r.Symbol.QualifiedName == "Armada.App.Api.Handler.Invoke"),
                            "Api.Handler.Invoke transitively calls Worker.Run via Service.Execute and must appear as a caller");
                        AssertFalse(impact.Results.Any(r => r.Symbol.QualifiedName == "Armada.Spec.WorkerSpec"),
                            "WorkerSpec is a pure callee (never a source) and must NOT appear with Direction=Callers");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetImpactAsync_DirectionCalleesOnly_ExcludesCallerSide", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-callees-only-");
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
                            Symbol = "Armada.App.Worker.Run",
                            Direction = CodeGraphTraversalDirectionEnum.Callees,
                            MaxDepth = 3,
                            MaxResults = 20
                        }).ConfigureAwait(false);

                        AssertTrue(impact.Results.Count > 0, "callees-only traversal should surface downstream symbols");
                        AssertTrue(impact.Results.Any(r => r.Symbol.QualifiedName == "Armada.App.Worker.Helper"),
                            "Worker.Helper is a direct callee and must appear with Direction=Callees");
                        AssertFalse(impact.Results.Any(r => r.Symbol.QualifiedName == "Armada.App.Api.Handler.Invoke"),
                            "Api.Handler.Invoke is an upstream caller and must NOT appear with Direction=Callees");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetImpactAsync_MaxDepthOne_LimitsToDirectHopsOnly", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-depth-one-");
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
                            Direction = CodeGraphTraversalDirectionEnum.Callees,
                            MaxDepth = 1,
                            MaxResults = 20
                        }).ConfigureAwait(false);

                        AssertTrue(impact.Results.All(r => r.MinDepth == 1),
                            "all hits must be exactly one hop from the seed when MaxDepth=1");
                        AssertTrue(impact.Results.Any(r => r.Symbol.QualifiedName == "Armada.App.Worker.Run"),
                            "Worker.Run is a direct callee of Service.Execute and must appear at depth 1");
                        AssertFalse(impact.Results.Any(r => r.Symbol.QualifiedName == "Armada.App.Worker.Helper"),
                            "Worker.Helper is two hops deep and must NOT appear when MaxDepth=1");
                        AssertEqual(1, impact.MaxDepth, "effective MaxDepth should round-trip when within bounds");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetImpactAsync_NonPositiveDepth_FallsBackToDefaultBound", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-depth-default-");
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
                            MaxDepth = 0,
                            MaxResults = 0
                        }).ConfigureAwait(false);

                        AssertEqual(3, impact.MaxDepth, "MaxDepth=0 must clamp up to the service default (3)");
                        AssertTrue(impact.Results.Count > 0,
                            "MaxResults=0 must still produce at least one result by falling back to the default limit");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetImpactAsync_HugeDepthAndLimit_AreClampedToServiceCeiling", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-clamp-ceiling-");
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
                            MaxDepth = 10000,
                            MaxResults = 100000
                        }).ConfigureAwait(false);

                        AssertTrue(impact.MaxDepth <= 8, "MaxDepth must be clamped down to the _MaxGraphDepth ceiling (8)");
                        AssertTrue(impact.MaxDepth > 0, "clamp must not yield a non-positive depth");
                        AssertTrue(impact.Results.Count <= 200,
                            "MaxResults must be clamped down to the _MaxGraphResults ceiling (200)");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetCallersAsync_UnknownSymbol_ReturnsEmptyResultsAndNoSeeds", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-unknown-symbol-");
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
                            Symbol = "Nope.Does.Not.Exist",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertEqual(0, callers.Results.Count);
                        AssertEqual(0, callers.ResolvedSeedSymbols.Count);
                        AssertNotNull(callers.Status);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetCallersAsync_NonPositiveLimit_AppliesServiceDefault", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-callers-limit-default-");
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
                            Limit = 0
                        }).ConfigureAwait(false);

                        AssertTrue(callers.Results.Count > 0,
                            "Limit=0 must not zero out results; service should fall back to the default neighbor limit");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SuggestAffectedTestsAsync_MissingGraphSidecars_ReturnsEmptyWithWarnings", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-affected-missing-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: false, includeExplicitTest: false);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphAffectedTestsResponse response = await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        AssertEqual(0, response.Candidates.Count);
                        AssertEqual(0, response.ResolvedSeedSymbols.Count);
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

            await RunTest("SuggestAffectedTestsAsync_EvidenceDepth_IsBoundedByMaxDepth", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-neg-evidence-depth-");
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

                        AssertTrue(response.Candidates.Count > 0, "fixture should yield at least one test candidate");
                        AssertTrue(response.Candidates.All(c => c.EvidenceDepth >= 0 && c.EvidenceDepth <= 3),
                            "every candidate's evidence depth must lie within [0, MaxDepth]");
                        AssertTrue(response.Candidates.All(c => !String.IsNullOrWhiteSpace(c.TestPath)),
                            "every candidate must have a TestPath");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GraphQueryMethods_DoNotCloneOrMutateVesselState", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-readonly-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        // vessel has empty RepoUrl, empty WorkingDirectory, null LocalPath
                        AssertTrue(String.IsNullOrEmpty(vessel.RepoUrl), "fixture vessel should have no RepoUrl");
                        AssertTrue(String.IsNullOrEmpty(vessel.WorkingDirectory), "fixture vessel should have no WorkingDirectory");
                        AssertTrue(String.IsNullOrEmpty(vessel.LocalPath), "fixture vessel should have no LocalPath");

                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: false);

                        CodeIndexService service = CreateService(db, settings);

                        await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Execute"
                        }).ConfigureAwait(false);

                        await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run"
                        }).ConfigureAwait(false);

                        await service.GetCalleesAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run"
                        }).ConfigureAwait(false);

                        await service.GetImpactAsync(new CodeGraphImpactRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        // Re-read vessel from DB and assert it was not mutated
                        Vessel? after = await db.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                        AssertTrue(after != null, "vessel must still exist in DB after graph queries");
                        AssertTrue(String.IsNullOrEmpty(after!.LocalPath),
                            "graph queries must not set LocalPath (would indicate a clone occurred)");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SuggestAffectedTestsAsync_ConventionOnlyFallback_FindsNonReachableTestFile", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-convention-only-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteConventionOnlyFixture(settings, vessel);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphAffectedTestsResponse response = await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        // test/WorkerTests.cs exists in symbols.jsonl but has no edge to the target symbol.
                        // The convention-only fallback should still suggest it because "WorkerTests" contains "Worker".
                        AssertTrue(response.Candidates.Count > 0,
                            "convention-only fallback should yield at least one candidate");
                        AssertTrue(
                            response.Candidates.Any(c => c.TestPath.Contains("WorkerTests", StringComparison.OrdinalIgnoreCase)),
                            "non-graph-reachable WorkerTests.cs must appear via convention-only fallback");
                        AssertTrue(
                            response.Candidates.All(c => !c.IsExplicitSignal),
                            "all candidates should be convention matches, not explicit signals");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GraphQueryMethods_DoNotInvokeGitCloneEvenWhenVesselHasRepoUrl", async () =>
            {
                // Stronger no-mutate proof than the LocalPath check: a stub git service records
                // every CloneBareAsync call. With vessel.RepoUrl populated, the OLD code path
                // (GetStatusAsync -> TryResolveCurrentCommitAsync -> ResolveRepositoryPathAsync)
                // would have invoked CloneBareAsync. After the fix, the read-only status loader
                // never reaches that code, so the call list must stay empty across all five
                // graph query APIs.
                string dataRoot = NewTempDirectory("armada-code-graph-query-git-noop-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        vessel.RepoUrl = "https://example.invalid/never-resolves.git";
                        vessel = await db.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                        AssertTrue(String.IsNullOrEmpty(vessel.LocalPath), "vessel must start with no LocalPath");

                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        StubGitService stub = new StubGitService();
                        CodeIndexService service = CreateServiceWithGit(db, settings, stub);

                        await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Execute"
                        }).ConfigureAwait(false);
                        await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run"
                        }).ConfigureAwait(false);
                        await service.GetCalleesAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run"
                        }).ConfigureAwait(false);
                        await service.GetImpactAsync(new CodeGraphImpactRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);
                        await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        AssertEqual(0, stub.CloneCalls.Count);
                        AssertEqual(0, stub.OperationCalls.Count);
                        AssertEqual(0, stub.PushCalls.Count);
                        AssertEqual(0, stub.PullCalls.Count);
                        AssertEqual(0, stub.DiffCalls.Count);
                        AssertEqual(0, stub.WorktreeCalls.Count);

                        Vessel? after = await db.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                        AssertTrue(after != null, "vessel still present after graph queries");
                        AssertTrue(String.IsNullOrEmpty(after!.LocalPath),
                            "LocalPath must remain unset (would prove ResolveRepositoryPathAsync was called)");
                        AssertEqual(vessel.RepoUrl, after.RepoUrl);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SuggestAffectedTestsAsync_ConventionOnlyFallback_SkippedWhenExplicitCandidateExists", async () =>
            {
                // Pre-existing behavior: convention-only fallback runs only when explicitCandidates
                // is empty. After the worker added the context.Symbols sweep, that gate still
                // governs whether the new sweep runs. A disconnected test file with a matching
                // stem must NOT surface when an explicit test signal already exists.
                string dataRoot = NewTempDirectory("armada-code-graph-query-convention-suppressed-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteConventionWithExplicitFixture(settings, vessel);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphAffectedTestsResponse response = await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        AssertTrue(response.Candidates.Any(c => c.IsExplicitSignal),
                            "explicit reachable test candidate must surface");
                        AssertFalse(
                            response.Candidates.Any(c => c.TestPath.Contains("DisconnectedWorkerSpec", StringComparison.OrdinalIgnoreCase)),
                            "non-reachable convention test must NOT surface when explicit candidates exist");
                        AssertFalse(
                            response.Candidates.Any(c => !c.IsExplicitSignal),
                            "no convention-only candidates should appear when explicit candidates exist");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SuggestAffectedTestsAsync_ConventionOnlyFallback_StemMismatch_FiltersOutNonMatchingTestFile", async () =>
            {
                // The convention-only sweep only suggests a test file when its filename stem
                // contains the seed's filename stem. A disconnected test file whose stem does
                // not contain the seed stem must NOT be suggested even though its path looks
                // like a test path.
                string dataRoot = NewTempDirectory("armada-code-graph-query-convention-stem-mismatch-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteConventionStemMismatchFixture(settings, vessel);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphAffectedTestsResponse response = await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        AssertFalse(
                            response.Candidates.Any(c => c.TestPath.Contains("UnrelatedThingTest.cs", StringComparison.OrdinalIgnoreCase)),
                            "UnrelatedThingTest.cs has no stem overlap with Worker.cs and must NOT be suggested");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SuggestAffectedTestsAsync_ConventionOnlyFallback_NotDuplicatedWhenAlsoReachable", async () =>
            {
                // Guard for the reachablePaths.Contains check: when a test file is BOTH
                // graph-reachable (via an edge) AND present in context.Symbols with a
                // non-test sibling symbol on the same path, the new context.Symbols sweep
                // must skip the path so the candidate is not registered twice.
                string dataRoot = NewTempDirectory("armada-code-graph-query-convention-no-dup-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteConventionReachableSamePathFixture(settings, vessel);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphAffectedTestsResponse response = await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        int workerSpecBuckets = response.Candidates.Count(c =>
                            c.TestPath.Equals("test/WorkerSpec.cs", StringComparison.OrdinalIgnoreCase));
                        AssertEqual(1, workerSpecBuckets);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SuggestAffectedTestsAsync_ConventionOnlyFallback_SetsExpectedReasonAndZeroEvidenceDepth", async () =>
            {
                // The new context.Symbols sweep records:
                //   - reason = "filename convention matched seed file stem (not graph-reachable)"
                //   - MinDepth = Int32.MaxValue, which gets encoded as EvidenceDepth = 0
                // Pin the contract so refactors that drop or rewrite this reason text are noticed.
                string dataRoot = NewTempDirectory("armada-code-graph-query-convention-reason-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteConventionOnlyFixture(settings, vessel);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphAffectedTestsResponse response = await service.SuggestAffectedTestsAsync(new CodeGraphAffectedTestsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Worker.Run",
                            MaxDepth = 3,
                            MaxResults = 10
                        }).ConfigureAwait(false);

                        CodeGraphAffectedTestCandidate? workerTests = response.Candidates.FirstOrDefault(c =>
                            c.TestPath.Contains("WorkerTests", StringComparison.OrdinalIgnoreCase));
                        AssertNotNull(workerTests);
                        AssertFalse(workerTests!.IsExplicitSignal,
                            "convention-only candidate must not be flagged as explicit");
                        AssertEqual(0, workerTests.EvidenceDepth);
                        AssertTrue(workerTests.Reasons.Any(r =>
                                r.Contains("not graph-reachable", StringComparison.OrdinalIgnoreCase)),
                            "expected '(not graph-reachable)' marker in the reason text");
                        AssertTrue(workerTests.Score > 0,
                            "convention-only candidate score must be positive (sweep assigns 20)");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("LoadGraphQueryContext_PersistedStaleFreshness_StillSurfacesWarning", async () =>
            {
                // The read-only status loader skips TryResolveCurrentCommitAsync, so
                // CurrentCommitSha stays null and ResolveFreshness returns whatever the
                // persisted metadata.json recorded. Persisted "Stale" must still surface
                // as a freshness warning -- proves we did not silently coerce to Fresh.
                string dataRoot = NewTempDirectory("armada-code-graph-query-stale-freshness-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Stale", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphSymbolSearchResponse response = await service.SearchSymbolsAsync(new CodeGraphSymbolSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Worker",
                            Limit = 10
                        }).ConfigureAwait(false);

                        AssertTrue(response.Warnings.Any(w => w.Contains("freshness is Stale", StringComparison.OrdinalIgnoreCase)),
                            "persisted Stale freshness must surface as a warning through the read-only loader");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetNodeAsync_ReturnsNeighborsAndSourceExcerpt", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-node-source-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphNodeResponse response = await service.GetNodeAsync(new CodeGraphNodeRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Armada.App.Service.Execute",
                            IncludeSource = true,
                            SourcePadding = 1
                        }).ConfigureAwait(false);

                        AssertTrue(response.ResolvedSymbols.Count > 0, "node should resolve at least one symbol");
                        AssertTrue(response.Callers.Count > 0, "node should include direct callers");
                        AssertTrue(response.Callees.Count > 0, "node should include direct callees");
                        AssertNotNull(response.Source);
                        AssertContains("Execute", response.Source!.Content);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("ExploreAsync_GroupsSymbolsByFileAndReturnsRelationships", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-explore-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphExploreResponse response = await service.ExploreAsync(new CodeGraphExploreRequest
                        {
                            VesselId = vessel.Id,
                            Query = "Execute",
                            MaxDepth = 2,
                            MaxResults = 10,
                            IncludeSource = true
                        }).ConfigureAwait(false);

                        AssertTrue(response.Files.Count > 0, "explore should return grouped files");
                        AssertTrue(response.Relationships.Count > 0, "explore should return relationship edges");
                        AssertTrue(response.Files.Any(f => f.SourceSections.Count > 0), "explore should include source sections");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("GetFileStructureAsync_ReturnsGroupedSymbols", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-files-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphFileStructureResponse response = await service.GetFileStructureAsync(new CodeGraphFileStructureRequest
                        {
                            VesselId = vessel.Id,
                            PathPrefix = "src/",
                            IncludeSymbols = true,
                            Limit = 10
                        }).ConfigureAwait(false);

                        CodeGraphFileStructureEntry serviceFile = response.Files.First(f => f.Path == "src/Service.cs");
                        AssertTrue(serviceFile.SymbolCount >= 2, "file structure should group symbols by indexed file");
                        AssertTrue(serviceFile.Symbols.Any(s => s.QualifiedName == "Armada.App.Service.Execute"),
                            "file structure should include symbols when requested");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_UsesGraphExpansionWhenSidecarsResolveSeeds", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-context-pack-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteGraphFixture(settings, vessel, freshness: "Fresh", includeGraphFiles: true, includeExplicitTest: true);

                        CodeIndexService service = CreateService(db, settings);
                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "Change Execute behavior",
                            TokenBudget = 4000,
                            MaxResults = 4
                        }).ConfigureAwait(false);

                        AssertTrue(response.Metrics.GraphExpansionUsed, "context pack should report graph expansion");
                        AssertContains("## Symbol Graph Context", response.Markdown);
                        AssertTrue(response.Metrics.IncludedFiles.Any(f => f.Contains("Worker", StringComparison.OrdinalIgnoreCase)),
                            "graph expansion should contribute related files to metrics");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });
        }

        #endregion

        #region Private-Methods

        private static CodeIndexService CreateService(TestDatabase db, ArmadaSettings settings)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new CodeIndexService(logging, db.Driver, settings, new GitService(logging), null, null);
        }

        private static CodeIndexService CreateServiceWithGit(TestDatabase db, ArmadaSettings settings, IGitService git)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new CodeIndexService(logging, db.Driver, settings, git, null, null);
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
                Name = "graph-query-neg-vessel-" + Guid.NewGuid().ToString("N"),
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
            bool includeExplicitTest,
            string sidecarCommitSha = "deadbeef")
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

            List<CodeIndexRecord> records = new List<CodeIndexRecord>
            {
                new CodeIndexRecord
                {
                    VesselId = vessel.Id,
                    Path = "src/Service.cs",
                    CommitSha = "deadbeef",
                    ContentHash = "h1",
                    Language = "csharp",
                    StartLine = 1,
                    EndLine = 35,
                    Content = BuildSourceWithLines(35, new Dictionary<int, string>
                    {
                        [1] = "namespace Armada.App;",
                        [2] = "public class Service",
                        [3] = "{",
                        [10] = "    public void Execute()",
                        [11] = "    {",
                        [12] = "        var worker = new Worker();",
                        [13] = "        worker.Run();",
                        [14] = "    }",
                        [18] = "    public void Orchestrate()",
                        [19] = "    {",
                        [20] = "        new WorkerSpec().Run_should_dispatch();",
                        [21] = "    }",
                        [35] = "}"
                    })
                },
                new CodeIndexRecord
                {
                    VesselId = vessel.Id,
                    Path = "src/Worker.cs",
                    CommitSha = "deadbeef",
                    ContentHash = "h2",
                    Language = "csharp",
                    StartLine = 1,
                    EndLine = 25,
                    Content = BuildSourceWithLines(25, new Dictionary<int, string>
                    {
                        [1] = "namespace Armada.App;",
                        [2] = "public class Worker",
                        [3] = "{",
                        [4] = "    public void Run()",
                        [5] = "    {",
                        [7] = "        Helper();",
                        [10] = "    }",
                        [12] = "    public void Helper()",
                        [13] = "    {",
                        [14] = "        new Service().Execute();",
                        [15] = "        new WorkerSpec();",
                        [20] = "    }",
                        [25] = "}"
                    })
                },
                new CodeIndexRecord
                {
                    VesselId = vessel.Id,
                    Path = "test/WorkerTests.cs",
                    CommitSha = "deadbeef",
                    ContentHash = "h5",
                    Language = "csharp",
                    StartLine = 1,
                    EndLine = 25,
                    Content = BuildSourceWithLines(25, new Dictionary<int, string>
                    {
                        [1] = "namespace Armada.Tests;",
                        [2] = "public class WorkerTests",
                        [3] = "{",
                        [11] = "    public void Run_should_dispatch()",
                        [12] = "    {",
                        [14] = "        new Armada.App.Service().Execute();",
                        [25] = "}"
                    })
                }
            };
            WriteJsonl(Path.Combine(indexDir, "chunks.jsonl"), records);

            List<CodeGraphSymbolRecord> symbols = new List<CodeGraphSymbolRecord>
            {
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Path = "src/Service.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Execute", QualifiedName = "Armada.App.Service.Execute", StartLine = 10, EndLine = 15, ContentHash = "h1" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Path = "src/Service.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Orchestrate", QualifiedName = "Armada.App.Service.Orchestrate", StartLine = 18, EndLine = 30, ContentHash = "h1" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Path = "src/Worker.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Run", QualifiedName = "Armada.App.Worker.Run", StartLine = 4, EndLine = 10, ContentHash = "h2" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Path = "src/Worker.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Helper", QualifiedName = "Armada.App.Worker.Helper", StartLine = 12, EndLine = 20, ContentHash = "h2" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Path = "src/ApiHandler.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Invoke", QualifiedName = "Armada.App.Api.Handler.Invoke", StartLine = 8, EndLine = 14, ContentHash = "h3" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Path = "spec/WorkerSpec.cs", Kind = CodeGraphSymbolKindEnum.Class, SimpleName = "WorkerSpec", QualifiedName = "Armada.Spec.WorkerSpec", StartLine = 1, EndLine = 20, ContentHash = "h4" }
            };

            if (includeExplicitTest)
            {
                symbols.Add(new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = sidecarCommitSha,
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
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Service.Execute", TargetSymbol = "Armada.App.Worker.Run", SourcePath = "src/Service.cs", SourceLine = 12 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Worker.Run", TargetSymbol = "Armada.App.Worker.Helper", SourcePath = "src/Worker.cs", SourceLine = 7 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Worker.Helper", TargetSymbol = "Armada.App.Service.Execute", SourcePath = "src/Worker.cs", SourceLine = 14 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Worker.Helper", TargetSymbol = "Armada.Spec.WorkerSpec", SourcePath = "src/Worker.cs", SourceLine = 15 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Api.Handler.Invoke", TargetSymbol = "Armada.App.Service.Execute", SourcePath = "src/ApiHandler.cs", SourceLine = 10 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = sidecarCommitSha, Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Armada.App.Service.Orchestrate", TargetSymbol = "Armada.Spec.WorkerSpec", SourcePath = "src/Service.cs", SourceLine = 20 }
            };

            if (includeExplicitTest)
            {
                edges.Add(new CodeGraphEdgeRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = sidecarCommitSha,
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

        private static string BuildSourceWithLines(int lineCount, Dictionary<int, string> linesByNumber)
        {
            List<string> lines = new List<string>();
            for (int i = 1; i <= lineCount; i++)
            {
                lines.Add(linesByNumber.TryGetValue(i, out string? line) ? line : "");
            }
            return String.Join("\n", lines);
        }

        private static void WriteConventionOnlyFixture(ArmadaSettings settings, Vessel vessel)
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
                Freshness = "Fresh",
                DocumentCount = 2,
                ChunkCount = 4,
                IndexDirectory = indexDir,
                LastError = null
            };

            File.WriteAllText(Path.Combine(indexDir, "metadata.json"), JsonSerializer.Serialize(status, _JsonOptions), Encoding.UTF8);

            // Source symbol under test -- no edge to WorkerTests
            List<CodeGraphSymbolRecord> symbols = new List<CodeGraphSymbolRecord>
            {
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "src/Worker.cs",
                    Kind = CodeGraphSymbolKindEnum.Method,
                    SimpleName = "Run",
                    QualifiedName = "Armada.App.Worker.Run",
                    StartLine = 4,
                    EndLine = 10,
                    ContentHash = "h1"
                },
                // Test symbol with a conventional name matching the source file stem ("Worker").
                // Intentionally no edge connects this symbol to the source symbol above,
                // so the graph traversal never reaches it -- the convention-only fallback must find it.
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "test/WorkerTests.cs",
                    Kind = CodeGraphSymbolKindEnum.Class,
                    SimpleName = "WorkerTests",
                    QualifiedName = "Armada.Tests.WorkerTests",
                    StartLine = 1,
                    EndLine = 30,
                    ContentHash = "h2"
                }
            };

            // No edges -- WorkerTests is entirely disconnected from the graph
            List<CodeGraphEdgeRecord> edges = new List<CodeGraphEdgeRecord>();

            WriteJsonl(Path.Combine(indexDir, "symbols.jsonl"), symbols);
            WriteJsonl(Path.Combine(indexDir, "edges.jsonl"), edges);
        }

        private static void WriteConventionWithExplicitFixture(ArmadaSettings settings, Vessel vessel)
        {
            // Two test files share a "Worker" stem:
            //   - test/WorkerTests.cs (Class WorkerTests) has an edge back to Worker.Run -> explicit + reachable
            //   - test/DisconnectedWorkerSpec.cs (Class WorkerHelperSpec) has NO edge -> would match convention sweep
            // The explicit candidate must suppress the convention-only sweep entirely.
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
                Freshness = "Fresh",
                DocumentCount = 3,
                ChunkCount = 6,
                IndexDirectory = indexDir,
                LastError = null
            };

            File.WriteAllText(Path.Combine(indexDir, "metadata.json"), JsonSerializer.Serialize(status, _JsonOptions), Encoding.UTF8);

            List<CodeGraphSymbolRecord> symbols = new List<CodeGraphSymbolRecord>
            {
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "src/Worker.cs",
                    Kind = CodeGraphSymbolKindEnum.Method,
                    SimpleName = "Run",
                    QualifiedName = "Armada.App.Worker.Run",
                    StartLine = 4,
                    EndLine = 10,
                    ContentHash = "h1"
                },
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "test/WorkerTests.cs",
                    Kind = CodeGraphSymbolKindEnum.Class,
                    SimpleName = "WorkerTests",
                    QualifiedName = "Armada.Tests.WorkerTests",
                    StartLine = 1,
                    EndLine = 30,
                    ContentHash = "h2"
                },
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "test/DisconnectedWorkerSpec.cs",
                    Kind = CodeGraphSymbolKindEnum.Class,
                    SimpleName = "WorkerHelperSpec",
                    QualifiedName = "Armada.Tests.WorkerHelperSpec",
                    StartLine = 1,
                    EndLine = 20,
                    ContentHash = "h3"
                }
            };

            List<CodeGraphEdgeRecord> edges = new List<CodeGraphEdgeRecord>
            {
                new CodeGraphEdgeRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Kind = CodeGraphEdgeKindEnum.Calls,
                    SourceSymbol = "Armada.Tests.WorkerTests",
                    TargetSymbol = "Armada.App.Worker.Run",
                    SourcePath = "test/WorkerTests.cs",
                    SourceLine = 5
                }
            };

            WriteJsonl(Path.Combine(indexDir, "symbols.jsonl"), symbols);
            WriteJsonl(Path.Combine(indexDir, "edges.jsonl"), edges);
        }

        private static void WriteConventionStemMismatchFixture(ArmadaSettings settings, Vessel vessel)
        {
            // src/Worker.cs is the seed. UnrelatedThingTest.cs is disconnected AND its stem
            // ("UnrelatedThingTest") does NOT contain the seed stem ("Worker"). The convention
            // sweep must filter it out.
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
                Freshness = "Fresh",
                DocumentCount = 2,
                ChunkCount = 4,
                IndexDirectory = indexDir,
                LastError = null
            };

            File.WriteAllText(Path.Combine(indexDir, "metadata.json"), JsonSerializer.Serialize(status, _JsonOptions), Encoding.UTF8);

            List<CodeGraphSymbolRecord> symbols = new List<CodeGraphSymbolRecord>
            {
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "src/Worker.cs",
                    Kind = CodeGraphSymbolKindEnum.Method,
                    SimpleName = "Run",
                    QualifiedName = "Armada.App.Worker.Run",
                    StartLine = 4,
                    EndLine = 10,
                    ContentHash = "h1"
                },
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "test/UnrelatedThingTest.cs",
                    Kind = CodeGraphSymbolKindEnum.Class,
                    SimpleName = "UnrelatedThingTest",
                    QualifiedName = "Armada.Tests.UnrelatedThingTest",
                    StartLine = 1,
                    EndLine = 20,
                    ContentHash = "h2"
                }
            };

            List<CodeGraphEdgeRecord> edges = new List<CodeGraphEdgeRecord>();

            WriteJsonl(Path.Combine(indexDir, "symbols.jsonl"), symbols);
            WriteJsonl(Path.Combine(indexDir, "edges.jsonl"), edges);
        }

        private static void WriteConventionReachableSamePathFixture(ArmadaSettings settings, Vessel vessel)
        {
            // test/WorkerSpec.cs has two symbols: an outer class WorkerSpec that has an edge
            // from Worker.Run (so the path is reachable) and a NestedHelper class on the same
            // path with no edge. Because the path is in reachablePaths, the new context.Symbols
            // sweep must skip both -- the candidate is only registered once via the existing
            // impact.Results convention loop, not duplicated by the new sweep.
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
                Freshness = "Fresh",
                DocumentCount = 3,
                ChunkCount = 6,
                IndexDirectory = indexDir,
                LastError = null
            };

            File.WriteAllText(Path.Combine(indexDir, "metadata.json"), JsonSerializer.Serialize(status, _JsonOptions), Encoding.UTF8);

            List<CodeGraphSymbolRecord> symbols = new List<CodeGraphSymbolRecord>
            {
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "src/Worker.cs",
                    Kind = CodeGraphSymbolKindEnum.Method,
                    SimpleName = "Run",
                    QualifiedName = "Armada.App.Worker.Run",
                    StartLine = 4,
                    EndLine = 10,
                    ContentHash = "h1"
                },
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "test/WorkerSpec.cs",
                    Kind = CodeGraphSymbolKindEnum.Class,
                    SimpleName = "WorkerSpec",
                    QualifiedName = "Armada.Spec.WorkerSpec",
                    StartLine = 1,
                    EndLine = 20,
                    ContentHash = "h2"
                },
                new CodeGraphSymbolRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Path = "test/WorkerSpec.cs",
                    Kind = CodeGraphSymbolKindEnum.Class,
                    SimpleName = "WorkerSpec_NestedHelper",
                    QualifiedName = "Armada.Spec.WorkerSpec.NestedHelper",
                    StartLine = 21,
                    EndLine = 40,
                    ContentHash = "h2"
                }
            };

            List<CodeGraphEdgeRecord> edges = new List<CodeGraphEdgeRecord>
            {
                new CodeGraphEdgeRecord
                {
                    VesselId = vessel.Id,
                    CommitSha = "deadbeef",
                    Kind = CodeGraphEdgeKindEnum.Calls,
                    SourceSymbol = "Armada.App.Worker.Run",
                    TargetSymbol = "Armada.Spec.WorkerSpec",
                    SourcePath = "src/Worker.cs",
                    SourceLine = 7
                }
            };

            WriteJsonl(Path.Combine(indexDir, "symbols.jsonl"), symbols);
            WriteJsonl(Path.Combine(indexDir, "edges.jsonl"), edges);
        }

        private static void WriteEmptySidecarFixture(ArmadaSettings settings, Vessel vessel)
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
                Freshness = "Fresh",
                DocumentCount = 0,
                ChunkCount = 0,
                IndexDirectory = indexDir,
                LastError = null
            };

            File.WriteAllText(Path.Combine(indexDir, "metadata.json"), JsonSerializer.Serialize(status, _JsonOptions), Encoding.UTF8);
            File.WriteAllText(Path.Combine(indexDir, "symbols.jsonl"), "", Encoding.UTF8);
            File.WriteAllText(Path.Combine(indexDir, "edges.jsonl"), "", Encoding.UTF8);
        }

        private static void AppendForeignVesselRecords(ArmadaSettings settings, Vessel vessel)
        {
            string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);

            List<CodeGraphSymbolRecord> foreignSymbols = new List<CodeGraphSymbolRecord>
            {
                new CodeGraphSymbolRecord
                {
                    VesselId = "vsl_someone_else_" + Guid.NewGuid().ToString("N"),
                    CommitSha = "deadbeef",
                    Path = "src/Foreign.cs",
                    Kind = CodeGraphSymbolKindEnum.Method,
                    SimpleName = "ForeignSymbol",
                    QualifiedName = "Foreign.Vessel.Symbol",
                    StartLine = 1,
                    EndLine = 5,
                    ContentHash = "fh"
                }
            };

            List<CodeGraphEdgeRecord> foreignEdges = new List<CodeGraphEdgeRecord>
            {
                new CodeGraphEdgeRecord
                {
                    VesselId = "vsl_someone_else_" + Guid.NewGuid().ToString("N"),
                    CommitSha = "deadbeef",
                    Kind = CodeGraphEdgeKindEnum.Calls,
                    SourceSymbol = "Foreign.Vessel.Caller",
                    TargetSymbol = "Foreign.Vessel.Symbol",
                    SourcePath = "src/Foreign.cs",
                    SourceLine = 3
                }
            };

            AppendJsonl(Path.Combine(indexDir, "symbols.jsonl"), foreignSymbols);
            AppendJsonl(Path.Combine(indexDir, "edges.jsonl"), foreignEdges);
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

        private static void AppendJsonl<T>(string path, List<T> items)
        {
            using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
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

        #endregion
    }
}
