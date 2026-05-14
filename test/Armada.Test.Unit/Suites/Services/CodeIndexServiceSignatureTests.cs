namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for Layer 5 file signature indexing and ranking behavior.
    /// </summary>
    public class CodeIndexServiceSignatureTests : TestSuite
    {
        private static readonly JsonSerializerOptions _IndexJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <inheritdoc />
        public override string Name => "Code Index Service Signatures";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("UpdateAsync_UseFileSignaturesFalse_DoesNotWriteSignaturesJsonl", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-signatures-off-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingEmbeddingClient embedding = new RecordingEmbeddingClient(new float[] { 0F, 1F });
                        RecordingInferenceClient inference = new RecordingInferenceClient(_ => "unused");
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            embedding,
                            inference,
                            ci =>
                            {
                                ci.UseSemanticSearch = false;
                                ci.UseFileSignatures = false;
                            });

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        string signaturesPath = Path.Combine(status.IndexDirectory, "signatures.jsonl");
                        AssertFalse(File.Exists(signaturesPath), "signatures.jsonl should not be created when UseFileSignatures is false");
                        AssertEqual(0, inference.CallCount, "inference must not run when UseFileSignatures is false");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchAsync_UseFileSignaturesFalse_LeavesRankingUnchangedEvenIfSignatureFileExists", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-signatures-noeffect-");

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
                                EndLine = 20,
                                Content = "alpha alpha",
                                EmbeddingVector = new float[] { 0F, 1F }
                            },
                            new CodeIndexRecord
                            {
                                VesselId = vessel.Id,
                                Path = "target.cs",
                                CommitSha = "abc",
                                ContentHash = "t1",
                                Language = "csharp",
                                StartLine = 1,
                                EndLine = 20,
                                Content = "alpha",
                                EmbeddingVector = new float[] { 0F, 1F }
                            }
                        };

                        ArmadaSettings settings = BuildSettings(dataRoot, ci =>
                        {
                            ci.UseSemanticSearch = true;
                            ci.UseFileSignatures = false;
                            ci.FileSignatureBoostWeight = 1.0;
                        });
                        await WritePersistedIndexAsync(settings, vessel, records).ConfigureAwait(false);
                        await WriteSignaturesAsync(settings, vessel.Id, new List<FileSignatureRecord>
                        {
                            new FileSignatureRecord
                            {
                                VesselId = vessel.Id,
                                Path = "target.cs",
                                CommitSha = "abc",
                                ContentHash = "t1",
                                Language = "csharp",
                                Signature = "alpha handler",
                                SignatureVector = new float[] { 1F, 0F }
                            }
                        }).ConfigureAwait(false);

                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            new RoutingEmbeddingClient(
                                defaultVector: new float[] { 0F, 1F },
                                queryVector: new float[] { 1F, 0F }),
                            inferenceClient: null,
                            configureCodeIndex: ci =>
                            {
                                ci.UseSemanticSearch = true;
                                ci.UseFileSignatures = false;
                                ci.FileSignatureBoostWeight = 1.0;
                            });

                        CodeSearchResponse response = await service.SearchAsync(new CodeSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "alpha",
                            Limit = 10,
                            IncludeContent = true
                        }).ConfigureAwait(false);

                        AssertEqual(2, response.Results.Count);
                        AssertEqual("noisy.cs", response.Results[0].Record.Path);
                        AssertEqual("target.cs", response.Results[1].Record.Path);
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchAsync_UseFileSignaturesTrue_BoostsChunksFromMatchedFile", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-signatures-boost-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RoutingEmbeddingClient embedding = new RoutingEmbeddingClient(
                            defaultVector: new float[] { 0F, 1F },
                            queryVector: new float[] { 1F, 0F },
                            targetSignatureText: "target file handles alpha");
                        RecordingInferenceClient inference = new RecordingInferenceClient(userMessage =>
                        {
                            if (userMessage.Contains("File: src/target.cs", StringComparison.Ordinal))
                            {
                                return "target file handles alpha";
                            }

                            return "misc helper file";
                        });

                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            embedding,
                            inference,
                            ci =>
                            {
                                ci.UseSemanticSearch = true;
                                ci.UseFileSignatures = true;
                                ci.FileSignatureBoostWeight = 1.0;
                            });

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        CodeSearchResponse response = await service.SearchAsync(new CodeSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "alpha",
                            Limit = 10,
                            IncludeContent = true
                        }).ConfigureAwait(false);

                        AssertTrue(response.Results.Count >= 2, "Expected both files to rank");
                        AssertEqual("src/target.cs", response.Results[0].Record.Path);
                        AssertEqual("src/noisy.cs", response.Results[1].Record.Path);
                        AssertTrue(inference.CallCount >= 2, "Inference should run once per file");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_InferenceReturnsBlank_SkipsFileSignatureRecord", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-signatures-blank-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingInferenceClient inference = new RecordingInferenceClient(userMessage =>
                        {
                            if (userMessage.Contains("File: src/noisy.cs", StringComparison.Ordinal))
                            {
                                return "   ";
                            }

                            return "target file handles alpha";
                        });
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            new RecordingEmbeddingClient(new float[] { 1F, 0F }),
                            inference,
                            ci =>
                            {
                                ci.UseSemanticSearch = false;
                                ci.UseFileSignatures = true;
                            });

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        List<FileSignatureRecord> signatures = await ReadSignatureRecordsAsync(
                            Path.Combine(status.IndexDirectory, "signatures.jsonl")).ConfigureAwait(false);
                        AssertEqual(1, signatures.Count);
                        AssertEqual("src/target.cs", signatures[0].Path);
                        AssertEqual("target file handles alpha", signatures[0].Signature);
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_EmbeddingReturnsEmptyVector_SkipsFileSignatureRecord", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-signatures-empty-vector-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingInferenceClient inference = new RecordingInferenceClient(userMessage =>
                        {
                            if (userMessage.Contains("File: src/noisy.cs", StringComparison.Ordinal))
                            {
                                return "empty vector file";
                            }

                            return "target file handles alpha";
                        });
                        SelectiveEmbeddingClient embedding = new SelectiveEmbeddingClient(text =>
                        {
                            if (String.Equals(text, "empty vector file", StringComparison.Ordinal))
                            {
                                return Array.Empty<float>();
                            }

                            return new float[] { 1F, 0F };
                        });
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            embedding,
                            inference,
                            ci =>
                            {
                                ci.UseSemanticSearch = false;
                                ci.UseFileSignatures = true;
                            });

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        List<FileSignatureRecord> signatures = await ReadSignatureRecordsAsync(
                            Path.Combine(status.IndexDirectory, "signatures.jsonl")).ConfigureAwait(false);
                        AssertEqual(1, signatures.Count);
                        AssertEqual("src/target.cs", signatures[0].Path);
                        AssertEqual("target file handles alpha", signatures[0].Signature);
                        AssertTrue(embedding.CallCount >= 2, "Embedding should run for both non-empty signatures");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("FileSignatureRecord_RoundTripsJsonl_WithNullAndNonNullVectors", () =>
            {
                FileSignatureRecord withVector = new FileSignatureRecord
                {
                    VesselId = "v1",
                    Path = "src/a.cs",
                    CommitSha = "abc",
                    ContentHash = "h1",
                    Language = "csharp",
                    Signature = "A handles alpha.",
                    SignatureVector = new float[] { 0.25F, -0.5F },
                    GeneratedAtUtc = DateTime.UtcNow
                };
                string jsonWithVector = JsonSerializer.Serialize(withVector, _IndexJsonOptions);
                FileSignatureRecord? parsedWithVector = JsonSerializer.Deserialize<FileSignatureRecord>(jsonWithVector, _IndexJsonOptions);
                AssertTrue(parsedWithVector != null, "Deserialize with vector");
                AssertTrue(parsedWithVector!.SignatureVector != null, "SignatureVector should round-trip");
                AssertEqual(2, parsedWithVector.SignatureVector!.Length);
                AssertEqual(0.25F, parsedWithVector.SignatureVector[0]);
                AssertEqual(-0.5F, parsedWithVector.SignatureVector[1]);

                FileSignatureRecord nullVector = new FileSignatureRecord
                {
                    VesselId = "v1",
                    Path = "src/b.cs",
                    Signature = "B handles beta.",
                    SignatureVector = null
                };
                string jsonNullVector = JsonSerializer.Serialize(nullVector, _IndexJsonOptions);
                FileSignatureRecord? parsedNullVector = JsonSerializer.Deserialize<FileSignatureRecord>(jsonNullVector, _IndexJsonOptions);
                AssertTrue(parsedNullVector != null, "Deserialize null vector");
                AssertTrue(parsedNullVector!.SignatureVector == null, "Null SignatureVector should round-trip");
            });

            await RunTest("SourceGuard_ArmadaServerCompositionPath_WithUseFileSignaturesTrue_WritesNonEmptySignaturesJsonl", async () =>
            {
                string armadaServerPath = Path.Combine(FindRepositoryRoot(), "src", "Armada.Server", "ArmadaServer.cs");
                string armadaServerContents = File.ReadAllText(armadaServerPath);
                AssertContains(
                    "new CodeIndexService(_Logging, _Database, _Settings, _Git, embeddingClient, inferenceClient)",
                    armadaServerContents,
                    "Source guard: ArmadaServer must pass embeddingClient and inferenceClient into CodeIndexService");

                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-signatures-source-guard-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        LoggingModule logging = SilentLogging();
                        ArmadaSettings settings = BuildSettings(dataRoot, ci =>
                        {
                            ci.UseSemanticSearch = true;
                            ci.UseFileSignatures = true;
                            ci.FileSignatureBoostWeight = 1.0;
                        });
                        IEmbeddingClient embeddingClient = new RoutingEmbeddingClient(
                            defaultVector: new float[] { 0F, 1F },
                            queryVector: new float[] { 1F, 0F },
                            targetSignatureText: "target file handles alpha");
                        IInferenceClient inferenceClient = new RecordingInferenceClient(_ => "target file handles alpha");
                        CodeIndexService service = new CodeIndexService(
                            logging,
                            testDb.Driver,
                            settings,
                            new GitService(logging),
                            embeddingClient,
                            inferenceClient);

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        string signaturesPath = Path.Combine(status.IndexDirectory, "signatures.jsonl");
                        AssertTrue(File.Exists(signaturesPath), "signatures.jsonl should be written");
                        string[] lines = await File.ReadAllLinesAsync(signaturesPath).ConfigureAwait(false);
                        AssertTrue(lines.Any(line => !String.IsNullOrWhiteSpace(line)), "signatures.jsonl should contain at least one record");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
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
                UseSemanticSearch = false,
                UseFileSignatures = false
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

        private static CodeIndexService CreateService(
            TestDatabase testDb,
            string dataRoot,
            IEmbeddingClient? embeddingClient,
            IInferenceClient? inferenceClient,
            Action<CodeIndexSettings>? configureCodeIndex)
        {
            ArmadaSettings settings = BuildSettings(dataRoot, configureCodeIndex);
            LoggingModule logging = SilentLogging();
            return new CodeIndexService(logging, testDb.Driver, settings, new GitService(logging), embeddingClient, inferenceClient);
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

        private static async Task WriteSignaturesAsync(ArmadaSettings settings, string vesselId, IReadOnlyList<FileSignatureRecord> signatures)
        {
            string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vesselId);
            Directory.CreateDirectory(indexDir);
            string signaturesPath = Path.Combine(indexDir, "signatures.jsonl");
            using (StreamWriter writer = new StreamWriter(signaturesPath, false, new UTF8Encoding(false)))
            {
                foreach (FileSignatureRecord signature in signatures)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(signature, _IndexJsonOptions)).ConfigureAwait(false);
                }
            }
        }

        private static async Task<List<FileSignatureRecord>> ReadSignatureRecordsAsync(string signaturesPath)
        {
            List<FileSignatureRecord> signatures = new List<FileSignatureRecord>();
            if (!File.Exists(signaturesPath))
            {
                return signatures;
            }

            string[] lines = await File.ReadAllLinesAsync(signaturesPath).ConfigureAwait(false);
            foreach (string line in lines)
            {
                if (String.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                FileSignatureRecord? signature = JsonSerializer.Deserialize<FileSignatureRecord>(line, _IndexJsonOptions);
                if (signature != null)
                {
                    signatures.Add(signature);
                }
            }

            return signatures;
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string repositoryPath)
        {
            Vessel vessel = new Vessel
            {
                Name = "code-index-signature-vessel-" + Guid.NewGuid().ToString("N"),
                RepoUrl = repositoryPath,
                WorkingDirectory = repositoryPath,
                DefaultBranch = "main"
            };

            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<TestRepository> CreateRepositoryAsync(string noisyContent, string targetContent)
        {
            string root = NewTempDirectory("armada-code-index-signature-repo-");
            string repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);

            try
            {
                await RunGitAsync(repo, "init", "-b", "main").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                Directory.CreateDirectory(Path.Combine(repo, "src"));
                await File.WriteAllTextAsync(
                    Path.Combine(repo, "src", "noisy.cs"),
                    "namespace Sample;\npublic static class Noisy { public static string Value = \"" + noisyContent + "\"; }\n").ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(repo, "src", "target.cs"),
                    "namespace Sample;\npublic static class Target { public static string Value = \"" + targetContent + "\"; }\n").ConfigureAwait(false);

                await RunGitAsync(repo, "add", ".").ConfigureAwait(false);
                await RunGitAsync(repo, "commit", "-m", "Add signature ranking fixtures").ConfigureAwait(false);
                string commitSha = (await RunGitAsync(repo, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                return new TestRepository(root, repo, commitSha);
            }
            catch
            {
                TryDeleteDirectory(root);
                throw;
            }
        }

        private static LoggingModule SilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src"))
                    && Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
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
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
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

        private sealed class RoutingEmbeddingClient : IEmbeddingClient
        {
            private readonly float[] _DefaultVector;
            private readonly float[] _QueryVector;
            private readonly string _TargetSignatureText;

            public RoutingEmbeddingClient(float[] defaultVector, float[] queryVector, string targetSignatureText = "target file handles alpha")
            {
                _DefaultVector = defaultVector;
                _QueryVector = queryVector;
                _TargetSignatureText = targetSignatureText;
            }

            public Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                if ((text ?? "").Contains(_TargetSignatureText, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(_QueryVector);
                }

                if (String.Equals(text, "alpha", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(_QueryVector);
                }

                return Task.FromResult(_DefaultVector);
            }
        }

        private sealed class RecordingEmbeddingClient : IEmbeddingClient
        {
            private readonly float[] _Vector;

            public int CallCount { get; private set; }

            public RecordingEmbeddingClient(float[] vector)
            {
                _Vector = vector;
            }

            public Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                CallCount++;
                return Task.FromResult(_Vector);
            }
        }

        private sealed class SelectiveEmbeddingClient : IEmbeddingClient
        {
            private readonly Func<string, float[]> _Handler;

            public int CallCount { get; private set; }

            public SelectiveEmbeddingClient(Func<string, float[]> handler)
            {
                _Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            public Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                CallCount++;
                return Task.FromResult(_Handler(text ?? String.Empty));
            }
        }

        private sealed class RecordingInferenceClient : IInferenceClient
        {
            private readonly Func<string, string> _Handler;

            public int CallCount { get; private set; }

            public RecordingInferenceClient(Func<string, string> handler)
            {
                _Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken token = default)
            {
                CallCount++;
                return Task.FromResult(_Handler(userMessage ?? String.Empty));
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
