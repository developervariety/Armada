namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for Admiral-owned code indexing, lexical search, and context-pack generation.
    /// </summary>
    public class CodeIndexServiceTests : TestSuite
    {
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
        }

        private static CodeIndexService CreateService(TestDatabase testDb, string dataRoot)
        {
            ArmadaSettings settings = new ArmadaSettings
            {
                DataDirectory = Path.Combine(dataRoot, "data"),
                ReposDirectory = Path.Combine(dataRoot, "repos"),
                CodeIndex = new CodeIndexSettings
                {
                    IndexDirectory = Path.Combine(dataRoot, "code-index"),
                    MaxChunkLines = 20,
                    MaxSearchResults = 10,
                    MaxContextPackResults = 8
                }
            };
            settings.InitializeDirectories();

            LoggingModule logging = SilentLogging();
            return new CodeIndexService(logging, testDb.Driver, settings, new GitService(logging));
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
