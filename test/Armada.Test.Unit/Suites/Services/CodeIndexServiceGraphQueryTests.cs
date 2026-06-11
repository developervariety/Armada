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

            await RunTest("Endpoint-indexed traversal preserves cycle hub and suffix collision behavior", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-indexed-traversal-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteEndpointIndexedTraversalFixture(settings, vessel);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphNeighborsResponse callers = await service.GetCallersAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Foo.Bar",
                            Limit = 20
                        }).ConfigureAwait(false);

                        List<string> callerNames = callers.Results.Select(r => r.Symbol.QualifiedName).ToList();
                        AssertTrue(callerNames.Contains("Graph.Api.Entry"), "direct caller should be preserved");
                        AssertTrue(callerNames.Contains("Graph.Leaf.Two"), "cycle caller should be preserved");
                        AssertTrue(callerNames.Contains("Graph.Collision.OtherSource"), "suffix-collision caller should still match");

                        CodeGraphNeighborsResponse callees = await service.GetCalleesAsync(new CodeGraphNeighborsRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Foo.Bar",
                            Limit = 20
                        }).ConfigureAwait(false);

                        List<string> calleeNames = callees.Results.Select(r => r.Symbol.QualifiedName).ToList();
                        AssertTrue(calleeNames.Contains("Graph.Hub.FanOut"), "hub callee should be preserved");
                        AssertTrue(calleeNames.Contains("Graph.Leaf.One"), "direct leaf callee should be preserved");
                        AssertTrue(calleeNames.Contains("Graph.Collision.Unrelated"), "suffix-collision callee should still match");

                        CodeGraphImpactResponse impact = await service.GetImpactAsync(new CodeGraphImpactRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Foo.Bar",
                            Direction = CodeGraphTraversalDirectionEnum.Both,
                            MaxDepth = 3,
                            MaxResults = 20
                        }).ConfigureAwait(false);

                        List<string> impactNames = impact.Results.Select(r => r.Symbol.QualifiedName).ToList();
                        AssertFalse(impactNames.Contains("Foo.Bar"), "cycle traversal should not return the seed");
                        AssertTrue(impactNames.Contains("Graph.Hub.FanOut"), "impact traversal should include the hub");
                        AssertTrue(impactNames.Contains("Graph.Leaf.Two"), "impact traversal should include the cycle edge endpoint");
                        AssertTrue(impactNames.Contains("Graph.Collision.Unrelated"), "impact traversal should preserve suffix-collision callees");
                        AssertTrue(impactNames.Contains("Graph.Collision.OtherSource"), "impact traversal should preserve suffix-collision callers");
                        AssertEqual(
                            impact.Results.Select(r => r.Symbol.QualifiedName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                            impact.Results.Count,
                            "impact results should stay de-duplicated after indexed traversal");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("Endpoint-indexed traversal ignores non-call candidate edges", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-graph-query-indexed-edge-kind-");
                try
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = BuildSettings(dataRoot);
                        Vessel vessel = await CreateFixtureVesselAsync(db).ConfigureAwait(false);
                        WriteEndpointIndexedTraversalFixture(settings, vessel);

                        CodeIndexService service = CreateService(db, settings);
                        CodeGraphImpactResponse impact = await service.GetImpactAsync(new CodeGraphImpactRequest
                        {
                            VesselId = vessel.Id,
                            Symbol = "Foo.Bar",
                            Direction = CodeGraphTraversalDirectionEnum.Callees,
                            MaxDepth = 2,
                            MaxResults = 20
                        }).ConfigureAwait(false);

                        List<string> impactNames = impact.Results.Select(r => r.Symbol.QualifiedName).ToList();
                        AssertTrue(impactNames.Contains("Graph.Hub.FanOut"), "call edges from the candidate bucket should still traverse");
                        AssertFalse(impactNames.Contains("Graph.Noise.Imported"), "non-call candidate edges should not traverse");
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

        private static void WriteEndpointIndexedTraversalFixture(ArmadaSettings settings, Vessel vessel)
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
                DocumentCount = 7,
                ChunkCount = 12,
                IndexDirectory = indexDir,
                LastError = null
            };

            File.WriteAllText(Path.Combine(indexDir, "metadata.json"), JsonSerializer.Serialize(status, _JsonOptions), Encoding.UTF8);

            List<CodeGraphSymbolRecord> symbols = new List<CodeGraphSymbolRecord>
            {
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Foo.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Bar", QualifiedName = "Foo.Bar", StartLine = 10, EndLine = 20, ContentHash = "h1" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Baz.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Bar", QualifiedName = "Baz.Bar", StartLine = 10, EndLine = 20, ContentHash = "h2" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Api.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Entry", QualifiedName = "Graph.Api.Entry", StartLine = 3, EndLine = 8, ContentHash = "h3" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Hub.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "FanOut", QualifiedName = "Graph.Hub.FanOut", StartLine = 6, EndLine = 18, ContentHash = "h4" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Leaf.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "One", QualifiedName = "Graph.Leaf.One", StartLine = 1, EndLine = 4, ContentHash = "h5" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Leaf.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Two", QualifiedName = "Graph.Leaf.Two", StartLine = 6, EndLine = 12, ContentHash = "h5" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Collision.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Unrelated", QualifiedName = "Graph.Collision.Unrelated", StartLine = 2, EndLine = 5, ContentHash = "h6" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Collision.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "OtherSource", QualifiedName = "Graph.Collision.OtherSource", StartLine = 8, EndLine = 12, ContentHash = "h6" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "src/Noise.cs", Kind = CodeGraphSymbolKindEnum.Namespace, SimpleName = "Imported", QualifiedName = "Graph.Noise.Imported", StartLine = 1, EndLine = 1, ContentHash = "h7" },
                new CodeGraphSymbolRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Path = "test/FooBarTests.cs", Kind = CodeGraphSymbolKindEnum.Method, SimpleName = "Covers", QualifiedName = "Graph.Tests.FooBarTests.Covers", StartLine = 5, EndLine = 15, ContentHash = "h7" }
            };

            List<CodeGraphEdgeRecord> edges = new List<CodeGraphEdgeRecord>
            {
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Graph.Api.Entry", TargetSymbol = "Foo.Bar", SourcePath = "src/Api.cs", SourceLine = 4 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Foo.Bar", TargetSymbol = "Graph.Hub.FanOut", SourcePath = "src/Foo.cs", SourceLine = 12 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Foo.Bar", TargetSymbol = "Graph.Leaf.One", SourcePath = "src/Foo.cs", SourceLine = 13 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Graph.Hub.FanOut", TargetSymbol = "Graph.Leaf.Two", SourcePath = "src/Hub.cs", SourceLine = 9 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Graph.Leaf.Two", TargetSymbol = "Foo.Bar", SourcePath = "src/Leaf.cs", SourceLine = 8 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Baz.Bar", TargetSymbol = "Graph.Collision.Unrelated", SourcePath = "src/Baz.cs", SourceLine = 11 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Graph.Collision.OtherSource", TargetSymbol = "Baz.Bar", SourcePath = "src/Collision.cs", SourceLine = 9 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Imports, SourceSymbol = "Foo.Bar", TargetSymbol = "Graph.Noise.Imported", SourcePath = "src/Foo.cs", SourceLine = 14 },
                new CodeGraphEdgeRecord { VesselId = vessel.Id, CommitSha = "deadbeef", Kind = CodeGraphEdgeKindEnum.Calls, SourceSymbol = "Graph.Tests.FooBarTests.Covers", TargetSymbol = "Foo.Bar", SourcePath = "test/FooBarTests.cs", SourceLine = 7 }
            };

            WriteJsonl(Path.Combine(indexDir, "symbols.jsonl"), symbols);
            WriteJsonl(Path.Combine(indexDir, "edges.jsonl"), edges);
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
