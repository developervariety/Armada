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
                        AssertTrue(response.Metrics.ResultCount > 0, "Metrics should include result count");
                        AssertEqual(1, response.Metrics.IncludedFileCount, "Metrics should count distinct included files");
                        AssertTrue(response.Metrics.IncludedFiles.Contains("src/CodeIndexTarget.cs"), "Metrics should list included file paths");
                        AssertEqual(0, response.Metrics.MatchedHintCount, "No pack hints should match in this fixture");
                        AssertFalse(response.Metrics.GraphExpansionUsed, "Context pack should report no graph expansion when none is used");
                        AssertEqual(1, response.Metrics.PrestagedFileCount, "Metrics should count prestaged file entries");
                        AssertEqual(response.EstimatedTokens, response.Metrics.EstimatedTokens, "Metrics should mirror estimated tokens");
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

            await RunTest("SearchAsync_QueryEmbeddingThrows_FallsBackToLexicalRanking", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Directory.CreateDirectory(Path.Combine(dataRoot, "repo"));
                        Vessel vessel = await CreateVesselAsync(testDb, Path.Combine(dataRoot, "repo")).ConfigureAwait(false);

                        List<CodeIndexRecord> records = BuildSemanticBlendCorpus(vessel.Id);
                        ArmadaSettings settings = BuildSettings(dataRoot, ci => { ci.UseSemanticSearch = true; });
                        await WritePersistedIndexAsync(settings, vessel, records).ConfigureAwait(false);

                        ThrowingEmbeddingClient throwing = new ThrowingEmbeddingClient(new InvalidOperationException("transient embed failure"));
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            throwing,
                            ci => { ci.UseSemanticSearch = true; });

                        CodeSearchResponse response = await service.SearchAsync(new CodeSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "SearchKeyword",
                            Limit = 10,
                            IncludeContent = true
                        }).ConfigureAwait(false);

                        AssertEqual(1, throwing.CallCount, "query embedding should be attempted exactly once");
                        AssertEqual(1, response.Results.Count, "only the lexically-matching record should rank when semantic falls back");
                        AssertEqual("noisy.cs", response.Results[0].Record.Path);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchAsync_QueryEmbeddingReturnsEmptyArray_FallsBackToLexicalRanking", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Directory.CreateDirectory(Path.Combine(dataRoot, "repo"));
                        Vessel vessel = await CreateVesselAsync(testDb, Path.Combine(dataRoot, "repo")).ConfigureAwait(false);

                        List<CodeIndexRecord> records = BuildSemanticBlendCorpus(vessel.Id);
                        ArmadaSettings settings = BuildSettings(dataRoot, ci => { ci.UseSemanticSearch = true; });
                        await WritePersistedIndexAsync(settings, vessel, records).ConfigureAwait(false);

                        ConstantVectorEmbeddingClient empty = new ConstantVectorEmbeddingClient(Array.Empty<float>());
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            empty,
                            ci => { ci.UseSemanticSearch = true; });

                        CodeSearchResponse response = await service.SearchAsync(new CodeSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "SearchKeyword",
                            Limit = 10,
                            IncludeContent = true
                        }).ConfigureAwait(false);

                        AssertEqual(1, response.Results.Count, "empty query vector must drop semantic blending entirely");
                        AssertEqual("noisy.cs", response.Results[0].Record.Path);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchAsync_QueryVectorLengthMismatch_RanksLexicallyOnly", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Directory.CreateDirectory(Path.Combine(dataRoot, "repo"));
                        Vessel vessel = await CreateVesselAsync(testDb, Path.Combine(dataRoot, "repo")).ConfigureAwait(false);

                        List<CodeIndexRecord> records = BuildSemanticBlendCorpus(vessel.Id);
                        ArmadaSettings settings = BuildSettings(dataRoot, ci => { ci.UseSemanticSearch = true; });
                        await WritePersistedIndexAsync(settings, vessel, records).ConfigureAwait(false);

                        // Query vector has length 4; record vectors have length 3. ScoreRecord must downgrade to lexical.
                        ConstantVectorEmbeddingClient mismatched = new ConstantVectorEmbeddingClient(new float[] { 1F, 0F, 0F, 0F });
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            mismatched,
                            ci => { ci.UseSemanticSearch = true; });

                        CodeSearchResponse response = await service.SearchAsync(new CodeSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "SearchKeyword",
                            Limit = 10,
                            IncludeContent = true
                        }).ConfigureAwait(false);

                        AssertEqual(1, response.Results.Count, "length-mismatched query vector must not blend");
                        AssertEqual("noisy.cs", response.Results[0].Record.Path);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchAsync_LegacyChunksWithoutEmbeddingVectorField_LoadAndRankLexically", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Directory.CreateDirectory(Path.Combine(dataRoot, "repo"));
                        Vessel vessel = await CreateVesselAsync(testDb, Path.Combine(dataRoot, "repo")).ConfigureAwait(false);

                        ArmadaSettings settings = BuildSettings(dataRoot, ci => { ci.UseSemanticSearch = true; });
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
                            ChunkCount = 2,
                            IndexDirectory = indexDir,
                            LastError = null
                        };
                        string metadataPath = Path.Combine(indexDir, "metadata.json");
                        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(status, _IndexJsonOptions)).ConfigureAwait(false);

                        // Hand-write JSONL entries that do NOT carry the embeddingVector property at all.
                        string chunksPath = Path.Combine(indexDir, "chunks.jsonl");
                        string aLine = "{\"vesselId\":\"" + vessel.Id + "\",\"path\":\"a.cs\",\"commitSha\":\"abc\","
                                       + "\"contentHash\":\"h1\",\"language\":\"csharp\",\"startLine\":1,\"endLine\":5,"
                                       + "\"isReferenceOnly\":false,\"content\":\"alpha beta gamma\"}";
                        string bLine = "{\"vesselId\":\"" + vessel.Id + "\",\"path\":\"b.cs\",\"commitSha\":\"abc\","
                                       + "\"contentHash\":\"h2\",\"language\":\"csharp\",\"startLine\":1,\"endLine\":5,"
                                       + "\"isReferenceOnly\":false,\"content\":\"alpha alpha beta\"}";
                        await File.WriteAllTextAsync(chunksPath, aLine + "\n" + bLine + "\n").ConfigureAwait(false);

                        ConstantVectorEmbeddingClient working = new ConstantVectorEmbeddingClient(new float[] { 1F, 0F, 0F });
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            working,
                            ci => { ci.UseSemanticSearch = true; });

                        CodeSearchResponse response = await service.SearchAsync(new CodeSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "alpha",
                            Limit = 10,
                            IncludeContent = true
                        }).ConfigureAwait(false);

                        // Even with semantic on + working query embedding, missing record vectors keep
                        // the score per record on the lexical-only branch -- bit-identical to V1.
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

            await RunTest("UpdateAsync_SemanticSearchOff_DoesNotInvokeEmbeddingClient", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingEmbeddingClient recording = new RecordingEmbeddingClient(new float[] { 1F, 0F, 0F });
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            recording,
                            ci => { ci.UseSemanticSearch = false; });

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        AssertEqual(0, recording.CallCount, "embedding client must not be called when UseSemanticSearch is false");
                        string chunksPath = Path.Combine(status.IndexDirectory, "chunks.jsonl");
                        AssertTrue(File.Exists(chunksPath), "chunks.jsonl should still be written");
                        string chunksJson = await File.ReadAllTextAsync(chunksPath).ConfigureAwait(false);
                        AssertFalse(chunksJson.Contains("\"embeddingVector\":[", StringComparison.Ordinal),
                            "chunks must not carry embedding vectors when semantic search is disabled");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_SemanticSearchOn_PersistsEmbeddingVectorsToChunksJsonl", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        // Use values with exact binary representations so JSON shortest-form output is deterministic.
                        RecordingEmbeddingClient recording = new RecordingEmbeddingClient(new float[] { 0.5F, 0.25F, -0.125F });
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            recording,
                            ci => { ci.UseSemanticSearch = true; });

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        AssertTrue(recording.CallCount >= status.ChunkCount,
                            "embedding client must be invoked at least once per chunk");
                        string chunksPath = Path.Combine(status.IndexDirectory, "chunks.jsonl");
                        string chunksJson = await File.ReadAllTextAsync(chunksPath).ConfigureAwait(false);
                        AssertContains("\"embeddingVector\":[0.5,0.25,-0.125]", chunksJson);
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_SemanticSearchOn_UsesEmbeddingBatchRequests", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        BatchRecordingEmbeddingClient recording = new BatchRecordingEmbeddingClient(new float[] { 0.25F, 0.5F, 1F });
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            recording,
                            ci =>
                            {
                                ci.UseSemanticSearch = true;
                                ci.EmbeddingBatchSize = 2;
                            });

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        AssertTrue(status.ChunkCount > 1, "fixture should produce multiple chunks");
                        AssertTrue(recording.BatchCallCount > 0, "semantic indexing should use batched embedding calls");
                        AssertEqual(0, recording.SingleCallCount, "per-chunk fallback should not run when batch returns complete vectors");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_ReusesUnchangedChunkEmbeddings_AfterSmallCommit", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingEmbeddingClient first = new RecordingEmbeddingClient(new float[] { 0.5F, 0.25F, -0.125F });
                        CodeIndexService firstService = CreateService(
                            testDb,
                            dataRoot,
                            first,
                            ci => { ci.UseSemanticSearch = true; });

                        CodeIndexStatus firstStatus = await firstService.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        AssertTrue(firstStatus.ChunkCount > 1, "fixture should produce more than one chunk");
                        AssertTrue(first.CallCount >= firstStatus.ChunkCount, "first index should embed every chunk");

                        await File.WriteAllTextAsync(
                            Path.Combine(repository.Path, "docs", "usage.md"),
                            "# Usage\n\nThis document mentions context packs for mission briefs.\n\nSmall follow-up change.\n").ConfigureAwait(false);
                        await RunGitAsync(repository.Path, "add", ".").ConfigureAwait(false);
                        await RunGitAsync(repository.Path, "commit", "-m", "Update one indexed file").ConfigureAwait(false);

                        RecordingEmbeddingClient second = new RecordingEmbeddingClient(new float[] { 0.75F, 0.125F, -0.5F });
                        CodeIndexService secondService = CreateService(
                            testDb,
                            dataRoot,
                            second,
                            ci => { ci.UseSemanticSearch = true; });

                        CodeIndexStatus secondStatus = await secondService.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        AssertEqual("Fresh", secondStatus.Freshness);
                        AssertTrue(secondStatus.ChunkCount >= firstStatus.ChunkCount, "second index should preserve unchanged chunks");
                        AssertTrue(second.CallCount < secondStatus.ChunkCount,
                            "second index should only embed chunks whose content was not already present");
                        AssertTrue(second.Inputs.Any(i => i.Contains("Small follow-up change", StringComparison.Ordinal)),
                            "changed chunk should be embedded");
                        AssertFalse(second.Inputs.Any(i => i.Contains("SearchKeyword", StringComparison.Ordinal)),
                            "unchanged source chunk should reuse its existing embedding");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_FirstIndex_NoPreviousRecords_EmbedsEveryChunkContent", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingEmbeddingClient recording = new RecordingEmbeddingClient(new float[] { 0.125F, 0.25F, 0.5F });
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            recording,
                            ci => { ci.UseSemanticSearch = true; });

                        // First index has no previousRecords; BuildReusableVectors(null) -> empty dict, so every chunk goes to the embedder.
                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        AssertTrue(status.ChunkCount > 0, "fixture should produce at least one chunk");
                        AssertEqual(status.ChunkCount, recording.CallCount,
                            "with no reusable vectors every chunk must be embedded exactly once");
                        AssertTrue(recording.Inputs.Any(i => i.Contains("SearchKeyword", StringComparison.Ordinal)),
                            "C# fixture chunk should be embedded");
                        AssertTrue(recording.Inputs.Any(i => i.Contains("context packs", StringComparison.Ordinal)),
                            "docs fixture chunk should be embedded");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_PriorIndexHasOnlyEmptyVectors_SecondRunEmbedsEveryChunkAgain", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);

                        // First index: embedding client returns empty vectors so persisted records carry no embedding data.
                        ConstantVectorEmbeddingClient emptyEmbedder = new ConstantVectorEmbeddingClient(Array.Empty<float>());
                        CodeIndexService firstService = CreateService(
                            testDb,
                            dataRoot,
                            emptyEmbedder,
                            ci => { ci.UseSemanticSearch = true; });
                        CodeIndexStatus firstStatus = await firstService.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        AssertTrue(firstStatus.ChunkCount > 0, "fixture should produce at least one chunk");

                        // Touch an indexed file and commit so CanReusePersistedIndex (commit-sha match) does not short-circuit.
                        await File.WriteAllTextAsync(
                            Path.Combine(repository.Path, "docs", "usage.md"),
                            "# Usage\n\nThis document mentions context packs for mission briefs.\n\nMinor edit to bump commit sha.\n").ConfigureAwait(false);
                        await RunGitAsync(repository.Path, "add", ".").ConfigureAwait(false);
                        await RunGitAsync(repository.Path, "commit", "-m", "Bump commit").ConfigureAwait(false);

                        // Second index: now use a real embedder. Previous records all have null/empty vectors,
                        // so canReuseEmbeddings is false and BuildReusableVectors receives null. Every chunk must be embedded.
                        RecordingEmbeddingClient recording = new RecordingEmbeddingClient(new float[] { 0.5F, 0.25F, -0.125F });
                        CodeIndexService secondService = CreateService(
                            testDb,
                            dataRoot,
                            recording,
                            ci => { ci.UseSemanticSearch = true; });
                        CodeIndexStatus secondStatus = await secondService.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        AssertEqual(secondStatus.ChunkCount, recording.CallCount,
                            "empty prior vectors must not be reused; every chunk should be re-embedded");
                        string chunksPath = Path.Combine(secondStatus.IndexDirectory, "chunks.jsonl");
                        string chunksJson = await File.ReadAllTextAsync(chunksPath).ConfigureAwait(false);
                        AssertContains("\"embeddingVector\":[0.5,0.25,-0.125]", chunksJson);
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_AllSourceContentRewritten_PreviousVectorsDoNotMatch_AllChunksReEmbedded", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingEmbeddingClient first = new RecordingEmbeddingClient(new float[] { 0.25F, 0.5F, 0.75F });
                        CodeIndexService firstService = CreateService(
                            testDb,
                            dataRoot,
                            first,
                            ci => { ci.UseSemanticSearch = true; });
                        CodeIndexStatus firstStatus = await firstService.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        AssertTrue(firstStatus.ChunkCount > 0, "first index should produce chunks with vectors");
                        AssertTrue(first.CallCount >= firstStatus.ChunkCount, "first index should embed every chunk");

                        // Completely rewrite the indexed files so every new chunk hashes differently from prior records.
                        await File.WriteAllTextAsync(
                            Path.Combine(repository.Path, "src", "CodeIndexTarget.cs"),
                            "namespace Sample\n{\n    public class CodeIndexTarget\n    {\n        public string CompletelyDifferentToken() => \"new evidence corpus\";\n    }\n}\n").ConfigureAwait(false);
                        await File.WriteAllTextAsync(
                            Path.Combine(repository.Path, "docs", "usage.md"),
                            "# Different\n\nUnrelated guidance about totally separate things and unrelated narrative content.\n").ConfigureAwait(false);
                        await RunGitAsync(repository.Path, "add", ".").ConfigureAwait(false);
                        await RunGitAsync(repository.Path, "commit", "-m", "Rewrite all indexed content").ConfigureAwait(false);

                        RecordingEmbeddingClient second = new RecordingEmbeddingClient(new float[] { -0.5F, 0.0625F, 0.375F });
                        CodeIndexService secondService = CreateService(
                            testDb,
                            dataRoot,
                            second,
                            ci => { ci.UseSemanticSearch = true; });
                        CodeIndexStatus secondStatus = await secondService.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        AssertTrue(secondStatus.ChunkCount > 0, "second index should produce chunks");
                        AssertEqual(secondStatus.ChunkCount, second.CallCount,
                            "non-matching reusable vectors must not satisfy any new chunk; embedder must be called for every chunk");
                        AssertFalse(second.Inputs.Any(i => i.Contains("SearchKeyword", StringComparison.Ordinal)),
                            "old chunk content should not appear in the second-run embedder inputs after a full rewrite");
                        AssertTrue(second.Inputs.Any(i => i.Contains("CompletelyDifferentToken", StringComparison.Ordinal)),
                            "new chunk content should be embedded on the second run");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_SemanticSearchOn_PerChunkEmbeddingFailureDoesNotAbortUpdate", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        ThrowingEmbeddingClient throwing = new ThrowingEmbeddingClient(new InvalidOperationException("downstream embedder is sad"));
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            throwing,
                            ci => { ci.UseSemanticSearch = true; });

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        AssertTrue(status.ChunkCount > 0, "chunk count should still be populated");
                        AssertEqual("Fresh", status.Freshness);
                        string chunksPath = Path.Combine(status.IndexDirectory, "chunks.jsonl");
                        AssertTrue(File.Exists(chunksPath), "chunks.jsonl must still be written despite per-chunk embedding failures");
                        string chunksJson = await File.ReadAllTextAsync(chunksPath).ConfigureAwait(false);
                        AssertFalse(chunksJson.Contains("\"embeddingVector\":[", StringComparison.Ordinal),
                            "no embeddingVector array should be persisted when every embed call throws");
                        AssertTrue(throwing.CallCount >= status.ChunkCount,
                            "embedder should have been attempted once per chunk before failure swallowed");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_SemanticSearchOn_EmbeddingClientReturnsEmpty_LeavesVectorNull", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        ConstantVectorEmbeddingClient empty = new ConstantVectorEmbeddingClient(Array.Empty<float>());
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            empty,
                            ci => { ci.UseSemanticSearch = true; });

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        string chunksPath = Path.Combine(status.IndexDirectory, "chunks.jsonl");
                        string chunksJson = await File.ReadAllTextAsync(chunksPath).ConfigureAwait(false);
                        AssertFalse(chunksJson.Contains("\"embeddingVector\":[", StringComparison.Ordinal),
                            "empty embedding response must not produce an embedding vector array");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_SemanticSearchOnButClientNull_CompletesWithoutEmbeddings", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-data-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        // No embedding client passed in; flag is on. Service must still run cleanly.
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            embeddingClient: null,
                            configureCodeIndex: ci => { ci.UseSemanticSearch = true; });

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        AssertEqual("Fresh", status.Freshness);
                        string chunksPath = Path.Combine(status.IndexDirectory, "chunks.jsonl");
                        string chunksJson = await File.ReadAllTextAsync(chunksPath).ConfigureAwait(false);
                        AssertFalse(chunksJson.Contains("\"embeddingVector\":[", StringComparison.Ordinal),
                            "no embedding client means no vectors persisted, even with the flag on");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("WarmBaselineCacheAsync_ThenTryGetCachedContextPackAsync_ReturnsCacheHit", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-cache-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        ContextPackRequest request = new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "any mission goal"
                        };

                        ContextPackResponse? cached = await service.TryGetCachedContextPackAsync(request).ConfigureAwait(false);

                        AssertTrue(cached != null, "Cache hit should return a non-null response");
                        AssertTrue(cached!.Metrics.CacheHit, "Metrics.CacheHit should be true for a cache hit");
                        AssertFalse(String.IsNullOrWhiteSpace(cached.Metrics.CacheKey), "Metrics.CacheKey should be the indexed commit SHA");
                        AssertEqual(repository.CommitSha, cached.Metrics.CacheKey);
                        AssertFalse(String.IsNullOrWhiteSpace(cached.Markdown), "Cached markdown should not be empty");
                        AssertTrue(File.Exists(cached.MaterializedPath), "Cached materialized path should exist on disk");
                        AssertEqual(1, cached.PrestagedFiles.Count, "Cached response should include one prestaged file");
                        AssertEqual("_briefing/context-pack.md", cached.PrestagedFiles[0].DestPath);
                        AssertEqual(cached.MaterializedPath, cached.PrestagedFiles[0].SourcePath);
                        AssertTrue(cached.EstimatedTokens > 0, "Cached response should have positive estimated token count");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("TryGetCachedContextPackAsync_BeforeWarm_ReturnsCacheMiss", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-cache-miss-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        ContextPackResponse? cached = await service.TryGetCachedContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "any goal"
                        }).ConfigureAwait(false);

                        AssertTrue(cached == null, "Cache should miss before WarmBaselineCacheAsync is called");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("TryGetCachedContextPackAsync_AfterCommitShaChange_ReturnsCacheMiss", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-sha-change-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        // Advance the commit so the index is stale relative to the cached SHA.
                        await File.WriteAllTextAsync(
                            Path.Combine(repository.Path, "docs", "usage.md"),
                            "# Usage\n\nUpdated content to bump the commit sha.\n").ConfigureAwait(false);
                        await RunGitAsync(repository.Path, "add", ".").ConfigureAwait(false);
                        await RunGitAsync(repository.Path, "commit", "-m", "Bump commit for cache-invalidation test").ConfigureAwait(false);

                        // Re-index so IndexedCommitSha changes.
                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        // Cache metadata still references the old SHA; new indexed SHA differs -> cache miss.
                        ContextPackResponse? cached = await service.TryGetCachedContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "any goal"
                        }).ConfigureAwait(false);

                        AssertTrue(cached == null, "Cache should miss after indexed commit SHA changes");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("WarmBaselineCacheAsync_CalledTwice_SecondCallIsNoOp", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-warm-noop-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        string cacheDir = Path.Combine(
                            dataRoot, "code-index", vessel.Id, "baseline-cache");
                        string packPath = Path.Combine(cacheDir, "baseline-pack.md");

                        DateTime firstWriteTime = File.GetLastWriteTimeUtc(packPath);

                        // Second warm-up should detect the existing valid cache and skip regeneration.
                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        DateTime secondWriteTime = File.GetLastWriteTimeUtc(packPath);

                        AssertEqual(firstWriteTime, secondWriteTime, "Second warm-up should not overwrite the cached pack file");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("WarmBaselineCacheAsync_NullOrWhitespaceVesselId_Throws", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-warm-arg-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await AssertThrowsAsync<ArgumentNullException>(
                            () => service.WarmBaselineCacheAsync(null!),
                            "null vesselId must throw").ConfigureAwait(false);
                        await AssertThrowsAsync<ArgumentNullException>(
                            () => service.WarmBaselineCacheAsync("   "),
                            "whitespace vesselId must throw").ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("TryGetCachedContextPackAsync_NullRequestOrVesselId_Throws", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-get-arg-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await AssertThrowsAsync<ArgumentNullException>(
                            () => service.TryGetCachedContextPackAsync(null!),
                            "null request must throw").ConfigureAwait(false);
                        await AssertThrowsAsync<ArgumentNullException>(
                            () => service.TryGetCachedContextPackAsync(new ContextPackRequest { VesselId = "  ", Goal = "g" }),
                            "whitespace VesselId must throw").ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("WarmBaselineCacheAsync_VesselNeverIndexed_NoOpsAndLeavesNoCache", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-warm-noindex-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        // No UpdateAsync call: status has no IndexedCommitSha, so warm-up must short-circuit.
                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        string cacheDir = Path.Combine(dataRoot, "code-index", vessel.Id, "baseline-cache");
                        AssertFalse(Directory.Exists(cacheDir), "No cache directory should be created when the vessel was never indexed");

                        ContextPackResponse? cached = await service.TryGetCachedContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "any goal"
                        }).ConfigureAwait(false);
                        AssertTrue(cached == null, "Cache must miss when there is no indexed commit SHA");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("TryGetCachedContextPackAsync_CorruptMetadataJson_ReturnsCacheMiss", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-corrupt-meta-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        string metadataPath = Path.Combine(
                            dataRoot, "code-index", vessel.Id, "baseline-cache", "baseline-metadata.json");
                        AssertTrue(File.Exists(metadataPath), "Warm-up should have written cache metadata");
                        await File.WriteAllTextAsync(metadataPath, "{ this is not valid json").ConfigureAwait(false);

                        ContextPackResponse? cached = await service.TryGetCachedContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "any goal"
                        }).ConfigureAwait(false);

                        AssertTrue(cached == null, "Corrupt metadata JSON must be swallowed and treated as a cache miss");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("TryGetCachedContextPackAsync_MaterializedPackFileMissing_ReturnsCacheMiss", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-missing-pack-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        // Metadata stays valid but the materialized pack file is gone (e.g. cleaned externally).
                        string packPath = Path.Combine(
                            dataRoot, "code-index", vessel.Id, "baseline-cache", "baseline-pack.md");
                        AssertTrue(File.Exists(packPath), "Warm-up should have written the pack file");
                        File.Delete(packPath);

                        ContextPackResponse? cached = await service.TryGetCachedContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "any goal"
                        }).ConfigureAwait(false);

                        AssertTrue(cached == null, "Missing materialized pack file must be treated as a cache miss");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("WarmBaselineCacheAsync_AfterShaChange_ReWarmRebuildsCacheForNewSha", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-rewarm-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        await File.WriteAllTextAsync(
                            Path.Combine(repository.Path, "docs", "usage.md"),
                            "# Usage\n\nUpdated content to bump the commit sha for re-warm.\n").ConfigureAwait(false);
                        await RunGitAsync(repository.Path, "add", ".").ConfigureAwait(false);
                        await RunGitAsync(repository.Path, "commit", "-m", "Bump commit for re-warm test").ConfigureAwait(false);
                        string newSha = (await RunGitAsync(repository.Path, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                        AssertNotEqual(repository.CommitSha, newSha, "Test setup should produce a different commit SHA");

                        CodeIndexStatus reindexed = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        AssertEqual(newSha, reindexed.IndexedCommitSha, "Re-index should record the new commit SHA");

                        // Re-warm against the new SHA, then the cache should hit and key on the new SHA.
                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        ContextPackResponse? cached = await service.TryGetCachedContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "any goal"
                        }).ConfigureAwait(false);

                        AssertTrue(cached != null, "Re-warm after a SHA change should produce a fresh cache hit");
                        AssertTrue(cached!.Metrics.CacheHit, "Re-warmed response should report a cache hit");
                        AssertEqual(newSha, cached.Metrics.CacheKey, "Cache key must track the new indexed commit SHA");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("WarmBaselineCacheAsync_CorruptExistingMetadata_RegeneratesValidCache", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-regen-meta-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        // Corrupt the existing metadata so the warm-up cannot confirm a valid cache and must regenerate.
                        string metadataPath = Path.Combine(
                            dataRoot, "code-index", vessel.Id, "baseline-cache", "baseline-metadata.json");
                        await File.WriteAllTextAsync(metadataPath, "<<<corrupt>>>").ConfigureAwait(false);

                        await service.WarmBaselineCacheAsync(vessel.Id).ConfigureAwait(false);

                        ContextPackResponse? cached = await service.TryGetCachedContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "any goal"
                        }).ConfigureAwait(false);

                        AssertTrue(cached != null, "Warm-up over corrupt metadata should regenerate a usable cache");
                        AssertTrue(cached!.Metrics.CacheHit, "Regenerated cache should report a cache hit");
                        AssertEqual(repository.CommitSha, cached.Metrics.CacheKey, "Regenerated cache should key on the indexed commit SHA");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });
        }

        private static List<CodeIndexRecord> BuildSemanticBlendCorpus(string vesselId)
        {
            return new List<CodeIndexRecord>
            {
                new CodeIndexRecord
                {
                    VesselId = vesselId,
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
                    VesselId = vesselId,
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

        private sealed class ThrowingEmbeddingClient : IEmbeddingClient
        {
            private readonly Exception _Exception;

            public int CallCount { get; private set; }

            public ThrowingEmbeddingClient(Exception exception)
            {
                _Exception = exception ?? throw new ArgumentNullException(nameof(exception));
            }

            public Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                CallCount++;
                throw _Exception;
            }
        }

        private sealed class RecordingEmbeddingClient : IEmbeddingClient
        {
            private readonly float[] _Vector;

            public int CallCount { get; private set; }

            public List<string> Inputs { get; } = new List<string>();

            public RecordingEmbeddingClient(float[] vector)
            {
                _Vector = vector ?? throw new ArgumentNullException(nameof(vector));
            }

            public Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                CallCount++;
                Inputs.Add(text ?? string.Empty);
                return Task.FromResult(_Vector);
            }
        }

        private sealed class BatchRecordingEmbeddingClient : IEmbeddingClient
        {
            private readonly float[] _Vector;

            public int BatchCallCount { get; private set; }

            public int SingleCallCount { get; private set; }

            public BatchRecordingEmbeddingClient(float[] vector)
            {
                _Vector = vector ?? throw new ArgumentNullException(nameof(vector));
            }

            public Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                SingleCallCount++;
                return Task.FromResult(_Vector);
            }

            public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken token = default)
            {
                BatchCallCount++;
                IReadOnlyList<float[]> vectors = texts.Select(_ => _Vector).ToList();
                return Task.FromResult(vectors);
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
