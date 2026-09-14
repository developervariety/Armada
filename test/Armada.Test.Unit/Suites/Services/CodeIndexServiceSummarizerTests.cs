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
    using Microsoft.Extensions.Time.Testing;
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

            await RunTest("BuildContextPackAsync_SlowSummarizer_TimesOutAndFallsBackToRawMarkdown", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-summarizer-slow-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        BlockingInferenceClient inference = new BlockingInferenceClient("slow summary that arrives too late");
                        TimerSignalingTimeProvider time = new TimerSignalingTimeProvider(TimeSpan.FromSeconds(1));
                        CodeIndexService service = CreateService(
                            testDb,
                            dataRoot,
                            inferenceClient: inference,
                            configureCodeIndex: ci =>
                            {
                                ci.UseSemanticSearch = false;
                                ci.UseSummarizer = true;
                                ci.SummarizerTimeoutSeconds = 1;
                            },
                            timeProvider: time);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        // Advance exactly the per-call timeout after the call starts. The default context-pack
                        // budget is longer, so only the summarizer timeout can end the wait.
                        Task<ContextPackResponse> build = service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        });
                        // The timeout timer is created only after the summarizer call has started, so advancing on
                        // the call signal alone can move the clock before the timer exists and the timer never
                        // fires. Advance once both the call and its timeout timer are in place.
                        await StageSignal.WaitForAsync(inference.Entered, build, "summarizer call").ConfigureAwait(false);
                        await StageSignal.WaitForAsync(time.WatchedTimerCreated, build, "summarizer timeout timer").ConfigureAwait(false);
                        time.Advance(TimeSpan.FromSeconds(1));
                        ContextPackResponse response = await build.ConfigureAwait(false);
                        bool summarizerStillPending = !inference.Completed;
                        inference.Release();

                        AssertFalse(response.IsSummarized, "IsSummarized must be false when the summarizer times out");
                        AssertTrue(response.SummarizedMarkdown == null, "SummarizedMarkdown must be null on timeout");
                        AssertTrue(summarizerStillPending, "Build must stop waiting at the per-call timeout while the summarizer call is still pending");
                        AssertFalse(response.Warnings.Exists(w => w.Contains("context_pack_budget_expired")), "The per-call timeout, not the overall budget, must end the wait");
                        AssertEqual(1, inference.CallCount, "Inference client must have been invoked once");
                        AssertTrue(response.Warnings.Exists(w => w.Contains("summarizer_timeout")), "A non-blocking summarizer_timeout warning must be recorded");

                        string materialized = await File.ReadAllTextAsync(response.MaterializedPath).ConfigureAwait(false);
                        AssertEqual(response.Markdown, materialized, "Materialized file must fall back to raw markdown on timeout");

                        AssertTrue(response.Metrics.TotalElapsedMs >= 0, "Total elapsed metric must be populated");
                        AssertTrue(response.Metrics.SummarizerElapsedMs >= 0, "Summarizer elapsed metric must be populated");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_SummarizerSucceeds_MetricsExposeSearchAndSummarizerDurations", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync("alpha alpha", "alpha").ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-index-summarizer-metrics-");

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

                        ContextPackResponse response = await service.BuildContextPackAsync(new ContextPackRequest
                        {
                            VesselId = vessel.Id,
                            Goal = "alpha",
                            TokenBudget = 1000
                        }).ConfigureAwait(false);

                        AssertTrue(response.IsSummarized, "IsSummarized should be true on a successful summarization");
                        AssertEqual(mockOutput, response.SummarizedMarkdown, "SummarizedMarkdown should contain mock output");
                        AssertTrue(response.Metrics.SearchElapsedMs >= 0, "Search elapsed metric must be populated");
                        AssertTrue(response.Metrics.SummarizerElapsedMs >= 0, "Summarizer elapsed metric must be populated");
                        AssertTrue(response.Metrics.TotalElapsedMs >= 0, "Total elapsed metric must be populated");
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
            Action<CodeIndexSettings>? configureCodeIndex,
            TimeProvider? timeProvider = null)
        {
            ArmadaSettings settings = BuildSettings(dataRoot, configureCodeIndex);
            LoggingModule logging = SilentLogging();
            return new CodeIndexService(logging, testDb.Driver, settings, new GitService(logging), null, inferenceClient, timeProvider);
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

        /// <summary>
        /// A controlled clock that signals when a timer with the watched due time is created, so a test
        /// advances time only after the timer it means to fire exists.
        /// </summary>
        private sealed class TimerSignalingTimeProvider : FakeTimeProvider
        {
            private readonly TimeSpan _WatchedDueTime;
            private readonly TaskCompletionSource _WatchedTimerCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public TimerSignalingTimeProvider(TimeSpan watchedDueTime)
            {
                _WatchedDueTime = watchedDueTime;
            }

            /// <summary>Completes once a timer with the watched due time has been registered.</summary>
            public Task WatchedTimerCreated => _WatchedTimerCreated.Task;

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                ITimer timer = base.CreateTimer(callback, state, dueTime, period);
                if (dueTime == _WatchedDueTime) _WatchedTimerCreated.TrySetResult();
                return timer;
            }
        }

        private sealed class BlockingInferenceClient : IInferenceClient
        {
            private readonly string _Result;
            private readonly TaskCompletionSource _Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _CallCount;
            private int _Completed;

            public BlockingInferenceClient(string result)
            {
                _Result = result ?? String.Empty;
            }

            public int CallCount => Volatile.Read(ref _CallCount);

            /// <summary>Completes when the summarizer call has started.</summary>
            public Task Entered => _Entered.Task;

            /// <summary>True once the call has returned.</summary>
            public bool Completed => Volatile.Read(ref _Completed) != 0;

            /// <summary>Let the pending call return.</summary>
            public void Release() => _Release.TrySetResult();

            public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken token = default)
            {
                Interlocked.Increment(ref _CallCount);
                _Entered.TrySetResult();
                await _Release.Task.WaitAsync(token).ConfigureAwait(false);
                Interlocked.Exchange(ref _Completed, 1);
                return _Result;
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