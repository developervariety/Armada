namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the search-only fast-pack context path, the repo-size fast-pack decision,
    /// and the related budget/threshold settings on <see cref="CodeIndexService"/>.
    /// </summary>
    public class CodeIndexFastPackTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Code Index Fast Pack";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("BuildContextPackAsync_FastPackOnly_SkipsGraphExpansionButStillStagesPack", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-fast-pack-true-");

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
                            MaxResults = 4,
                            FastPackOnly = true
                        }).ConfigureAwait(false);

                        AssertEqual(0, response.GraphIncludedFiles.Count, "Fast pack must not add graph-expansion files");
                        AssertFalse(response.Metrics.GraphExpansionUsed, "Fast pack must report no graph expansion");
                        AssertTrue(response.Metrics.FastPackFallbackUsed, "Fast pack must report the fast-pack fallback was used");
                        AssertTrue(File.Exists(response.MaterializedPath), "Fast pack should still be materialized");
                        AssertEqual(1, response.PrestagedFiles.Count, "Fast pack should still stage a prestaged file");
                        AssertEqual("_briefing/context-pack.md", response.PrestagedFiles[0].DestPath);
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("BuildContextPackAsync_DefaultRequest_RunsGraphExpansionPathAndFlagsFastPackFalse", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-fast-pack-false-");

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

                        // Default path must not flag the fast-pack fallback. The graph-expansion path still
                        // runs for small repos; this pins the "do not remove graph-expansion" constraint.
                        AssertFalse(response.Metrics.FastPackFallbackUsed, "Default request must not report the fast-pack fallback");
                        AssertTrue(File.Exists(response.MaterializedPath), "Default pack should be materialized");
                        AssertEqual(1, response.PrestagedFiles.Count, "Default pack should stage a prestaged file");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("ShouldUseFastPackAsync_BelowThresholdFalse_AboveThresholdTrue", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-fast-pack-threshold-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);

                        // Index once so persisted status has a non-zero indexed file count (the fixture has 2 files).
                        CodeIndexService indexer = CreateService(testDb, dataRoot);
                        CodeIndexStatus status = await indexer.UpdateAsync(vessel.Id).ConfigureAwait(false);
                        AssertTrue(status.DocumentCount >= 2, "Fixture should index at least two files");

                        CodeIndexService highThreshold = CreateService(testDb, dataRoot, s => s.FastPackFileThreshold = 1000);
                        AssertFalse(
                            await highThreshold.ShouldUseFastPackAsync(vessel.Id).ConfigureAwait(false),
                            "File count below threshold should not use the fast pack");

                        CodeIndexService lowThreshold = CreateService(testDb, dataRoot, s => s.FastPackFileThreshold = 1);
                        AssertTrue(
                            await lowThreshold.ShouldUseFastPackAsync(vessel.Id).ConfigureAwait(false),
                            "File count above threshold should use the fast pack");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("CodeIndexSettings_ContextPackBudgetMs_And_FastPackFileThreshold_Clamp", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings();

                settings.ContextPackBudgetMs = 1;
                AssertEqual(500, settings.ContextPackBudgetMs, "ContextPackBudgetMs should clamp up to the minimum");
                settings.ContextPackBudgetMs = 999999;
                AssertEqual(120000, settings.ContextPackBudgetMs, "ContextPackBudgetMs should clamp down to the maximum");
                settings.ContextPackBudgetMs = 8000;
                AssertEqual(8000, settings.ContextPackBudgetMs, "In-range ContextPackBudgetMs should be preserved");

                settings.FastPackFileThreshold = -5;
                AssertEqual(0, settings.FastPackFileThreshold, "FastPackFileThreshold should clamp up to zero");
                settings.FastPackFileThreshold = 2500;
                AssertEqual(2500, settings.FastPackFileThreshold, "In-range FastPackFileThreshold should be preserved");

                return Task.CompletedTask;
            });
        }

        #region Helpers

        private static CodeIndexService CreateService(
            TestDatabase testDb,
            string dataRoot,
            Action<CodeIndexSettings>? configureCodeIndex = null)
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

            LoggingModule logging = SilentLogging();
            return new CodeIndexService(logging, testDb.Driver, settings, new GitService(logging), null, null);
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string repositoryPath)
        {
            Vessel vessel = new Vessel
            {
                Name = "fast-pack-vessel-" + Guid.NewGuid().ToString("N"),
                RepoUrl = repositoryPath,
                WorkingDirectory = repositoryPath,
                DefaultBranch = "main"
            };

            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<TestRepository> CreateRepositoryAsync()
        {
            string root = NewTempDirectory("armada-fast-pack-repo-");
            string repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);

            try
            {
                await RunGitAsync(repo, "init", "-b", "main").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                Directory.CreateDirectory(Path.Combine(repo, "src"));
                Directory.CreateDirectory(Path.Combine(repo, "docs"));

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "src", "CodeIndexTarget.cs"),
                    "namespace Sample\n{\n    public class CodeIndexTarget\n    {\n        public string SearchKeyword() => \"dispatch evidence\";\n    }\n}\n").ConfigureAwait(false);

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "docs", "usage.md"),
                    "# Usage\n\nThis document mentions context packs for mission briefs.\n").ConfigureAwait(false);

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
