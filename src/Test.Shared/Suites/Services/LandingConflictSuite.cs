namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for merge-conflict capture in the landing path: the real-git
    /// <see cref="GitService.GetConflictedFilesAsync"/> utility and its use by
    /// <see cref="LandingService.RetryLandingAsync"/> to record the conflicting file list on the
    /// mission when a retry fails. Positive cases assert conflict detection and capture; negative cases
    /// assert clean-tree emptiness and missing-mission handling.
    /// </summary>
    public sealed class LandingConflictSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.LandingConflict";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Landing Conflict suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("git_detects_conflicted_files", "Git DetectsConflictedFiles", TestTags.Positive, async () =>
            {
                string dir = CreateTempDir();
                try
                {
                    Git(dir, "init", "-b", "main");
                    Git(dir, "config", "user.email", "test@armada.local");
                    Git(dir, "config", "user.name", "Armada Test");
                    string file = Path.Combine(dir, "conflict.txt");
                    await File.WriteAllTextAsync(file, "base\n");
                    Git(dir, "add", "conflict.txt");
                    Git(dir, "commit", "-m", "base");

                    Git(dir, "checkout", "-b", "feature");
                    await File.WriteAllTextAsync(file, "feature change\n");
                    Git(dir, "commit", "-am", "feature");

                    Git(dir, "checkout", "main");
                    await File.WriteAllTextAsync(file, "main change\n");
                    Git(dir, "commit", "-am", "main");

                    Git(dir, "merge", "feature"); // conflicts, leaves unmerged path

                    GitService git = new GitService(CreateLogging());
                    IReadOnlyList<string> conflicts = await git.GetConflictedFilesAsync(dir);
                    AssertTrue(conflicts.Count >= 1, "Expected at least one conflicted file");
                    AssertContains("conflict.txt", string.Join(",", conflicts));
                }
                finally
                {
                    TryDelete(dir);
                }
            }));

            cases.Add(CaseAsync("git_clean_tree_has_no_conflicts", "Git CleanTree NoConflicts", TestTags.Negative, async () =>
            {
                string dir = TestGitRepoHelper.CreateWorkingRepoCopy();
                try
                {
                    GitService git = new GitService(CreateLogging());
                    IReadOnlyList<string> conflicts = await git.GetConflictedFilesAsync(dir);
                    AssertEqual(0, conflicts.Count);
                }
                finally
                {
                    TryDelete(dir);
                }
            }));

            cases.Add(CaseAsync("retry_landing_captures_conflicts", "RetryLanding CapturesConflicts", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    git.ExistingBranches.Add("feature/x");
                    git.ConflictedFilesResult = new List<string> { "src/Foo.cs", "src/Bar.cs" };

                    Vessel vessel = new Vessel("Conflict Vessel", "https://github.com/test/conflict");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_landing_" + Guid.NewGuid().ToString("N"));
                    await db.Vessels.CreateAsync(vessel);

                    Mission mission = new Mission("Retry me")
                    {
                        Status = MissionStatusEnum.LandingFailed,
                        VesselId = vessel.Id,
                        BranchName = "feature/x"
                    };
                    await db.Missions.CreateAsync(mission);

                    LandingService landing = new LandingService(CreateLogging(), db, CreateSettings(), git);
                    landing.OnPerformLanding = (m, d) => throw new InvalidOperationException("merge failed");

                    bool ok = await landing.RetryLandingAsync(mission.Id);
                    AssertFalse(ok, "Retry should report failure");

                    Mission? reread = await db.Missions.ReadAsync(mission.Id);
                    AssertNotNull(reread);
                    AssertEqual(MissionStatusEnum.LandingFailed, reread!.Status);
                    AssertContains("src/Foo.cs", reread.FailureReason ?? "");
                    AssertContains("src/Bar.cs", reread.FailureReason ?? "");
                }
            }));

            cases.Add(CaseAsync("retry_landing_missing_mission_returns_false", "RetryLanding MissingMission ReturnsFalse", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LandingService landing = new LandingService(CreateLogging(), testDb.Driver, CreateSettings(), new StubGitService());
                    AssertFalse(await landing.RetryLandingAsync("msn_missing"));
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Landing Conflict",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static void Git(string dir, params string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process process = Process.Start(psi)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit(30000);
            Task.WaitAll(new Task[] { stdout, stderr }, 5000);
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "armada_conflict_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
