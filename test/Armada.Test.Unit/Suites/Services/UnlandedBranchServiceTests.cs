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
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the unlanded mission-branch report. Uses real git repositories so the ancestry
    /// check is genuinely exercised rather than stubbed into agreeing with itself.
    /// </summary>
    public class UnlandedBranchServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Unlanded Branch Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Reports a merged branch as landed and an unmerged branch as unlanded", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    string repo = await CreateRepoWithBranchesAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        Vessel vessel = new Vessel("unlanded-vessel", "https://github.com/test/unlanded.git");
                        vessel.LocalPath = repo;
                        vessel.DefaultBranch = "main";
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        UnlandedBranchService service = new UnlandedBranchService(
                            testDb.Driver, new GitService(logging), logging);

                        List<UnlandedBranchReport> reports = await service.BuildReportAsync(vessel.Id).ConfigureAwait(false);

                        AssertEqual(1, reports.Count, "one vessel was requested");
                        UnlandedBranchReport report = reports[0];
                        AssertNull(report.Error, "a readable repository must not report an error");
                        AssertEqual("main", report.DefaultBranch);

                        // Two armada/* branches exist: one merged into main, one not. Only the
                        // unmerged one may be reported, which is what gives this test teeth --
                        // a report that simply listed every mission branch would fail here.
                        AssertEqual(2, report.MissionBranchCount, "both mission branches should be counted");
                        AssertEqual(1, report.UnlandedCount, "only the unmerged branch is unlanded");
                        AssertEqual("armada/claude-1/msn_unlanded001", report.Unlanded[0].BranchName,
                            "the unmerged branch must be the one reported");
                        AssertEqual("msn_unlanded001", report.Unlanded[0].MissionId,
                            "the mission id must be parsed from the branch name");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("Ignores non-mission branches and resolves mission status when the record exists", async () =>
            {
                string rootDir = NewTempDir();
                try
                {
                    string repo = await CreateRepoWithBranchesAsync(rootDir).ConfigureAwait(false);
                    // A non-armada branch that is NOT merged: it must not appear in the report,
                    // because this report is about stranded Armada mission work specifically.
                    await RunGitAsync(repo, "branch", "feature/human-work", "armada/claude-1/msn_unlanded001").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        Vessel vessel = new Vessel("status-vessel", "https://github.com/test/status.git");
                        vessel.LocalPath = repo;
                        vessel.DefaultBranch = "main";
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Mission mission = new Mission("stranded mission", "left behind by a failed landing");
                        mission.Id = "msn_unlanded001";
                        mission.VesselId = vessel.Id;
                        mission.Status = Armada.Core.Enums.MissionStatusEnum.WorkProduced;
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        UnlandedBranchService service = new UnlandedBranchService(
                            testDb.Driver, new GitService(logging), logging);

                        List<UnlandedBranchReport> reports = await service.BuildReportAsync(vessel.Id).ConfigureAwait(false);
                        UnlandedBranchReport report = reports[0];

                        AssertEqual(1, report.UnlandedCount,
                            "a non-armada branch must not be counted as an unlanded mission branch");
                        AssertEqual("WorkProduced", report.Unlanded[0].MissionStatus,
                            "the mission record should be joined so an operator can see the work was real");
                    }
                }
                finally
                {
                    TryDelete(rootDir);
                }
            }).ConfigureAwait(false);

            await RunTest("Reports a vessel with no local path as unmeasured rather than clean", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Vessel vessel = new Vessel("no-path-vessel", "https://github.com/test/nopath.git");
                    vessel.LocalPath = null;
                    vessel.DefaultBranch = "main";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    UnlandedBranchService service = new UnlandedBranchService(
                        testDb.Driver, new GitService(logging), logging);

                    List<UnlandedBranchReport> reports = await service.BuildReportAsync(vessel.Id).ConfigureAwait(false);

                    // The dangerous failure mode for a monitoring report is reporting zero for a
                    // vessel it could not actually measure, which reads as "all clear".
                    AssertNotNull(reports[0].Error, "an unmeasurable vessel must say so");
                    AssertEqual(0, reports[0].UnlandedCount, "no count is claimed for an unmeasurable vessel");
                }
            }).ConfigureAwait(false);

            await RunTest("BuildReportAsync rejects an unknown vessel id with a specific error", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    UnlandedBranchService service = new UnlandedBranchService(
                        testDb.Driver, new GitService(logging), logging);

                    string message = "";
                    try
                    {
                        await service.BuildReportAsync("vsl_nope").ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        message = ex.Message;
                    }

                    AssertContains("vsl_nope", message, "the error must name the vessel that was not found");
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

        private static string NewTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_unlanded_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        /// <summary>
        /// Build a repo on main with two armada mission branches: msn_landed001 merged into main and
        /// msn_unlanded001 left unmerged.
        /// </summary>
        private static async Task<string> CreateRepoWithBranchesAsync(string rootDir)
        {
            string repo = Path.Combine(rootDir, "repo");
            Directory.CreateDirectory(repo);
            await RunGitAsync(repo, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(repo, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(repo, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(repo, "base.txt"), "base\n").ConfigureAwait(false);
            await RunGitAsync(repo, "add", ".").ConfigureAwait(false);
            await RunGitAsync(repo, "commit", "-m", "initial").ConfigureAwait(false);

            // Landed branch: committed then merged into main.
            await RunGitAsync(repo, "checkout", "-b", "armada/claude-1/msn_landed001").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repo, "landed.txt"), "landed\n").ConfigureAwait(false);
            await RunGitAsync(repo, "add", ".").ConfigureAwait(false);
            await RunGitAsync(repo, "commit", "-m", "landed work").ConfigureAwait(false);
            await RunGitAsync(repo, "checkout", "main").ConfigureAwait(false);
            await RunGitAsync(repo, "merge", "--no-edit", "armada/claude-1/msn_landed001").ConfigureAwait(false);

            // Unlanded branch: committed and deliberately never merged.
            await RunGitAsync(repo, "checkout", "-b", "armada/claude-1/msn_unlanded001").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repo, "stranded.txt"), "stranded\n").ConfigureAwait(false);
            await RunGitAsync(repo, "add", ".").ConfigureAwait(false);
            await RunGitAsync(repo, "commit", "-m", "stranded work").ConfigureAwait(false);
            await RunGitAsync(repo, "checkout", "main").ConfigureAwait(false);

            return repo;
        }

        private static async Task RunGitAsync(string workingDir, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);

            using (Process proc = new Process { StartInfo = startInfo })
            {
                proc.Start();
                string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + stderr);
            }
        }

        #endregion
    }
}
