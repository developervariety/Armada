namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// End-to-end coverage for the graph sidecar emission added to <see cref="CodeIndexService.UpdateAsync"/>.
    /// Verifies that <c>symbols.jsonl</c> and <c>edges.jsonl</c> are produced next to <c>chunks.jsonl</c>
    /// at index update time, that JSONL framing is preserved, and that supported polyglot files contribute.
    /// </summary>
    public class CodeIndexServiceGraphSidecarTests : TestSuite
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _IndexJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        #endregion

        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Code Index Service Graph Sidecar";

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("UpdateAsync_emits_symbols_and_edges_jsonl_sidecars_next_to_chunks", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-graph-sidecar-emit-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        string symbolsPath = Path.Combine(status.IndexDirectory, "symbols.jsonl");
                        string edgesPath = Path.Combine(status.IndexDirectory, "edges.jsonl");
                        AssertTrue(File.Exists(symbolsPath), "symbols.jsonl must exist next to chunks.jsonl");
                        AssertTrue(File.Exists(edgesPath), "edges.jsonl must exist next to chunks.jsonl");

                        string symbolsContent = await File.ReadAllTextAsync(symbolsPath).ConfigureAwait(false);
                        string edgesContent = await File.ReadAllTextAsync(edgesPath).ConfigureAwait(false);
                        AssertContains("CodeIndexTarget", symbolsContent, "class symbol from fixture appears in symbols.jsonl");
                        AssertContains("\"kind\":\"Class\"", symbolsContent, "Class symbol kind serializes by name");
                        AssertContains("\"kind\":\"Contains\"", edgesContent, "Contains edge kind serializes by name");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_sidecars_are_valid_jsonl_one_object_per_line", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-graph-sidecar-jsonl-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        string symbolsPath = Path.Combine(status.IndexDirectory, "symbols.jsonl");
                        string[] symbolLines = await File.ReadAllLinesAsync(symbolsPath).ConfigureAwait(false);
                        AssertTrue(symbolLines.Length >= 1, "symbols.jsonl must contain at least one line");

                        int parsedSymbols = 0;
                        foreach (string line in symbolLines)
                        {
                            if (String.IsNullOrWhiteSpace(line)) continue;
                            CodeGraphSymbolRecord? sym = JsonSerializer.Deserialize<CodeGraphSymbolRecord>(line, _IndexJsonOptions);
                            AssertNotNull(sym, "each symbols.jsonl line must deserialize to CodeGraphSymbolRecord");
                            AssertEqual(vessel.Id, sym!.VesselId, "vessel id round-trips on symbol");
                            AssertEqual(repository.CommitSha, sym.CommitSha, "commit sha round-trips on symbol");
                            parsedSymbols++;
                        }

                        AssertTrue(parsedSymbols >= 1, "at least one symbol round-tripped from sidecar");

                        string edgesPath = Path.Combine(status.IndexDirectory, "edges.jsonl");
                        string[] edgeLines = await File.ReadAllLinesAsync(edgesPath).ConfigureAwait(false);
                        foreach (string line in edgeLines)
                        {
                            if (String.IsNullOrWhiteSpace(line)) continue;
                            CodeGraphEdgeRecord? edge = JsonSerializer.Deserialize<CodeGraphEdgeRecord>(line, _IndexJsonOptions);
                            AssertNotNull(edge, "each edges.jsonl line must deserialize to CodeGraphEdgeRecord");
                            AssertEqual(vessel.Id, edge!.VesselId, "vessel id round-trips on edge");
                            AssertEqual(repository.CommitSha, edge.CommitSha, "commit sha round-trips on edge");
                        }
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_sidecars_include_supported_polyglot_paths_not_markdown", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-graph-sidecar-polyglot-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        string symbolsPath = Path.Combine(status.IndexDirectory, "symbols.jsonl");
                        string[] symbolLines = await File.ReadAllLinesAsync(symbolsPath).ConfigureAwait(false);

                        bool sawCSharp = false;
                        bool sawTypeScript = false;
                        bool sawPython = false;
                        foreach (string line in symbolLines)
                        {
                            if (String.IsNullOrWhiteSpace(line)) continue;
                            CodeGraphSymbolRecord? sym = JsonSerializer.Deserialize<CodeGraphSymbolRecord>(line, _IndexJsonOptions);
                            AssertNotNull(sym, "each line deserializes");
                            AssertFalse(sym!.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase),
                                "markdown files must not contribute graph symbols");
                            sawCSharp |= sym.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
                            sawTypeScript |= sym.Path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);
                            sawPython |= sym.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
                        }

                        AssertTrue(sawCSharp, "C# file should contribute graph symbols");
                        AssertTrue(sawTypeScript, "TypeScript file should contribute graph symbols");
                        AssertTrue(sawPython, "Python file should contribute graph symbols");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("UpdateAsync_with_zero_graph_supported_files_still_writes_empty_sidecars", async () =>
            {
                TestRepository repository = await CreateMarkdownOnlyRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-graph-sidecar-empty-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        CodeIndexStatus status = await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        string symbolsPath = Path.Combine(status.IndexDirectory, "symbols.jsonl");
                        string edgesPath = Path.Combine(status.IndexDirectory, "edges.jsonl");
                        AssertTrue(File.Exists(symbolsPath), "symbols.jsonl must be created even with no .cs files");
                        AssertTrue(File.Exists(edgesPath), "edges.jsonl must be created even with no .cs files");

                        string symbolsContent = await File.ReadAllTextAsync(symbolsPath).ConfigureAwait(false);
                        string edgesContent = await File.ReadAllTextAsync(edgesPath).ConfigureAwait(false);
                        AssertEqual(0, symbolsContent.Length, "symbols.jsonl must be empty when no graph-supported files were indexed");
                        AssertEqual(0, edgesContent.Length, "edges.jsonl must be empty when no graph-supported files were indexed");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });

            await RunTest("SearchAsync_boosts_graph_endpoint_file_over_lexical_noise", async () =>
            {
                TestRepository repository = await CreateRepositoryAsync().ConfigureAwait(false);
                string dataRoot = NewTempDirectory("armada-code-graph-sidecar-search-boost-");

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Vessel vessel = await CreateVesselAsync(testDb, repository.Path).ConfigureAwait(false);
                        CodeIndexService service = CreateService(testDb, dataRoot);

                        await service.UpdateAsync(vessel.Id).ConfigureAwait(false);

                        CodeSearchResponse response = await service.SearchAsync(new CodeSearchRequest
                        {
                            VesselId = vessel.Id,
                            Query = "health",
                            Limit = 3,
                            IncludeContent = false
                        }).ConfigureAwait(false);

                        AssertTrue(response.Results.Count >= 2, "fixture should return both endpoint and lexical-noise results");
                        AssertEqual("app/api.py", response.Results[0].Record.Path, "graph endpoint boost should rank route file first");
                        AssertTrue(response.Results.Any(r => r.Record.Path == "docs/usage.md"), "lexical noise file should still be present");
                    }
                }
                finally
                {
                    TryDeleteDirectory(repository.Root);
                    TryDeleteDirectory(dataRoot);
                }
            });
        }

        #endregion

        #region Private-Methods

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

        private static CodeIndexService CreateService(TestDatabase testDb, string dataRoot)
        {
            ArmadaSettings settings = BuildSettings(dataRoot);
            LoggingModule logging = SilentLogging();
            return new CodeIndexService(logging, testDb.Driver, settings, new GitService(logging), null, null);
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string repositoryPath)
        {
            Vessel vessel = new Vessel
            {
                Name = "code-index-graph-sidecar-vessel-" + Guid.NewGuid().ToString("N"),
                RepoUrl = repositoryPath,
                WorkingDirectory = repositoryPath,
                DefaultBranch = "main"
            };

            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<TestRepository> CreateRepositoryAsync()
        {
            string root = NewTempDirectory("armada-code-graph-repo-");
            string repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);

            try
            {
                await RunGitAsync(repo, "init", "-b", "main").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                Directory.CreateDirectory(Path.Combine(repo, "src"));
                Directory.CreateDirectory(Path.Combine(repo, "app"));
                Directory.CreateDirectory(Path.Combine(repo, "docs"));

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "src", "CodeIndexTarget.cs"),
                    "namespace Sample\n{\n    public class CodeIndexTarget\n    {\n        public string SearchKeyword() => \"dispatch evidence\";\n    }\n}\n").ConfigureAwait(false);

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "src", "Widget.tsx"),
                    "import React from 'react';\n" +
                    "export function Widget() { return <div />; }\n").ConfigureAwait(false);

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "app", "api.py"),
                    "from fastapi import APIRouter\n" +
                    "router = APIRouter()\n" +
                    "@router.get('/health')\n" +
                    "def health():\n" +
                    "    return {'ok': True}\n").ConfigureAwait(false);

                await File.WriteAllTextAsync(
                    Path.Combine(repo, "docs", "usage.md"),
                    "# Usage\n\nhealth health health health health health health health health health health health\n").ConfigureAwait(false);

                await RunGitAsync(repo, "add", ".").ConfigureAwait(false);
                await RunGitAsync(repo, "commit", "-m", "Initial graph fixture").ConfigureAwait(false);
                string commitSha = (await RunGitAsync(repo, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                return new TestRepository(root, repo, commitSha);
            }
            catch
            {
                TryDeleteDirectory(root);
                throw;
            }
        }

        private static async Task<TestRepository> CreateMarkdownOnlyRepositoryAsync()
        {
            string root = NewTempDirectory("armada-code-graph-md-repo-");
            string repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);

            try
            {
                await RunGitAsync(repo, "init", "-b", "main").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await RunGitAsync(repo, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                Directory.CreateDirectory(Path.Combine(repo, "docs"));
                await File.WriteAllTextAsync(
                    Path.Combine(repo, "docs", "README.md"),
                    "# Readme\n\nMarkdown only repo for empty-sidecar test.\n").ConfigureAwait(false);

                await RunGitAsync(repo, "add", ".").ConfigureAwait(false);
                await RunGitAsync(repo, "commit", "-m", "Markdown-only fixture").ConfigureAwait(false);
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
