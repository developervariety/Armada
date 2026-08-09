namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Verifies non-landing code-index staleness handling: the fleet-wide staleness
    /// summary reads persisted metadata plus one rev-parse per vessel and never clones, and the
    /// staleness sweep schedules a reindex when a vessel's repository HEAD moved outside an Armada
    /// landing (direct push / manual merge / reconciliation).
    /// </summary>
    public sealed class CodeIndexStalenessTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "CodeIndexStaleness";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            if (!IsGitOnPath())
            {
                Console.WriteLine("  SKIP  CodeIndexStalenessTests (git-backed cases) -- git not found on PATH");
                return;
            }

            await RunTest("Summary_ReportsOnlyStaleVessels_FromPersistedMetadata", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();
                GitService git = new GitService(logging);
                CodeIndexService svc = new CodeIndexService(logging, testDb.Driver, settings, git);

                StalenessRepo stale = await CreateRepoWithBareAsync().ConfigureAwait(false);
                StalenessRepo fresh = await CreateRepoWithBareAsync().ConfigureAwait(false);

                try
                {
                    Vessel staleVessel = await CreateIndexedVesselAsync(testDb, settings, stale, "stale-vessel").ConfigureAwait(false);
                    Vessel freshVessel = await CreateIndexedVesselAsync(testDb, settings, fresh, "fresh-vessel").ConfigureAwait(false);

                    // Advance only the stale repo's bare HEAD with a direct push (no Armada landing).
                    stale.SecondCommitSha = await AddCommitAndPushAsync(stale.Source, stale.Bare).ConfigureAwait(false);

                    CodeIndexStalenessSummary summary = await svc.GetStalenessSummaryAsync(CancellationToken.None).ConfigureAwait(false);

                    CodeIndexStaleVessel? staleEntry = summary.StaleVessels.FirstOrDefault(v => v.VesselId == staleVessel.Id);
                    AssertNotNull(staleEntry, "The vessel whose bare HEAD advanced must be reported stale.");
                    AssertEqual(stale.FirstCommitSha, staleEntry!.IndexedCommitSha, "The summary must report the indexed commit.");
                    AssertEqual(stale.SecondCommitSha, staleEntry.CurrentCommitSha, "The summary must report the current commit.");
                    AssertTrue(
                        !summary.StaleVessels.Any(v => v.VesselId == freshVessel.Id),
                        "A vessel whose index matches its HEAD must not be reported stale.");
                }
                finally
                {
                    Cleanup(stale.Source, stale.Bare);
                    Cleanup(fresh.Source, fresh.Bare);
                }
            }).ConfigureAwait(false);

            await RunTest("Summary_SkipsVesselWithoutLocalRepository", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();
                GitService git = new GitService(logging);
                CodeIndexService svc = new CodeIndexService(logging, testDb.Driver, settings, git);

                string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, "no-repo-vessel");
                Directory.CreateDirectory(indexDir);
                await File.WriteAllTextAsync(
                    Path.Combine(indexDir, "metadata.json"),
                    "{\"indexedCommitSha\":\"abc123\"}").ConfigureAwait(false);

                Vessel vessel = new Vessel("no-repo-vessel", "https://github.com/test/repo.git");
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                CodeIndexStalenessSummary summary = await svc.GetStalenessSummaryAsync(CancellationToken.None).ConfigureAwait(false);
                AssertTrue(
                    !summary.StaleVessels.Any(v => v.VesselId == vessel.Id),
                    "A vessel with persisted metadata but no local repository must be skipped, not diagnosed.");
                AssertEqual(0, summary.StaleVesselCount, "No stale vessels without a local repository.");
            }).ConfigureAwait(false);

            await RunTest("Sweep_SchedulesReindex_ForHeadChangeOutsideLanding", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();
                // Never actually update during the test: push the debounce far out so the scheduled
                // worker only delays and the assertion covers the scheduling decision.
                settings.CodeIndex.PostLandRefreshDebounceSeconds = 3600;
                GitService git = new GitService(logging);
                CodeIndexService svc = new CodeIndexService(logging, testDb.Driver, settings, git);

                StalenessRepo stale = await CreateRepoWithBareAsync().ConfigureAwait(false);
                StalenessRepo fresh = await CreateRepoWithBareAsync().ConfigureAwait(false);

                try
                {
                    await CreateIndexedVesselAsync(testDb, settings, stale, "sweep-stale").ConfigureAwait(false);
                    await CreateIndexedVesselAsync(testDb, settings, fresh, "sweep-fresh").ConfigureAwait(false);

                    await AddCommitAndPushAsync(stale.Source, stale.Bare).ConfigureAwait(false);
                    int scheduled = await svc.SweepStalenessAsync(CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(1, scheduled, "Exactly the vessel whose HEAD moved outside a landing must be scheduled for reindex.");
                }
                finally
                {
                    Cleanup(stale.Source, stale.Bare);
                    Cleanup(fresh.Source, fresh.Bare);
                }
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DataDirectory = Path.Combine(Path.GetTempPath(), "armada-staleness-" + Guid.NewGuid().ToString("N"));
            settings.DocksDirectory = Path.Combine(settings.DataDirectory, "docks");
            settings.ReposDirectory = Path.Combine(settings.DataDirectory, "repos");
            settings.LogDirectory = Path.Combine(settings.DataDirectory, "logs");
            settings.CodeIndex.IndexDirectory = Path.Combine(settings.DataDirectory, "code-index");
            return settings;
        }

        private sealed class StalenessRepo
        {
            public string Source { get; set; } = String.Empty;

            public string Bare { get; set; } = String.Empty;

            public string FirstCommitSha { get; set; } = String.Empty;

            public string SecondCommitSha { get; set; } = String.Empty;
        }

        private static async Task<StalenessRepo> CreateRepoWithBareAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "armada-staleness-repo-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(root, "source");
            string bare = Path.Combine(root, "bare.git");
            Directory.CreateDirectory(source);

            await RunGitAsync(source, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(source, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(source, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(source, "code.txt"), "line one\n").ConfigureAwait(false);
            await RunGitAsync(source, "add", "code.txt").ConfigureAwait(false);
            await RunGitAsync(source, "commit", "-m", "first").ConfigureAwait(false);
            string first = await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false);

            await RunGitAsync(source, "clone", "--bare", source, bare).ConfigureAwait(false);

            return new StalenessRepo
            {
                Source = source,
                Bare = bare,
                FirstCommitSha = first,
                SecondCommitSha = first
            };
        }

        /// <summary>
        /// Add a second commit to the source repo and push it straight into the bare repo (a direct
        /// push that never went through an Armada landing). Returns the new bare HEAD SHA.
        /// </summary>
        private static async Task<string> AddCommitAndPushAsync(string source, string bare)
        {
            await File.WriteAllTextAsync(Path.Combine(source, "code.txt"), "line two\n").ConfigureAwait(false);
            await RunGitAsync(source, "add", "code.txt").ConfigureAwait(false);
            await RunGitAsync(source, "commit", "-m", "second").ConfigureAwait(false);
            await RunGitAsync(source, "push", bare, "main").ConfigureAwait(false);
            return await RunGitAsync(bare, "rev-parse", "main").ConfigureAwait(false);
        }

        private static async Task<Vessel> CreateIndexedVesselAsync(TestDatabase testDb, ArmadaSettings settings, StalenessRepo repo, string name)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/repo.git");
            vessel.Name = name;
            vessel.DefaultBranch = "main";
            vessel.LocalPath = repo.Bare;
            vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            string indexDir = Path.Combine(settings.CodeIndex.IndexDirectory, vessel.Id);
            Directory.CreateDirectory(indexDir);
            await File.WriteAllTextAsync(
                Path.Combine(indexDir, "metadata.json"),
                "{\"indexedCommitSha\":\"" + repo.FirstCommitSha + "\"}").ConfigureAwait(false);
            return vessel;
        }

        private static void Cleanup(string source, string bare)
        {
            foreach (string path in new[] { source, bare })
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                }
                catch
                {
                }
            }
            string? root = Path.GetDirectoryName(source);
            if (!String.IsNullOrEmpty(root))
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch
                {
                }
            }
        }

        private static bool IsGitOnPath()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(info)!)
                {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
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

                return stdout.Trim();
            }
        }

        #endregion
    }
}
