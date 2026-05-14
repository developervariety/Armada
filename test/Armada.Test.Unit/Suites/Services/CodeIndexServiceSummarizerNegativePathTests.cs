namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
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
    /// Negative-path and edge-case coverage for the Layer 3 context pack summarizer in
    /// <see cref="CodeIndexService.BuildContextPackAsync"/>. Complements the Worker-authored
    /// happy-path tests in <c>CodeIndexServiceSummarizerTests</c>.
    /// </summary>
    public class CodeIndexServiceSummarizerNegativePathTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Code Index Service Summarizer Negative Paths";

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("BuildContextPackAsync_NullInferenceClient_LeavesSummarizationDisabled", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-summarizer-null-client-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            inferenceClient: null,
                            configureCodeIndex: ci =>
                            {
                                ci.UseSemanticSearch = false;
                                ci.UseSummarizer = true;
                            });

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        }).ConfigureAwait(false);

                        AssertFalse(response.IsSummarized, "IsSummarized must stay false when no inference client is wired");
                        AssertTrue(response.SummarizedMarkdown == null, "SummarizedMarkdown must be null when summarizer is disabled");

                        string materialized = await File.ReadAllTextAsync(response.MaterializedPath).ConfigureAwait(false);
                        AssertEqual(response.Markdown, materialized, "Materialized file must contain the raw markdown when summarizer is disabled");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_InferenceThrows_SwallowsAndMaterializesRawMarkdown", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-summarizer-throws-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        ThrowingInferenceClient inference = new ThrowingInferenceClient(new InvalidOperationException("synthetic failure"));
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

                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        }).ConfigureAwait(false);

                        AssertFalse(response.IsSummarized, "IsSummarized must be false when inference throws");
                        AssertTrue(response.SummarizedMarkdown == null, "SummarizedMarkdown must be null when inference throws");
                        AssertEqual(1, inference.CallCount, "Inference client must have been invoked exactly once");

                        string materialized = await File.ReadAllTextAsync(response.MaterializedPath).ConfigureAwait(false);
                        AssertEqual(response.Markdown, materialized, "Materialized file must fall back to raw markdown when inference throws");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_NoSearchResults_DoesNotCallInference", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-summarizer-no-results-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingInferenceClient inference = new RecordingInferenceClient((s, u, t) => "should never run");
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

                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "zzz_nonexistent_query_token_qq",
                            TokenBudget = 1000
                        }).ConfigureAwait(false);

                        AssertEqual(0, response.Results.Count, "Search must produce zero results for the non-matching goal");
                        AssertEqual(0, inference.CallCount, "Summarizer must short-circuit when there are no search results");
                        AssertFalse(response.IsSummarized, "IsSummarized must be false when no results were summarized");
                        AssertTrue(response.SummarizedMarkdown == null, "SummarizedMarkdown must be null when summarizer was skipped");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_Summarized_RawMarkdownFieldStillContainsBuilderOutput", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-summarizer-raw-preserved-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        string summary = "## summarized output for raw-preservation check";
                        RecordingInferenceClient inference = new RecordingInferenceClient((s, u, t) => summary);
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

                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        }).ConfigureAwait(false);

                        AssertTrue(response.IsSummarized, "Summarization must succeed for this case");
                        AssertEqual(summary, response.SummarizedMarkdown, "SummarizedMarkdown must hold the inference output");
                        AssertFalse(response.Markdown == summary, "Markdown field must remain the raw builder output, not the summary");
                        AssertTrue(response.Markdown.Length > 0, "Raw markdown field must still contain the unsummarized builder output");
                        AssertContains("alpha", response.Markdown, "Raw markdown must reflect the search goal context");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_Summarized_PrestagedFileSourceEqualsMaterializedPath", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-summarizer-prestaged-source-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        string summary = "summary token: prestaged source check";
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            inferenceClient: new RecordingInferenceClient((s, u, t) => summary),
                            configureCodeIndex: ci =>
                            {
                                ci.UseSemanticSearch = false;
                                ci.UseSummarizer = true;
                            });

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        }).ConfigureAwait(false);

                        AssertTrue(response.PrestagedFiles.Count >= 1, "Response must include at least one prestaged file");
                        PrestagedFile entry = response.PrestagedFiles[0];
                        AssertEqual(response.MaterializedPath, entry.SourcePath, "Prestaged source path must equal the materialized summary path");
                        AssertEqual("_briefing/context-pack.md", entry.DestPath, "Prestaged dest path must be _briefing/context-pack.md");

                        string sourceContents = await File.ReadAllTextAsync(entry.SourcePath).ConfigureAwait(false);
                        AssertEqual(summary, sourceContents, "Prestaged source file must contain the summarized markdown, not the raw markdown");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_InferenceClient_ReceivesGoalPrefixedUserMessageAndSystemPrompt", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-summarizer-prompt-shape-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingInferenceClient inference = new RecordingInferenceClient((s, u, t) => "ok");
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

                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        }).ConfigureAwait(false);

                        AssertEqual(1, inference.CallCount, "Inference client must be invoked exactly once");
                        AssertNotNull(inference.LastSystemPrompt, "System prompt must be captured");
                        AssertNotNull(inference.LastUserMessage, "User message must be captured");
                        AssertContains("codebase analyst", inference.LastSystemPrompt!, "System prompt must brief the model as a codebase analyst");
                        AssertContains("Output only the summary markdown", inference.LastSystemPrompt!, "System prompt must instruct summary-only output");
                        AssertTrue(inference.LastUserMessage!.StartsWith("Goal: alpha\n\n", StringComparison.Ordinal), "User message must begin with 'Goal: <goal>' followed by a blank line");
                        AssertContains(response.Markdown, inference.LastUserMessage!, "User message must include the raw context-pack markdown");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_CancellationToken_FlowsToInferenceCompleteAsync", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-summarizer-cancellation-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        RecordingInferenceClient inference = new RecordingInferenceClient((s, u, t) => "ok");
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

                        using (CancellationTokenSource cts = new CancellationTokenSource())
                        {
                            await service.BuildContextPackAsync(new ContextPackRequest
                            {
                                VesselId = vessel.Id,
                                Goal = "alpha",
                                TokenBudget = 1000
                            }, cts.Token).ConfigureAwait(false);

                            AssertEqual(1, inference.CallCount, "Inference client must be invoked once");
                            AssertTrue(inference.LastCancellationToken.HasValue, "Cancellation token must be captured");
                            AssertEqual(cts.Token, inference.LastCancellationToken!.Value, "Captured cancellation token must equal the caller-supplied token");
                        }
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("ProductionComposition_AdmiralAndHelmBothPassInferenceClientToSummarizer", () =>
            {
                string repositoryRoot = FindRepositoryRoot();

                string admiralWiring = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Armada.Server", "ArmadaServer.cs"));
                AssertContains(
                    "new DeepSeekInferenceClient(_Settings.CodeIndex, _Logging, codeIndexHttpClient)",
                    admiralWiring,
                    "Admiral must construct a DeepSeekInferenceClient backed by CodeIndex settings");
                AssertContains(
                    "new CodeIndexService(_Logging, _Database, _Settings, _Git, embeddingClient, inferenceClient)",
                    admiralWiring,
                    "Admiral must pass inferenceClient into CodeIndexService so the summarizer is reachable from production");

                string helmWiring = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Armada.Helm", "Commands", "McpStdioCommand.cs"));
                AssertContains(
                    "new DeepSeekInferenceClient(armadaSettings.CodeIndex, logging, codeIndexHttpClient)",
                    helmWiring,
                    "Helm stdio MCP composition must construct a DeepSeekInferenceClient backed by CodeIndex settings");
                AssertContains(
                    "new CodeIndexService(logging, database, armadaSettings, git, embeddingClient, inferenceClient)",
                    helmWiring,
                    "Helm stdio MCP composition must pass inferenceClient into CodeIndexService so the summarizer is reachable from production");

                string summarizerImpl = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Armada.Core", "Services", "CodeIndexService.cs"));
                AssertContains(
                    "_Settings.CodeIndex.UseSummarizer && _InferenceClient != null && search.Results.Count > 0",
                    summarizerImpl,
                    "Summarizer guard must require UseSummarizer + non-null inference client + at least one search result");
                AssertContains(
                    "isSummarized ? summarizedMarkdown! : markdown",
                    summarizerImpl,
                    "WriteContextPackAsync must materialize the summarized markdown when summarization succeeded");

                return Task.CompletedTask;
            });
        }

        #endregion

        #region Private-Methods

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
                Name = "code-index-summarizer-neg-vessel-" + Guid.NewGuid().ToString("N"),
                RepoUrl = repositoryPath,
                WorkingDirectory = repositoryPath,
                DefaultBranch = "main"
            };

            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<TestRepository> CreateRepositoryAsync()
        {
            string root = NewTempDirectory("armada-code-index-summarizer-neg-repo-");
            string repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);

            try
            {
                await RunGitAsync(repo, "init", "-b", "main").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                Directory.CreateDirectory(Path.Combine(repo, "src"));
                await File.WriteAllTextAsync(
                    Path.Combine(repo, "src", "alpha.cs"),
                    "namespace Sample;\npublic static class AlphaHelper { public static string Value = \"alpha alpha\"; }\n").ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(repo, "src", "beta.cs"),
                    "namespace Sample;\npublic static class BetaHelper { public static string Value = \"alpha beta\"; }\n").ConfigureAwait(false);

                await RunGitAsync(repo, "add", ".").ConfigureAwait(false);
                await RunGitAsync(repo, "commit", "-m", "Add summarizer negative-path fixtures").ConfigureAwait(false);
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

        #endregion

        #region Test-Doubles

        private sealed class RecordingInferenceClient : IInferenceClient
        {
            private readonly Func<string, string, CancellationToken, string> _Handler;

            public int CallCount { get; private set; }

            public string? LastSystemPrompt { get; private set; }

            public string? LastUserMessage { get; private set; }

            public CancellationToken? LastCancellationToken { get; private set; }

            public RecordingInferenceClient(Func<string, string, CancellationToken, string> handler)
            {
                _Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken token = default)
            {
                CallCount++;
                LastSystemPrompt = systemPrompt;
                LastUserMessage = userMessage;
                LastCancellationToken = token;
                return Task.FromResult(_Handler(systemPrompt ?? "", userMessage ?? "", token));
            }
        }

        private sealed class ThrowingInferenceClient : IInferenceClient
        {
            private readonly Exception _ToThrow;

            public int CallCount { get; private set; }

            public ThrowingInferenceClient(Exception toThrow)
            {
                _ToThrow = toThrow ?? throw new ArgumentNullException(nameof(toThrow));
            }

            public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken token = default)
            {
                CallCount++;
                throw _ToThrow;
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

        #endregion
    }
}
