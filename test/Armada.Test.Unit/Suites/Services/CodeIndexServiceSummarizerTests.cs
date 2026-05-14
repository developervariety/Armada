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
    /// Tests for Layer 3 context pack summarizer behavior.
    /// </summary>
    public class CodeIndexServiceSummarizerTests : TestSuite
    {
        private static readonly JsonSerializerOptions _IndexJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <inheritdoc />
        public override string Name => "Code Index Service Summarizer";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("BuildContextPackAsync_UseSummarizerFalse_ReturnsRawMarkdown", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-summarizer-off-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingInferenceClient inference = new RecordingInferenceClient(_ => "summarized mock output");
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            inferenceClient: inference,
                            configureCodeIndex: ci =>
                            {
                                ci.UseSemanticSearch = false;
                                ci.UseSummarizer = false;
                            });

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        ContextPackRequest request = new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        };

                        ContextPackResponse response = await service.BuildContextPackAsync(request).ConfigureAwait(false);

                        AssertFalse(response.IsSummarized, "IsSummarized should be false when UseSummarizer is false");
                        AssertTrue(response.SummarizedMarkdown == null, "SummarizedMarkdown should be null");
                        AssertEqual(0, inference.CallCount, "Inference client should not be called");
                        
                        string materializedContent = await File.ReadAllTextAsync(response.MaterializedPath).ConfigureAwait(false);
                        AssertEqual(response.Markdown, materializedContent, "Materialized file should contain raw markdown");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_UseSummarizerTrue_ReturnsSummarizedMarkdown", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-summarizer-on-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        string mockOutput = "summarized mock output";
                        RecordingInferenceClient inference = new RecordingInferenceClient(_ => mockOutput);
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            inferenceClient: inference,
                            configureCodeIndex: ci =>
                            {
                                ci.UseSemanticSearch = false;
                                ci.UseSummarizer = true;
                            });

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        ContextPackRequest request = new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        };

                        ContextPackResponse response = await service.BuildContextPackAsync(request).ConfigureAwait(false);

                        AssertTrue(response.IsSummarized, "IsSummarized should be true");
                        AssertEqual(mockOutput, response.SummarizedMarkdown, "SummarizedMarkdown should contain mock output");
                        AssertEqual(1, inference.CallCount, "Inference client should be called once");
                        
                        string materializedContent = await File.ReadAllTextAsync(response.MaterializedPath).ConfigureAwait(false);
                        AssertEqual(mockOutput, materializedContent, "Materialized file should contain summarized markdown");
                        
                        AssertTrue(response.PrestagedFiles.Count > 0, "Should have prestaged files");
                        AssertEqual("_briefing/context-pack.md", response.PrestagedFiles[0].DestPath, "DestPath should be _briefing/context-pack.md");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_InferenceReturnsEmpty_ReturnsRawMarkdown", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-summarizer-empty-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingInferenceClient inference = new RecordingInferenceClient(_ => "   ");
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            inferenceClient: inference,
                            configureCodeIndex: ci =>
                            {
                                ci.UseSemanticSearch = false;
                                ci.UseSummarizer = true;
                            });

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        ContextPackRequest request = new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        };

                        ContextPackResponse response = await service.BuildContextPackAsync(request).ConfigureAwait(false);

                        AssertFalse(response.IsSummarized, "IsSummarized should be false on empty inference output");
                        AssertTrue(response.SummarizedMarkdown == null, "SummarizedMarkdown should be null");
                        AssertEqual(1, inference.CallCount, "Inference client should be called once");
                        
                        string materializedContent = await File.ReadAllTextAsync(response.MaterializedPath).ConfigureAwait(false);
                        AssertEqual(response.Markdown, materializedContent, "Materialized file should contain raw markdown");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SourceGuard_ArmadaServerCompositionPath_WithUseSummarizerTrue_ReturnsSummarizedMarkdown", async () =>
            {
                string armadaServerPath = Path.Combine(FindRepositoryRoot(), "src", "Armada.Server", "ArmadaServer.cs");
                string armadaServerContents = File.ReadAllText(armadaServerPath);
                AssertContains(
                    "new CodeIndexService(_Logging, _Database, _Settings, _Git, embeddingClient, inferenceClient)",
                    armadaServerContents,
                    "Source guard: ArmadaServer must pass embeddingClient and inferenceClient into CodeIndexService");

                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-summarizer-source-guard-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        LoggingModule logging = SilentLogging();
                        ArmadaSettings settings = BuildSettings(dataRoot, ci =>
                        {
                            ci.UseSemanticSearch = false;
                            ci.UseSummarizer = true;
                        });
                        
                        string stubString = "recognizable stub string for source guard";
                        IInferenceClient inferenceClient = new RecordingInferenceClient(_ => stubString);
                        
                        CodeIndexService service = new CodeIndexService(
                            logging,
                            testDb.Driver,
                            settings,
                            new GitService(logging),
                            null,
                            inferenceClient);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        ContextPackRequest request = new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        };

                        ContextPackResponse response = await service.BuildContextPackAsync(request).ConfigureAwait(false);

                        AssertTrue(response.IsSummarized, "IsSummarized should be true");
                        AssertEqual(stubString, response.SummarizedMarkdown, "SummarizedMarkdown should contain the stub string");
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
                UseFileSignatures = false,
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

        private static CodeIndexService CreateService(
            TestDatabase testDb,
            string dataRoot,
            IInferenceClient? inferenceClient,
            Action<CodeIndexSettings>? configureCodeIndex)
        {
            ArmadaSettings settings = BuildSettings(dataRoot, configureCodeIndex);
            LoggingModule logging = SilentLogging();
            return new CodeIndexService(logging, testDb.Driver, settings, new GitService(logging), null, inferenceClient);
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string repositoryPath)
        {
            Vessel vessel = new Vessel
            {
                Name = "code-index-summarizer-vessel-" + Guid.NewGuid().ToString("N"),
                RepoUrl = repositoryPath,
                WorkingDirectory = repositoryPath,
                DefaultBranch = "main"
            };

            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<TestRepository> CreateRepositoryAsync(string noisyContent, string targetContent)
        {
            string root = NewTempDirectory("armada-code-index-summarizer-repo-");
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
                await RunGitAsync(repo, "commit", "-m", "Add summarizer ranking fixtures").ConfigureAwait(false);
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