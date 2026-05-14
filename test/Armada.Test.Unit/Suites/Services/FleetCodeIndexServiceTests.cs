namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for fleet-scoped code index search and context-pack generation.
    /// </summary>
    public class FleetCodeIndexServiceTests : TestSuite
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
        public override string Name => "Fleet Code Index Service";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("SearchFleetAsync merges across vessels with vessel attribution sorted by score", async () =>
            {
                string dataRoot = NewTempDirectory("armada-fleet-code-index-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Fleet fleet = await testDb.Driver.Fleets.CreateAsync(new Fleet("fleet-search")).ConfigureAwait(false);
                        Vessel vesselA = await CreateFleetVesselAsync(testDb, fleet.Id, "fleet-vessel-a").ConfigureAwait(false);
                        Vessel vesselB = await CreateFleetVesselAsync(testDb, fleet.Id, "fleet-vessel-b").ConfigureAwait(false);

                        ArmadaSettings settings = BuildSettings(dataRoot, null);
                        await WritePersistedIndexAsync(
                            settings,
                            vesselA,
                            new List<CodeIndexRecord>
                            {
                                new CodeIndexRecord
                                {
                                    VesselId = vesselA.Id,
                                    Path = "src/a.cs",
                                    CommitSha = "abc1",
                                    ContentHash = "h1",
                                    Language = "csharp",
                                    StartLine = 1,
                                    EndLine = 10,
                                    Freshness = "Fresh",
                                    IndexedAtUtc = DateTime.UtcNow,
                                    IsReferenceOnly = false,
                                    Content = "needle needle"
                                }
                            }).ConfigureAwait(false);

                        await WritePersistedIndexAsync(
                            settings,
                            vesselB,
                            new List<CodeIndexRecord>
                            {
                                new CodeIndexRecord
                                {
                                    VesselId = vesselB.Id,
                                    Path = "src/b.cs",
                                    CommitSha = "abc2",
                                    ContentHash = "h2",
                                    Language = "csharp",
                                    StartLine = 1,
                                    EndLine = 10,
                                    Freshness = "Fresh",
                                    IndexedAtUtc = DateTime.UtcNow,
                                    IsReferenceOnly = false,
                                    Content = "needle needle needle"
                                }
                            }).ConfigureAwait(false);

                        CodeIndexService service = CreateService(testDb, settings);

                        FleetCodeSearchResponse response = await service.SearchFleetAsync(new FleetCodeSearchRequest
                        {
                            FleetId = fleet.Id,
                            Query = "needle",
                            Limit = 10,
                            IncludeContent = false
                        }).ConfigureAwait(false);

                        AssertEqual(fleet.Id, response.FleetId);
                        AssertEqual("needle", response.Query);
                        AssertEqual(2, response.Results.Count);
                        AssertEqual(vesselB.Id, response.Results[0].VesselId, "Higher scoring vessel result should rank first");
                        AssertEqual("fleet-vessel-b", response.Results[0].VesselName);
                        AssertTrue(response.Results[0].Score > response.Results[1].Score, "Results should be sorted descending by score");
                        foreach (FleetCodeSearchResult result in response.Results)
                        {
                            AssertFalse(String.IsNullOrWhiteSpace(result.VesselId), "VesselId must be populated");
                            AssertFalse(String.IsNullOrWhiteSpace(result.VesselName), "VesselName must be populated");
                        }
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildFleetContextPackAsync combines per-vessel packs and returns dispatch-ready prestaged file", async () =>
            {
                string dataRoot = NewTempDirectory("armada-fleet-context-pack-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Fleet fleet = await testDb.Driver.Fleets.CreateAsync(new Fleet("fleet-pack")).ConfigureAwait(false);
                        Vessel vesselA = await CreateFleetVesselAsync(testDb, fleet.Id, "pack-vessel-a").ConfigureAwait(false);
                        Vessel vesselB = await CreateFleetVesselAsync(testDb, fleet.Id, "pack-vessel-b").ConfigureAwait(false);

                        ArmadaSettings settings = BuildSettings(dataRoot, null);
                        await WritePersistedIndexAsync(
                            settings,
                            vesselA,
                            new List<CodeIndexRecord>
                            {
                                new CodeIndexRecord
                                {
                                    VesselId = vesselA.Id,
                                    Path = "src/a.cs",
                                    CommitSha = "abc1",
                                    ContentHash = "h1",
                                    Language = "csharp",
                                    StartLine = 1,
                                    EndLine = 5,
                                    Freshness = "Fresh",
                                    IndexedAtUtc = DateTime.UtcNow,
                                    IsReferenceOnly = false,
                                    Content = "fleet goal support from vessel a"
                                }
                            }).ConfigureAwait(false);

                        await WritePersistedIndexAsync(
                            settings,
                            vesselB,
                            new List<CodeIndexRecord>
                            {
                                new CodeIndexRecord
                                {
                                    VesselId = vesselB.Id,
                                    Path = "src/b.cs",
                                    CommitSha = "abc2",
                                    ContentHash = "h2",
                                    Language = "csharp",
                                    StartLine = 1,
                                    EndLine = 5,
                                    Freshness = "Fresh",
                                    IndexedAtUtc = DateTime.UtcNow,
                                    IsReferenceOnly = false,
                                    Content = "fleet goal support from vessel b"
                                }
                            }).ConfigureAwait(false);

                        CodeIndexService service = CreateService(testDb, settings);
                        FleetContextPackResponse response = await service.BuildFleetContextPackAsync(new FleetContextPackRequest
                        {
                            FleetId = fleet.Id,
                            Goal = "fleet goal",
                            TokenBudget = 8000,
                            MaxResultsPerVessel = 3
                        }).ConfigureAwait(false);

                        AssertContains("## Vessel: pack-vessel-a", response.Markdown);
                        AssertContains("## Vessel: pack-vessel-b", response.Markdown);
                        AssertEqual(1, CountOccurrences(response.Markdown, "## Vessel: pack-vessel-a"));
                        AssertEqual(1, CountOccurrences(response.Markdown, "## Vessel: pack-vessel-b"));
                        AssertTrue(File.Exists(response.MaterializedPath), "Combined context pack should be materialized");
                        AssertEqual(1, response.PrestagedFiles.Count);
                        AssertEqual("_briefing/context-pack.md", response.PrestagedFiles[0].DestPath);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("McpToolRegistrar RegisterAll includes fleet code index tools", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<System.Text.Json.JsonElement?, Task<object>>> handlers =
                        new Dictionary<string, Func<System.Text.Json.JsonElement?, Task<object>>>();

                    McpToolRegistrar.RegisterAll(
                        (name, _, _, handler) => handlers[name] = handler,
                        testDb.Driver,
                        new StubAdmiralService(),
                        codeIndexService: new StubCodeIndexService());

                    AssertTrue(handlers.ContainsKey("armada_fleet_code_search"));
                    AssertTrue(handlers.ContainsKey("armada_fleet_context_pack"));
                }
            });

            await RunTest("McpToolRegistrar RegisterAll omits fleet code index tools without code index service", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<System.Text.Json.JsonElement?, Task<object>>> handlers =
                        new Dictionary<string, Func<System.Text.Json.JsonElement?, Task<object>>>();

                    McpToolRegistrar.RegisterAll(
                        (name, _, _, handler) => handlers[name] = handler,
                        testDb.Driver,
                        new StubAdmiralService());

                    AssertFalse(handlers.ContainsKey("armada_fleet_code_search"));
                    AssertFalse(handlers.ContainsKey("armada_fleet_context_pack"));
                }
            });
        }

        private static CodeIndexService CreateService(TestDatabase testDb, ArmadaSettings settings)
        {
            LoggingModule logging = SilentLogging();
            return new CodeIndexService(logging, testDb.Driver, settings, new GitService(logging), null, null);
        }

        private static ArmadaSettings BuildSettings(string dataRoot, Action<CodeIndexSettings>? configureCodeIndex)
        {
            CodeIndexSettings codeIndex = new CodeIndexSettings
            {
                IndexDirectory = Path.Combine(dataRoot, "code-index"),
                MaxChunkLines = 20,
                MaxSearchResults = 10,
                MaxContextPackResults = 8,
                UseSemanticSearch = false,
                UseSummarizer = false
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

        private static async Task<Vessel> CreateFleetVesselAsync(TestDatabase testDb, string fleetId, string name)
        {
            Vessel vessel = new Vessel
            {
                Name = name,
                RepoUrl = "https://example.com/" + name + ".git",
                DefaultBranch = "main",
                FleetId = fleetId
            };
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task WritePersistedIndexAsync(ArmadaSettings settings, Vessel vessel, IReadOnlyList<CodeIndexRecord> records)
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
                DocumentCount = 1,
                ChunkCount = records.Count,
                IndexDirectory = indexDir
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

        private static int CountOccurrences(string text, string term)
        {
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(term)) return 0;
            int count = 0;
            int index = 0;
            while (index < text.Length)
            {
                int found = text.IndexOf(term, index, StringComparison.Ordinal);
                if (found < 0) break;
                count++;
                index = found + term.Length;
            }
            return count;
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

        private sealed class StubCodeIndexService : ICodeIndexService
        {
            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => throw new NotImplementedException();
        }

        private sealed class StubAdmiralService : IAdmiralService
        {
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }

            public Func<Captain, Task>? OnStopAgent { get; set; }

            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }

            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }

            public Func<Voyage, Task>? OnVoyageComplete { get; set; }

            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }

            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }

            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
