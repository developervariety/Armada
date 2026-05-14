namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for Admiral-owned code indexing, lexical search, and context-pack generation.
    /// </summary>
    public class CodeIndexServiceTests : TestSuite
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
        public override string Name => "Code Index Service";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("UpdateAsync indexes eligible files and skips secrets and build outputs", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        AssertEqual(vessel.Id, status.VesselId);
                        AssertEqual(repository.CommitSha, status.IndexedCommitSha);
                        AssertEqual("Fresh", status.Freshness);
                        AssertEqual(2, status.DocumentCount);
                        AssertTrue(status.ChunkCount >= 2, "At least two chunks should be written");

                        string chunksPath = Path.Combine(status.IndexDirectory, "chunks.jsonl");
                        AssertTrue(File.Exists(chunksPath), "chunks.jsonl should exist");
                        string chunksJson = await File.ReadAllTextAsync(chunksPath).ConfigureAwait(false);

                        AssertContains("CodeIndexTarget.cs", chunksJson);
                        AssertContains("usage.md", chunksJson);
                        AssertFalse(chunksJson.Contains("SHOULD_NOT_INDEX_SECRET", StringComparison.Ordinal), ".env content must not be indexed");
                        AssertFalse(chunksJson.Contains("SHOULD_NOT_INDEX_BUILD_OUTPUT", StringComparison.Ordinal), "bin output must not be indexed");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchAsync returns required result metadata", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        CodeSearchResponse response = await service.SearchAsync(new CodeSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "SearchKeyword",
                            Limit = 5,
                            IncludeContent = false
                        }).ConfigureAwait(false);

                        AssertEqual("Fresh", response.Status.Freshness);
                        AssertTrue(response.Results.Count > 0, "Search should return matches");

                        CodeSearchResult result = response.Results[0];
                        AssertEqual(vessel.Id, result.Record.VesselId);
                        AssertEqual("src/CodeIndexTarget.cs", result.Record.Path);
                        AssertEqual(repository.CommitSha, result.Record.CommitSha);
                        AssertFalse(String.IsNullOrWhiteSpace(result.Record.ContentHash), "Content hash should be present");
                        AssertEqual("csharp", result.Record.Language);
                        AssertTrue(result.Record.StartLine > 0, "Start line should be set");
                        AssertTrue(result.Record.EndLine >= result.Record.StartLine, "End line should be set");
                        AssertEqual("Fresh", result.Record.Freshness);
                        AssertEqual("", result.Record.Content);
                        AssertContains("SearchKeyword", result.Excerpt);
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync returns dispatch-ready prestaged context", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

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

                        AssertContains("# Armada Code Context Pack", response.Markdown);
                        AssertContains("Playbooks, vessel CLAUDE.md, and project CLAUDE.md rules win on conflict.", response.Markdown);
                        AssertContains("src/CodeIndexTarget.cs", response.Markdown);
                        AssertTrue(File.Exists(response.MaterializedPath), "Context pack markdown should be materialized");
                        AssertEqual(1, response.PrestagedFiles.Count);
                        AssertEqual(response.MaterializedPath, response.PrestagedFiles[0].SourcePath);
                        AssertEqual("_briefing/context-pack.md", response.PrestagedFiles[0].DestPath);
                        AssertTrue(response.EstimatedTokens > 0, "Estimated tokens should be set");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
              }
            });

            await RunTest("SearchAsync_UseSemanticSearchFalse_MatchesV1LexicalScoresForFixedCorpus", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Directory.CreateDirectory(Path.Combine(dataRoot, "repo"));
                        Vessel vessel = await CreateVesselAsync(testDb, Path.Combine(dataRoot, "repo")).ConfigureAwait(false);

                        List<CodeIndexRecord> records = new List<CodeIndexRecord>
                        {
                            new CodeIndexRecord
                            {
                                VesselId = vessel.Id,
                                Path = "a.cs",
                                CommitSha = "abc",
                                ContentHash = "h1",
                                Language = "csharp",
                                StartLine = 1,
                                EndLine = 5,
                                IsReferenceOnly = false,
                                Content = "alpha beta gamma"
                            },
                            new CodeIndexRecord
                            {
                                VesselId = vessel.Id,
                                Path = "b.cs",
                                CommitSha = "abc",
                                ContentHash = "h2",
                                Language = "csharp",
                                StartLine = 1,
                                EndLine = 5,
                                IsReferenceOnly = false,
                                Content = "alpha alpha beta"
                            }
                        };

                        ArmadaSettings settings = BuildSettings(dataRoot, ci =>
                        {
                            ci.UseSemanticSearch = false;
                        });
                        await WritePersistedIndexAsync(settings, vessel, records).ConfigureAwait(false);

                        CodeIndexService service = CreateService(testDb, dataRoot);
                        CodeSearchResponse response = await service.SearchAsync(new CodeSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "alpha",
                            Limit = 10,
                            IncludeContent = true
                        }).ConfigureAwait(false);

                        AssertEqual(2, response.Results.Count);
                        AssertEqual("b.cs", response.Results[0].Record.Path);
                        AssertEqual(10.0, response.Results[0].Score);
                        AssertEqual("a.cs", response.Results[1].Record.Path);
                        AssertEqual(9.0, response.Results[1].Score);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchAsync_SemanticBlending_OutranksLexicalNoiseWhenEmbeddingsAlign", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Directory.CreateDirectory(Path.Combine(dataRoot, "repo"));
                        Vessel vessel = await CreateVesselAsync(testDb, Path.Combine(dataRoot, "repo")).ConfigureAwait(false);

                        List<CodeIndexRecord> records = new List<CodeIndexRecord>
                        {
                            new CodeIndexRecord
                            {
                                VesselId = vessel.Id,
                                Path = "noisy.cs",
                                CommitSha = "abc",
                                ContentHash = "n1",
                                Language = "csharp",
                                StartLine = 1,
                                EndLine = 10,
                                IsReferenceOnly = false,
                                Content = "SearchKeyword SearchKeyword SearchKeyword SearchKeyword SearchKeyword SearchKeyword",
                                EmbeddingVector = new float[] { 0F, 1F, 0F }
                            },
                            new CodeIndexRecord
                            {
                                VesselId = vessel.Id,
                                Path = "target.cs",
                                CommitSha = "abc",
                                ContentHash = "t1",
                                Language = "csharp",
                                StartLine = 1,
                                EndLine = 3,
                                IsReferenceOnly = false,
                                Content = "conceptual widget",
                                EmbeddingVector = new float[] { 1F, 0F, 0F }
                            }
                        };

                        ArmadaSettings settings = BuildSettings(dataRoot, ci => { ci.UseSemanticSearch = true; });
                        await WritePersistedIndexAsync(settings, vessel, records).ConfigureAwait(false);

                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            new ConstantVectorEmbeddingClient(new float[] { 1F, 0F, 0F }),
                            ci => { ci.UseSemanticSearch = true; });

                        CodeSearchResponse response = await service.SearchAsync(new CodeSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "SearchKeyword",
                            Limit = 10,
                            IncludeContent = true
                        }).ConfigureAwait(false);

                        AssertTrue(response.Results.Count >= 2, "Expected both chunks to rank");
                        AssertEqual("target.cs", response.Results[0].Record.Path);
                        AssertTrue(response.Results[0].Score > response.Results[1].Score, "Semantic target should outrank lexical noise");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("CodeIndexRecord_EmbeddingVector_RoundTripsJson", () =>
            {
                CodeIndexRecord withVector = new CodeIndexRecord
                {
                    VesselId = "v1",
                    Path = "p.cs",
                    Content = "x",
                    EmbeddingVector = new float[] { 0.1F, -0.25F, 0.5F }
                };
                string json = JsonSerializer.Serialize(withVector, _IndexJsonOptions);
                CodeIndexRecord? parsed = JsonSerializer.Deserialize<CodeIndexRecord>(json, _IndexJsonOptions);
                AssertTrue(parsed != null, "Deserialize");
                AssertTrue(parsed!.EmbeddingVector != null && parsed.EmbeddingVector.Length == 3, "Vector length");
                AssertEqual(0.1F, parsed.EmbeddingVector![0]);
                AssertEqual(-0.25F, parsed.EmbeddingVector[1]);
                AssertEqual(0.5F, parsed.EmbeddingVector[2]);

                CodeIndexRecord nullVector = new CodeIndexRecord { VesselId = "v1", Path = "q.cs", Content = "y", EmbeddingVector = null };
                string jsonNull = JsonSerializer.Serialize(nullVector, _IndexJsonOptions);
                CodeIndexRecord? parsedNull = JsonSerializer.Deserialize<CodeIndexRecord>(jsonNull, _IndexJsonOptions);
                AssertTrue(parsedNull != null, "Deserialize null case");
                AssertTrue(parsedNull!.EmbeddingVector == null, "Vector stays null");

                CodeIndexRecord legacy = JsonSerializer.Deserialize<CodeIndexRecord>(
                    "{\"vesselId\":\"v1\",\"path\":\"z.cs\",\"content\":\"z\"}",
                    _IndexJsonOptions)!;
                AssertTrue(legacy.EmbeddingVector == null, "Missing property deserializes as null");
            });
        }

        private static ArmadaSettings BuildSettings(string dataRoot, Action<CodeIndexSettings>? configureCodeIndex)
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

        private static CodeIndexService CreateService(
            TestDatabase testDb,
            string dataRoot,
            IEmbeddingClient? embeddingClient = null,
            Action<CodeIndexSettings>? configureCodeIndex = null)
        {
            ArmadaSettings settings = BuildSettings(dataRoot, configureCodeIndex);

            LoggingModule logging = SilentLogging();
            return new CodeIndexService(logging, testDb.Driver, settings, new GitService(logging), embeddingClient, null);
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string repositoryPath)
        {
            Vessel vessel = new Vessel
            {
                Name = "code-index-vessel-" + Guid.NewGuid().ToString("N"),
                RepoUrl = repositoryPath,
                WorkingDirectory = repositoryPath,
                DefaultBranch = "main"
            };

            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<TestRepository> CreateRepositoryAsync()
        {
            string root = NewTempDirectory("armada-code-index-repo-");
            string repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);

            try
            {
                await RunGitAsync(repo, "init", "-b", "main").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                Directory.CreateDirectory(Path.Combine(repo, "src"));
                Directory.CreateDirectory(Path.Combine(repo, "docs"));
                Directory.CreateDirectory(Path.Combine(repo, "bin", "Debug"));

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "src", "CodeIndexTarget.cs"),
                    "namespace Sample\n{\n    public class CodeIndexTarget\n    {\n        public string SearchKeyword() => \"dispatch evidence\";\n    }\n}\n").ConfigureAwait(false);

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "docs", "usage.md"),
                    "# Usage\n\nThis document mentions context packs for mission briefs.\n").ConfigureAwait(false);

                await File.WriteAllTextAsync(
                    Path.Combine(repo, ".env"),
                    "SHOULD_NOT_INDEX_SECRET=abc123\n").ConfigureAwait(false);

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "bin", "Debug", "generated.txt"),
                    "SHOULD_NOT_INDEX_BUILD_OUTPUT\n").ConfigureAwait(false);

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

        private sealed class ConstantVectorEmbeddingClient : IEmbeddingClient
        {
            private readonly float[] _Vector;

            public ConstantVectorEmbeddingClient(float[] vector)
            {
                _Vector = vector ?? throw new ArgumentNullException(nameof(vector));
            }

            public Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                return Task.FromResult(_Vector);
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
    }
}
