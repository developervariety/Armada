namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
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
    /// Broad coverage for the dock boundary pre-commit hook that complements the single-pattern
    /// regression in <see cref="DockBoundaryHookExecutionTests"/>. Drives a real temp git repo
    /// through <see cref="DockService.ProvisionAsync"/> so the actual <c>.armada/boundary.patterns</c>
    /// and <c>.armada/boundary.json</c> are materialized, installs the production hook, and executes
    /// it via <c>sh</c> to prove:
    /// <list type="bullet">
    /// <item>all six CORE_RULE_5 built-in secret patterns block through the real extract+grep path;</item>
    /// <item>a metachar private identifier blocks on a public vessel but passes on a non-public one;</item>
    /// <item>both config files are registered in <c>.git/info/exclude</c> and <c>boundary.json</c> keeps its shape;</item>
    /// <item>a clean staged diff is allowed (exit 0).</item>
    /// </list>
    /// Note: the hook feeds raw patterns to GNU <c>grep -qE</c>, which honours the <c>\s \w \b</c>
    /// extensions but NOT <c>\d</c>; the private-identifier sample therefore uses a <c>\b ... [0-9]</c>
    /// metachar form that the real hook can match. Tests skip cleanly when git or sh is unavailable.
    /// </summary>
    public sealed class DockBoundaryHookCoverageTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Dock Boundary Hook Coverage";

        #endregion

        #region Private-Members

        // A metachar-bearing private identifier: \b word boundaries (a backslash escape that the
        // pre-fix JSON-escaping path would have mangled to \\b) plus a [0-9] digit class that
        // GNU grep -qE actually supports (unlike \d). Value bytes are arbitrary and never asserted.
        private const string _PrivateIdentifierPattern = @"\bACME-[0-9]{4,}\b";
        private const string _PrivateIdentifierLine = "internal cross-ref ACME-4821 noted here";

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("All six built-in CORE_RULE_5 secret patterns block via the real pre-commit hook", async () =>
            {
                if (!IsGitAvailable()) { Console.WriteLine("  [SKIP] git not on PATH; secret-pattern hook test skipped"); return; }
                string? shPath = FindShPath();
                if (shPath == null) { Console.WriteLine("  [SKIP] sh not found; secret-pattern hook test skipped"); return; }

                string tempRoot = NewTempRoot("armada-hookcov-secrets");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ProvisionedDock provisioned = await ProvisionDockAsync(testDb, tempRoot, "secrets-vessel",
                            "https://github.com/test/repo.git", null, null).ConfigureAwait(false);
                        string worktreePath = provisioned.WorktreePath;

                        // Each sample is crafted to match exactly one CORE_RULE_5 pattern, so a block
                        // proves that specific raw pattern survived into boundary.patterns and matched.
                        List<SecretSample> samples = BuildSecretSamples();
                        foreach (SecretSample sample in samples)
                        {
                            string secretFile = Path.Combine(worktreePath, "secret.txt");
                            await File.WriteAllTextAsync(secretFile, sample.Content + "\n").ConfigureAwait(false);
                            await RunGitAsync(worktreePath, "add", "secret.txt").ConfigureAwait(false);

                            HookRun run = await RunHookAsync(shPath, provisioned.HookPath, worktreePath).ConfigureAwait(false);

                            Assert(run.ExitCode != 0,
                                "Hook must block secret sample '" + sample.Label + "' (ExitCode=" + run.ExitCode + ", stderr=" + run.Stderr + ")");
                            AssertContains("BLOCKED:", run.Stderr,
                                "Hook must emit BLOCKED: for secret sample '" + sample.Label + "' (stderr=" + run.Stderr + ")");
                            AssertContains("secret material", run.Stderr,
                                "Block message must name secret material for sample '" + sample.Label + "'");
                            Assert(!run.Stderr.Contains(sample.Marker),
                                "Hook must never print secret bytes for sample '" + sample.Label + "' (CORE RULE 4)");

                            // Unstage so the next sample is evaluated in isolation.
                            await RunGitAsync(worktreePath, "reset").ConfigureAwait(false);
                        }
                    }
                }
                finally { SafeDeleteDirectory(tempRoot); }
            }).ConfigureAwait(false);

            await RunTest("Public vessel: metachar private identifier is blocked by the hook", async () =>
            {
                if (!IsGitAvailable()) { Console.WriteLine("  [SKIP] git not on PATH; public-privid hook test skipped"); return; }
                string? shPath = FindShPath();
                if (shPath == null) { Console.WriteLine("  [SKIP] sh not found; public-privid hook test skipped"); return; }

                string tempRoot = NewTempRoot("armada-hookcov-pubprivid");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        // Public classification: PublicRepoPatterns matches the vessel RepoUrl substring.
                        ProvisionedDock provisioned = await ProvisionDockAsync(testDb, tempRoot, "pub-privid-vessel",
                            "https://github.com/test/repo.git", "github.com/test/repo", _PrivateIdentifierPattern).ConfigureAwait(false);
                        string worktreePath = provisioned.WorktreePath;

                        string patternsContent = await File.ReadAllTextAsync(
                            Path.Combine(worktreePath, ".armada", "boundary.patterns")).ConfigureAwait(false);
                        AssertContains("# privateIdentifiers", patternsContent, "boundary.patterns must carry the private-id header");
                        AssertContains(_PrivateIdentifierPattern, patternsContent,
                            "Public vessel boundary.patterns must include the raw private-identifier pattern");

                        string idFile = Path.Combine(worktreePath, "notes.txt");
                        await File.WriteAllTextAsync(idFile, _PrivateIdentifierLine + "\n").ConfigureAwait(false);
                        await RunGitAsync(worktreePath, "add", "notes.txt").ConfigureAwait(false);

                        HookRun run = await RunHookAsync(shPath, provisioned.HookPath, worktreePath).ConfigureAwait(false);

                        Assert(run.ExitCode != 0,
                            "Hook must block a private identifier on a public vessel (ExitCode=" + run.ExitCode + ", stderr=" + run.Stderr + ")");
                        AssertContains("BLOCKED:", run.Stderr, "Hook must emit BLOCKED: for the private identifier (stderr=" + run.Stderr + ")");
                        AssertContains("private identifier", run.Stderr,
                            "Block message must name the private identifier category (stderr=" + run.Stderr + ")");
                        Assert(!run.Stderr.Contains("ACME-4821"),
                            "Hook must never print the private-identifier value (CORE RULE 4)");
                    }
                }
                finally { SafeDeleteDirectory(tempRoot); }
            }).ConfigureAwait(false);

            await RunTest("Non-public vessel: same private identifier passes (public gating)", async () =>
            {
                if (!IsGitAvailable()) { Console.WriteLine("  [SKIP] git not on PATH; non-public-privid hook test skipped"); return; }
                string? shPath = FindShPath();
                if (shPath == null) { Console.WriteLine("  [SKIP] sh not found; non-public-privid hook test skipped"); return; }

                string tempRoot = NewTempRoot("armada-hookcov-nonpubprivid");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        // Private identifier configured, but PublicRepoPatterns does NOT match this vessel,
                        // so BuildBoundaryConfig must omit private identifiers and the hook must not block.
                        ProvisionedDock provisioned = await ProvisionDockAsync(testDb, tempRoot, "private-vessel",
                            "https://github.com/test/repo.git", "github.com/some-other-org", _PrivateIdentifierPattern).ConfigureAwait(false);
                        string worktreePath = provisioned.WorktreePath;

                        string patternsContent = await File.ReadAllTextAsync(
                            Path.Combine(worktreePath, ".armada", "boundary.patterns")).ConfigureAwait(false);
                        Assert(!patternsContent.Contains(_PrivateIdentifierPattern),
                            "Non-public vessel boundary.patterns must NOT include the private-identifier pattern");

                        string idFile = Path.Combine(worktreePath, "notes.txt");
                        await File.WriteAllTextAsync(idFile, _PrivateIdentifierLine + "\n").ConfigureAwait(false);
                        await RunGitAsync(worktreePath, "add", "notes.txt").ConfigureAwait(false);

                        HookRun run = await RunHookAsync(shPath, provisioned.HookPath, worktreePath).ConfigureAwait(false);

                        AssertEqual(0, run.ExitCode,
                            "Hook must allow a private identifier on a non-public vessel (stderr=" + run.Stderr + ")");
                        Assert(!run.Stderr.Contains("BLOCKED:"),
                            "Hook must not emit BLOCKED: for a non-public vessel (stderr=" + run.Stderr + ")");
                    }
                }
                finally { SafeDeleteDirectory(tempRoot); }
            }).ConfigureAwait(false);

            await RunTest("Both config files registered in info/exclude and boundary.json keeps its shape", async () =>
            {
                if (!IsGitAvailable()) { Console.WriteLine("  [SKIP] git not on PATH; config-surface test skipped"); return; }

                string tempRoot = NewTempRoot("armada-hookcov-config");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        // Public so all three boundary.json sections (paths, secrets, private ids) are populated.
                        ProvisionedDock provisioned = await ProvisionDockAsync(testDb, tempRoot, "config-vessel",
                            "https://github.com/test/repo.git", "github.com/test/repo", _PrivateIdentifierPattern).ConfigureAwait(false);
                        string worktreePath = provisioned.WorktreePath;

                        // Both sibling files must be registered in git info/exclude so a captain cannot commit them.
                        string excludePath = Path.Combine(worktreePath, ".git", "info", "exclude");
                        Assert(File.Exists(excludePath), "git info/exclude must exist after provisioning");
                        string excludeContent = await File.ReadAllTextAsync(excludePath).ConfigureAwait(false);
                        AssertContains(".armada/boundary.json", excludeContent, "boundary.json must be registered in info/exclude");
                        AssertContains(".armada/boundary.patterns", excludeContent, "boundary.patterns must be registered in info/exclude");

                        // boundary.json must stay byte-compatible in SHAPE for server consumers: camelCase keys,
                        // all six built-in secret patterns, built-in protected paths, and the public private id.
                        string jsonPath = Path.Combine(worktreePath, ".armada", "boundary.json");
                        Assert(File.Exists(jsonPath), "boundary.json must be written");
                        string json = await File.ReadAllTextAsync(jsonPath).ConfigureAwait(false);
                        AssertContains("\"protectedPaths\"", json, "boundary.json must keep the protectedPaths key");
                        AssertContains("\"secretPatterns\"", json, "boundary.json must keep the secretPatterns key");
                        AssertContains("\"privateIdentifiers\"", json, "boundary.json must keep the privateIdentifiers key");

                        DockBoundaryConfig? config = JsonSerializer.Deserialize<DockBoundaryConfig>(json);
                        AssertNotNull(config, "boundary.json must deserialize to DockBoundaryConfig for server consumers");
                        AssertEqual(ConventionChecker.BuiltInSecretPatternStrings.Count, config!.SecretPatterns.Count,
                            "boundary.json must carry every built-in CORE_RULE_5 secret pattern");
                        foreach (string builtIn in ConventionChecker.BuiltInSecretPatternStrings)
                            Assert(config.SecretPatterns.Contains(builtIn),
                                "boundary.json secretPatterns must include built-in pattern: " + builtIn);
                        Assert(config.ProtectedPaths.Contains("**/CLAUDE.md"),
                            "boundary.json protectedPaths must include the built-in **/CLAUDE.md guard");
                        Assert(config.PrivateIdentifiers.Contains(_PrivateIdentifierPattern),
                            "Public vessel boundary.json must include the configured private identifier");
                    }
                }
                finally { SafeDeleteDirectory(tempRoot); }
            }).ConfigureAwait(false);

            await RunTest("Clean staged diff passes the pre-commit hook", async () =>
            {
                if (!IsGitAvailable()) { Console.WriteLine("  [SKIP] git not on PATH; clean-diff hook test skipped"); return; }
                string? shPath = FindShPath();
                if (shPath == null) { Console.WriteLine("  [SKIP] sh not found; clean-diff hook test skipped"); return; }

                string tempRoot = NewTempRoot("armada-hookcov-clean");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ProvisionedDock provisioned = await ProvisionDockAsync(testDb, tempRoot, "clean-vessel",
                            "https://github.com/test/repo.git", null, null).ConfigureAwait(false);
                        string worktreePath = provisioned.WorktreePath;

                        // Benign filename (not a protected path) and benign content (matches no secret pattern).
                        string benignFile = Path.Combine(worktreePath, "notes.txt");
                        await File.WriteAllTextAsync(benignFile, "just a normal config value = 42\n").ConfigureAwait(false);
                        await RunGitAsync(worktreePath, "add", "notes.txt").ConfigureAwait(false);

                        HookRun run = await RunHookAsync(shPath, provisioned.HookPath, worktreePath).ConfigureAwait(false);

                        AssertEqual(0, run.ExitCode, "Hook must allow a clean staged diff (stderr=" + run.Stderr + ")");
                        Assert(!run.Stderr.Contains("BLOCKED:"), "Hook must not emit BLOCKED: for a clean diff (stderr=" + run.Stderr + ")");
                    }
                }
                finally { SafeDeleteDirectory(tempRoot); }
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Provision a dock with a real git worktree so the production boundary files and hooks are
        /// materialized. When <paramref name="publicPattern"/> and <paramref name="privateIdentifier"/>
        /// are supplied, the vessel is configured for public private-identifier scanning.
        /// </summary>
        private static async Task<ProvisionedDock> ProvisionDockAsync(
            TestDatabase testDb,
            string tempRoot,
            string vesselName,
            string repoUrl,
            string? publicPattern,
            string? privateIdentifier)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(tempRoot, "docks");
            settings.ReposDirectory = Path.Combine(tempRoot, "repos");
            Directory.CreateDirectory(settings.DocksDirectory);
            Directory.CreateDirectory(settings.ReposDirectory);

            DockBoundarySettings boundary = new DockBoundarySettings
            {
                SecretScanEnabled = true,
                PrivateIdentifierScanEnabled = true,
                PublicRepoPatterns = new List<string>(),
                PrivateIdentifiers = new List<DockBoundaryPrivateIdentifierEntry>()
            };
            if (!String.IsNullOrEmpty(publicPattern)) boundary.PublicRepoPatterns.Add(publicPattern);
            if (!String.IsNullOrEmpty(privateIdentifier))
                boundary.PrivateIdentifiers.Add(new DockBoundaryPrivateIdentifierEntry { Label = "acme-ref", Pattern = privateIdentifier });
            settings.DockBoundary = boundary;

            string repoDir = Path.Combine(settings.ReposDirectory, vesselName + ".git");
            Directory.CreateDirectory(repoDir);

            CoverageWorktreeGitStub gitService = new CoverageWorktreeGitStub();
            DockService dockService = new DockService(logging, testDb.Driver, settings, gitService);

            Vessel vessel = new Vessel(vesselName, repoUrl);
            vessel.LocalPath = repoDir;
            vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Captain captain = new Captain(vesselName + "-captain");
            captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

            Dock? dock = await dockService.ProvisionAsync(vessel, captain,
                "armada/hookcov/msn_" + vesselName, "msn_" + vesselName).ConfigureAwait(false);
            if (dock == null) throw new InvalidOperationException("ProvisionAsync returned null dock for " + vesselName);

            ProvisionedDock result = new ProvisionedDock();
            result.WorktreePath = dock.WorktreePath!;
            result.HookPath = Path.Combine(repoDir, "hooks", "pre-commit");
            if (!File.Exists(result.HookPath))
                throw new InvalidOperationException("pre-commit hook was not installed at " + result.HookPath);
            return result;
        }

        /// <summary>Build one staged-content sample per CORE_RULE_5 built-in secret pattern.</summary>
        private static List<SecretSample> BuildSecretSamples()
        {
            List<SecretSample> samples = new List<SecretSample>();
            samples.Add(new SecretSample("private_key", "-----BEGIN RSA PRIVATE KEY-----", "RSA PRIVATE"));
            samples.Add(new SecretSample("base64_chunk",
                "token = \"QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5\"", "QUJDREVGR0hJSktMTU5"));
            samples.Add(new SecretSample("password_literal", "password = \"SuperSecret1\"", "SuperSecret1"));
            samples.Add(new SecretSample("apikey_literal", "api_key = \"ABCDEFGHIJKLMNOP01\"", "ABCDEFGHIJKLMNOP01"));
            samples.Add(new SecretSample("bearer_literal", "authorization: bearer abcdefghij0123456789XY", "abcdefghij0123456789XY"));
            samples.Add(new SecretSample("seed_literal", "seed = \"abcd1234efgh\"", "abcd1234efgh"));
            return samples;
        }

        private static string NewTempRoot(string prefix)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            return tempRoot;
        }

        /// <summary>Execute an installed hook script through sh and capture its exit code and streams.</summary>
        private static async Task<HookRun> RunHookAsync(string shPath, string hookPath, string worktreePath)
        {
            ProcessStartInfo hookSi = new ProcessStartInfo
            {
                FileName = shPath,
                WorkingDirectory = worktreePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            hookSi.ArgumentList.Add(hookPath.Replace('\\', '/'));

            using (Process hookProc = new Process { StartInfo = hookSi })
            {
                hookProc.Start();
                string stdout = await hookProc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await hookProc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await hookProc.WaitForExitAsync().ConfigureAwait(false);

                HookRun run = new HookRun();
                run.ExitCode = hookProc.ExitCode;
                run.Stdout = stdout;
                run.Stderr = stderr;
                return run;
            }
        }

        private static bool IsGitAvailable()
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(si)!)
                {
                    p.WaitForExit(3000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Locate sh from Git for Windows or the system PATH. Returns null when sh is unavailable
        /// so tests can skip cleanly rather than false-failing on minimal environments.
        /// </summary>
        private static string? FindShPath()
        {
            string[] candidates = new string[]
            {
                @"C:\Program Files\Git\bin\sh.exe",
                @"C:\Program Files (x86)\Git\bin\sh.exe",
                @"C:\Git\bin\sh.exe",
                @"C:\git\bin\sh.exe",
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            try
            {
                ProcessStartInfo si = new ProcessStartInfo
                {
                    FileName = "sh",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(si)!)
                {
                    p.WaitForExit(3000);
                    if (p.ExitCode == 0) return "sh";
                }
            }
            catch { }

            return null;
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
                startInfo.ArgumentList.Add(arg);

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + stderr.Trim());

                return stdout.Trim();
            }
        }

        private static void SafeDeleteDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }

        #endregion

        #region Private-Types

        /// <summary>A staged-content sample targeting a single built-in secret pattern.</summary>
        private sealed class SecretSample
        {
            /// <summary>Pattern label used in assertion messages.</summary>
            public string Label { get; }

            /// <summary>The line staged into the worktree to trigger the pattern.</summary>
            public string Content { get; }

            /// <summary>The sensitive substring that must never appear in hook output.</summary>
            public string Marker { get; }

            /// <summary>Create a sample.</summary>
            public SecretSample(string label, string content, string marker)
            {
                Label = label;
                Content = content;
                Marker = marker;
            }
        }

        /// <summary>Captured outcome of one hook execution.</summary>
        private sealed class HookRun
        {
            /// <summary>Process exit code; non-zero means the hook blocked.</summary>
            public int ExitCode { get; set; }

            /// <summary>Captured standard output.</summary>
            public string Stdout { get; set; } = "";

            /// <summary>Captured standard error, where BLOCKED: messages appear.</summary>
            public string Stderr { get; set; } = "";
        }

        /// <summary>Materialized dock paths returned by the provisioning helper.</summary>
        private sealed class ProvisionedDock
        {
            /// <summary>Absolute path to the provisioned worktree.</summary>
            public string WorktreePath { get; set; } = "";

            /// <summary>Absolute path to the installed pre-commit hook.</summary>
            public string HookPath { get; set; } = "";
        }

        /// <summary>
        /// Git service stub that creates a real initialized git repository in the worktree path so
        /// staging commands work when the hook executes. All other members are no-ops or safe defaults.
        /// </summary>
        private sealed class CoverageWorktreeGitStub : IGitService
        {
            /// <inheritdoc />
            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
            {
                Directory.CreateDirectory(localPath);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public async Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", CancellationToken token = default)
            {
                Directory.CreateDirectory(worktreePath);
                await RunGitAsync(worktreePath, "init", "-b", "main").ConfigureAwait(false);
                await RunGitAsync(worktreePath, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await RunGitAsync(worktreePath, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(worktreePath, ".gitkeep"), "init\n").ConfigureAwait(false);
                await RunGitAsync(worktreePath, "add", ".gitkeep").ConfigureAwait(false);
                await RunGitAsync(worktreePath, "commit", "-m", "init").ConfigureAwait(false);
            }

            /// <inheritdoc />
            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult(String.Empty);

            /// <inheritdoc />
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(Directory.Exists(path));

            /// <inheritdoc />
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");

            /// <inheritdoc />
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>(null);

            /// <inheritdoc />
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);

            /// <inheritdoc />
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);

            /// <inheritdoc />
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123hookcov");

            /// <inheritdoc />
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

            /// <inheritdoc />
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);

            /// <inheritdoc />
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(false);

            /// <inheritdoc />
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);

            /// <inheritdoc />
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(Directory.Exists(worktreePath));

            /// <inheritdoc />
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);
        }

        #endregion
    }
}
